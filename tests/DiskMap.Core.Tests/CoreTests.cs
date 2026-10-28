using System.Buffers.Binary;
using System.Text;
using DiskMap.Core;
using DiskMap.Core.History;
using DiskMap.Core.Layout;
using DiskMap.Core.Model;
using DiskMap.Core.Query;
using DiskMap.Core.Scanning;
using DiskMap.Core.Settings;

namespace DiskMap.Core.Tests;

public class SquarifyTests
{
    [Fact]
    public void Areas_are_proportional_and_inside_bounds()
    {
        double[] weights = [500, 250, 120, 60, 40, 20, 10];
        var bounds = new RectD(10, 20, 800, 500);
        var rects = new RectD[weights.Length];
        Squarify.Layout(weights, bounds, rects);

        double total = weights.Sum();
        for (int i = 0; i < weights.Length; i++)
        {
            Assert.Equal(weights[i] / total * bounds.Area, rects[i].Area, 3);
            Assert.True(rects[i].X >= bounds.X - 1e-6 && rects[i].Right <= bounds.Right + 1e-6);
            Assert.True(rects[i].Y >= bounds.Y - 1e-6 && rects[i].Bottom <= bounds.Bottom + 1e-6);
        }
    }

    [Fact]
    public void Aspect_ratios_stay_reasonable()
    {
        var weights = Enumerable.Range(1, 40).Select(i => 1000.0 / i).ToArray();
        var rects = new RectD[weights.Length];
        Squarify.Layout(weights, new RectD(0, 0, 1000, 600), rects);
        foreach (var r in rects.Take(10))
        {
            Assert.True(Math.Max(r.W / r.H, r.H / r.W) < 4, $"aspect {r}");
        }
    }
}

public class MapLayoutTests
{
    private static DiskNode Folder(string name, params DiskNode[] kids)
    {
        var d = new DiskNode(name, NodeFlags.Directory);
        foreach (var k in kids) d.AddChild(k);
        return d;
    }

    private static DiskNode File(string name, long size) => new(name, NodeFlags.None) { Size = size, FileCount = 1 };

    [Fact]
    public void Big_folders_become_frames_and_tiny_items_merge()
    {
        var tiny = Enumerable.Range(0, 200).Select(i => File($"t{i}.txt", 10)).ToArray();
        var root = Folder("C:\\",
            Folder("Games", File("a.pak", 600_000), File("b.pak", 300_000)),
            Folder("Docs", tiny),
            File("big.iso", 400_000));
        ScanPostProcessor.AggregateSubtree(root);

        var boxes = MapLayout.Build(root, new RectD(0, 0, 800, 600), freeBytes: 100_000);
        Assert.Contains(boxes, b => b.Kind == BoxKind.Frame && b.Node!.Name == "Games");
        Assert.Contains(boxes, b => b.Kind == BoxKind.Free);
        Assert.Contains(boxes, b => b.Kind == BoxKind.Tile && b.Node!.Name == "big.iso");
        var games = boxes.Single(b => b.Node?.Name == "Games");
        foreach (var child in boxes.Where(b => b.Parent == games))
        {
            Assert.True(child.Rect.Y >= games.Rect.Y + 17, "children sit below the title bar");
        }
    }

    [Fact]
    public void A_folder_that_is_almost_all_one_subfolder_shares_its_title()
    {
        var root = Folder("C:\\",
            Folder("Users",
                Folder("alex", File("a.bin", 500_000), File("b.bin", 300_000)),
                File("desktop.ini", 100)),
            File("other.bin", 400_000));
        ScanPostProcessor.AggregateSubtree(root);

        var boxes = MapLayout.Build(root, new RectD(0, 0, 800, 600), freeBytes: 0);
        var frame = boxes.Single(b => b.Kind == BoxKind.Frame);
        Assert.Equal("alex", frame.Node!.Name);
        Assert.Equal(["Users", "alex"], frame.Chain!.Select(n => n.Name));
        Assert.True(frame.Shows(root.Child("Users")!));
        Assert.DoesNotContain(boxes, b => b.Node?.Name == "Users" && b.Kind == BoxKind.Frame);
    }

    [Fact]
    public void No_box_is_drawn_as_a_hairline()
    {
        // A long tail of sizes like a real folder: a few big files, then many small ones.
        var files = Enumerable.Range(1, 300).Select(i => File($"f{i}.bin", 10_000_000 / (i * i) + 2_000)).ToArray();
        var root = Folder("C:\\", Folder("Data", files), File("big.iso", 30_000_000));
        ScanPostProcessor.AggregateSubtree(root);
        foreach (var tiling in new[] { MapTiling.Squarified, MapTiling.Rows })
        {
            var boxes = MapLayout.Build(root, new RectD(0, 0, 900, 600), 0, new MapLayoutOptions { Tiling = tiling });
            Assert.Contains(boxes, b => b.Kind == BoxKind.Rest);
            foreach (var b in boxes.Where(b => b.Kind == BoxKind.Tile))
            {
                Assert.True(Math.Min(b.Rect.W, b.Rect.H) >= 5, $"{tiling}: {b.Node!.Name} is {b.Rect.W:0.#} x {b.Rect.H:0.#}");
            }
        }
    }

    [Fact]
    public void Neighbours_keep_the_gap_of_their_depth()
    {
        var root = Folder("C:\\", Enumerable.Range(1, 12).Select(i => File($"f{i}.bin", 1000 * i)).ToArray());
        ScanPostProcessor.AggregateSubtree(root);
        var options = new MapLayoutOptions();
        foreach (var tiling in new[] { MapTiling.Squarified, MapTiling.Rows })
        {
            var boxes = MapLayout.Build(root, new RectD(0, 0, 900, 600), 0, new MapLayoutOptions { Tiling = tiling });
            for (int i = 0; i < boxes.Count; i++)
            {
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i].Rect;
                    var b = boxes[j].Rect;
                    bool overlap = a.X < b.Right + options.Gap(0) - 0.5 && b.X < a.Right + options.Gap(0) - 0.5 &&
                                   a.Y < b.Bottom + options.Gap(0) - 0.5 && b.Y < a.Bottom + options.Gap(0) - 0.5;
                    Assert.False(overlap, $"{tiling}: {a} and {b} are closer than the gap");
                }
            }
        }
    }
}

public class RowLayoutTests
{
    [Fact]
    public void Rows_span_the_width_and_get_shorter()
    {
        var weights = Enumerable.Range(1, 30).Select(i => 1000.0 / i).ToArray();
        var bounds = new RectD(0, 0, 960, 600);
        var rects = new RectD[weights.Length];
        RowLayout.Layout(weights, bounds, rects);

        double total = weights.Sum();
        for (int i = 0; i < weights.Length; i++) Assert.Equal(weights[i] / total * bounds.Area, rects[i].Area, 3);

        var rows = rects.GroupBy(r => Math.Round(r.Y, 6)).OrderBy(g => g.Key).ToList();
        Assert.True(rows.Count >= 3);
        double previous = double.MaxValue;
        foreach (var row in rows)
        {
            Assert.Equal(bounds.W, row.Sum(r => r.W), 6);
            Assert.All(row, r => Assert.Equal(row.First().H, r.H, 6));
            Assert.True(row.First().H <= previous + 1e-6, "rows get shorter towards the bottom");
            Assert.True(row.First().H >= 14 - 1e-6, "no hairline rows");
            previous = row.First().H;
        }
        Assert.Equal(bounds.Bottom, rects.Max(r => r.Bottom), 6);
    }
}

public class FolderGroupsTests
{
    private static DiskNode Folder(string name, params DiskNode[] kids)
    {
        var d = new DiskNode(name, NodeFlags.Directory);
        foreach (var k in kids) d.AddChild(k);
        return d;
    }

    private static DiskNode File(string name, long size) => new(name, NodeFlags.None) { Size = size, FileCount = 1 };

    [Fact]
    public void Opens_folders_that_hold_too_much_and_keeps_the_biggest()
    {
        // Users/alex holds most of the drive, so it is opened into its subfolders; Windows stays one group.
        var root = Folder("C:\\",
            Folder("Users", Folder("alex",
                Folder("AI", File("model.gguf", 400), File("lora.bin", 50)),
                Folder("Games", File("game.pak", 300), File("patch.pak", 20)),
                Folder("Videos", File("a.mkv", 100), File("b.mkv", 60)))),
            Folder("Windows", File("system.dll", 150), File("drivers.sys", 40)),
            File("pagefile.sys", 120));
        ScanPostProcessor.AggregateSubtree(root);

        var groups = FolderGroups.Find(root);
        Assert.Equal(["AI", "Games", "Windows", "Videos"], groups.Select(g => g.Name));
        Assert.True(FolderGroups.Find(root, max: 2).Count == 2);
    }
}

public class MftRecordTests
{
    /// <summary>One run of 89 clusters at LCN 0x1234 (89 × 4 KB = 364,544 bytes).</summary>
    private static readonly byte[] s_realRun = [0x21, 0x59, 0x34, 0x12, 0x00];

    /// <summary>Builds a 1 KB FILE record with a $FILE_NAME and a non-resident $DATA attribute.</summary>
    private static byte[] Record(string name, uint parent, long dataSize, long allocated, ushort flags = 1, byte[]? runs = null,
        ushort sequence = 1, ulong baseRef = 0, long lowestVcn = 0, uint number = 0)
    {
        var r = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(r, MftRecord.Signature);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 0x30);  // USA offset
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3);     // USA count
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x10), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38); // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), baseRef);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x2C), number);
        int o = 0x38;

        // $FILE_NAME, resident
        var nameBytes = Encoding.Unicode.GetBytes(name);
        int valueLen = 0x42 + nameBytes.Length;
        int attrLen = (0x18 + valueLen + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o + 4), (uint)attrLen);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o + 0x10), (uint)valueLen);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(o + 0x14), 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(o + 0x18), parent);
        r[o + 0x18 + 0x40] = (byte)name.Length;
        r[o + 0x18 + 0x41] = 1; // Win32 namespace
        nameBytes.CopyTo(r, o + 0x18 + 0x42);
        o += attrLen;

        // $DATA, non-resident
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o + 4), 0x48);
        r[o + 8] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(o + 0x10), (ulong)lowestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(o + 0x20), 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(o + 0x28), (ulong)allocated);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(o + 0x30), (ulong)dataSize);
        (runs ?? s_realRun).CopyTo(r, o + 0x40);
        o += 0x48;

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)(o + 8));

        // Update sequence: stamp 0x0007 at the end of each 512-byte stride, keep the originals in the array.
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), 7);
        for (int i = 1; i <= 2; i++)
        {
            int pos = i * 512 - 2;
            r[0x30 + i * 2] = r[pos];
            r[0x30 + i * 2 + 1] = r[pos + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(pos), 7);
        }
        return r;
    }

    [Fact]
    public void Parses_name_parent_and_sizes()
    {
        var rec = Record("notepad.exe", parent: 1234, dataSize: 360_448, allocated: 364_544);
        Assert.True(MftRecord.ApplyFixup(rec));
        var info = default(MftRecordInfo);
        var names = new List<MftName>();
        Assert.True(MftRecord.Parse(rec, ref info, names));
        Assert.True(info.InUse);
        Assert.False(info.IsDirectory);
        Assert.Equal(360_448, info.LogicalSize);
        Assert.Equal(89, info.AllocatedClusters);
        var n = Assert.Single(names);
        Assert.Equal("notepad.exe", n.Name);
        Assert.Equal(1234u, n.Parent);
    }

    [Fact]
    public void Sparse_stream_that_claims_the_whole_volume_counts_nothing()
    {
        // Like $BadClus:$Bad: the header says 464 GB allocated, but the only run is sparse (no LCN).
        byte[] sparseOnly = [0x04, 0x00, 0x00, 0x00, 0x07, 0x00];
        var rec = Record("$BadClus", parent: 5, dataSize: 464L << 30, allocated: 464L << 30, runs: sparseOnly);
        Assert.True(MftRecord.ApplyFixup(rec));
        var info = default(MftRecordInfo);
        Assert.True(MftRecord.Parse(rec, ref info, new List<MftName>()));
        Assert.Equal(0, info.AllocatedClusters);
    }

    [Fact]
    public void Counts_only_runs_that_have_a_disk_location()
    {
        // 0x10 sparse clusters, then 5 real clusters at offset +0x1000.
        byte[] runs = [0x01, 0x10, 0x21, 0x05, 0x00, 0x10, 0x00];
        Assert.Equal(5, MftRecord.CountAllocatedClusters(runs));
    }

    [Fact]
    public void Torn_record_is_rejected()
    {
        var rec = Record("a.txt", 5, 10, 4096);
        rec[1022] ^= 0xFF;
        Assert.False(MftRecord.ApplyFixup(rec));
    }

    [Fact]
    public void Leftover_extension_record_of_a_deleted_file_is_ignored()
    {
        var store = new MftScanner.RecordStore(300, 4096);
        void Add(uint number, byte[] rec)
        {
            Assert.True(MftRecord.ApplyFixup(rec));
            var info = default(MftRecordInfo);
            var names = new List<MftName>();
            Assert.True(MftRecord.Parse(rec, ref info, names));
            store.Add(number, in info, names);
        }

        Add(5, Record(".", parent: 5, dataSize: 0, allocated: 0, flags: 3, runs: [0x00], sequence: 5));
        Add(100, Record("report.pdf", parent: 5, dataSize: 200_000, allocated: 364_544, sequence: 3));
        // Left behind by a 300 MB file that owned record 100 before it was deleted (sequence 2).
        byte[] bigRun = [0x23, 0x00, 0x2C, 0x01, 0x00, 0x10, 0x00]; // 0x12C00 clusters = 300 MB
        Add(200, Record("old.mkv", parent: 5, dataSize: 300L << 20, allocated: 300L << 20, runs: bigRun,
            baseRef: (2UL << 48) | 100, lowestVcn: 100));
        // A genuine continuation of report.pdf: 10 more clusters.
        Add(201, Record("report.pdf", parent: 5, dataSize: 0, allocated: 40_960, runs: [0x21, 0x0A, 0x00, 0x20, 0x00],
            baseRef: (3UL << 48) | 100, lowestVcn: 89));

        var warnings = new System.Collections.Concurrent.ConcurrentQueue<string>();
        store.ApplyDeferred(warnings);
        var root = store.BuildTree(@"C:\", 5, 24, out _);

        var file = root.Child("report.pdf")!;
        Assert.Equal((89 + 10) * 4096L, file.Size);
        Assert.Equal(200_000, file.Logical);
        Assert.Contains(warnings, w => w.Contains("report.pdf +300 MB"));
    }

    [Fact]
    public void Reads_the_number_a_record_stores_for_itself()
    {
        Assert.Equal(4242, MftRecord.RecordNumber(Record("a.txt", 5, 10, 4096, number: 4242)));
    }

    [Fact]
    public void Decodes_data_runs_with_negative_offsets()
    {
        // 0x21: 1-byte length, 2-byte offset. Run 1: 0x18 clusters at LCN 0x5634.
        // Run 2: 0x10 clusters at offset -0x100 relative (0xFF00 as 2-byte signed).
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x21, 0x10, 0x00, 0xFF, 0x00];
        var r = MftRecord.DecodeRuns(runs);
        Assert.Equal(2, r.Count);
        Assert.Equal((0x5634L, 0x18L), r[0]);
        Assert.Equal((0x5634L - 0x100, 0x10L), r[1]);
    }
}

public class QueryTests
{
    private static DiskNode Tree()
    {
        var root = new DiskNode("C:\\", NodeFlags.Directory);
        var models = new DiskNode("models", NodeFlags.Directory);
        root.AddChild(models);
        models.AddChild(new DiskNode("big.gguf", NodeFlags.None) { Size = 40L << 30, Category = Category.AiModels });
        models.AddChild(new DiskNode("small.gguf", NodeFlags.None) { Size = 2L << 30, Category = Category.AiModels });
        root.AddChild(new DiskNode("movie.mkv", NodeFlags.None) { Size = 8L << 30, Category = Category.Video });
        ScanPostProcessor.AggregateSubtree(root);
        return root;
    }

    [Fact]
    public void Extension_and_size_terms_combine()
    {
        var root = Tree();
        var hits = NodeQuery.Parse("ext:gguf size:>20GB").Evaluate(root, out var matches, out var bytes);
        Assert.Equal("big.gguf", Assert.Single(matches).Name);
        Assert.Equal(40L << 30, bytes);
        Assert.Contains(root.Child("models")!, hits);
    }

    [Fact]
    public void Plain_words_match_folder_names()
    {
        var root = Tree();
        NodeQuery.Parse("model").Evaluate(root, out var matches, out _);
        Assert.Equal("models", Assert.Single(matches).Name);
    }

    [Fact]
    public void Category_filter_works_alone()
    {
        NodeQuery.Parse(null, Category.Video).Evaluate(Tree(), out var matches, out _);
        Assert.Equal("movie.mkv", Assert.Single(matches).Name);
    }
}

public class GrowthTests
{
    [Fact]
    public void Reports_the_deepest_folder_that_explains_growth()
    {
        var root = new DiskNode("C:\\", NodeFlags.Directory);
        var users = new DiskNode("Users", NodeFlags.Directory);
        var me = new DiskNode("me", NodeFlags.Directory);
        var models = new DiskNode("models", NodeFlags.Directory);
        var videos = new DiskNode("Videos", NodeFlags.Directory);
        root.AddChild(users);
        users.AddChild(me);
        me.AddChild(models);
        me.AddChild(videos);
        models.AddChild(new DiskNode("m.gguf", NodeFlags.None) { Size = 30L << 30 });
        videos.AddChild(new DiskNode("v.mkv", NodeFlags.None) { Size = 10L << 30 });
        ScanPostProcessor.AggregateSubtree(root);
        var scan = new ScanResult
        {
            Root = root, DriveRoot = "C:\\", Method = ScanMethod.FolderWalk, TotalBytes = 1L << 40, FreeBytes = 100L << 30,
            Duration = TimeSpan.Zero, ScannedAt = DateTime.Now,
        };
        var baseline = new Snapshot
        {
            TakenAt = DateTime.Now.AddDays(-7),
            Folders = new(StringComparer.OrdinalIgnoreCase)
            {
                [@"\Users"] = 10L << 30, [@"\Users\me"] = 10L << 30, [@"\Users\me\models"] = 2L << 30, [@"\Users\me\Videos"] = 8L << 30,
            },
        };

        var report = GrowthAnalyzer.Compare(scan, baseline);
        var top = report.Items[0];
        Assert.Equal(@"\Users\me\models", top.RelativePath);
        Assert.Equal(28L << 30, top.Delta);
        Assert.Same(models, top.Node);
        Assert.DoesNotContain(report.Items, i => i.RelativePath == @"\Users");
    }
}

public class AlertPolicyTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Alerts_once_then_again_after_losing_five_gb()
    {
        var alert = new DriveAlert { Threshold = 50 };
        var state = new DriveAlertState();
        var now = DateTime.Now;

        Assert.False(AlertPolicy.ShouldAlert(alert, state, 60 * GB, 1000 * GB, RemindPolicy.AfterFiveGbOrDaily, now, null));
        Assert.True(AlertPolicy.ShouldAlert(alert, state, 40 * GB, 1000 * GB, RemindPolicy.AfterFiveGbOrDaily, now, null));
        AlertPolicy.MarkAlerted(state, 40 * GB, now);
        Assert.False(AlertPolicy.ShouldAlert(alert, state, 37 * GB, 1000 * GB, RemindPolicy.AfterFiveGbOrDaily, now.AddHours(1), null));
        Assert.True(AlertPolicy.ShouldAlert(alert, state, 34 * GB, 1000 * GB, RemindPolicy.AfterFiveGbOrDaily, now.AddHours(2), null));
        Assert.False(AlertPolicy.ShouldAlert(alert, state, 20 * GB, 1000 * GB, RemindPolicy.AfterFiveGbOrDaily, now.AddHours(3), now.AddDays(1)));
    }

    [Fact]
    public void Percent_threshold_uses_drive_size()
    {
        var alert = new DriveAlert { Threshold = 10, Unit = ThresholdUnit.Percent };
        Assert.Equal(100 * GB, alert.ThresholdBytes(1000 * GB));
    }
}

public class FolderScannerTests
{
    [Fact]
    public void Scans_a_folder_and_aggregates()
    {
        var dir = Directory.CreateTempSubdirectory("diskmap-test");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            System.IO.File.WriteAllBytes(Path.Combine(dir.FullName, "a.bin"), new byte[10_000]);
            System.IO.File.WriteAllBytes(Path.Combine(dir.FullName, "sub", "b.mp4"), new byte[50_000]);
            var node = FolderScanner.Rescan(dir.FullName, "t", 4096);
            Assert.Equal(2, node.FileCount);
            Assert.Equal(60_000, node.Logical);
            Assert.Equal(12_288 + 53_248, node.Size);
            Assert.Equal("sub", node.Children![0].Name);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class SafetyTests
{
    [Fact]
    public void Structural_folders_cannot_be_deleted_but_their_contents_can()
    {
        var root = new DiskNode("C:\\", NodeFlags.Directory);
        var users = new DiskNode("Users", NodeFlags.Directory);
        var me = new DiskNode("me", NodeFlags.Directory);
        var downloads = new DiskNode("Downloads", NodeFlags.Directory);
        var appData = new DiskNode("AppData", NodeFlags.Directory);
        var local = new DiskNode("Local", NodeFlags.Directory);
        var cache = new DiskNode("SomeApp", NodeFlags.Directory);
        var iso = new DiskNode("big.iso", NodeFlags.None) { Size = 5L << 30 };
        var windows = new DiskNode("Windows", NodeFlags.Directory);
        var sys = new DiskNode("kernel32.dll", NodeFlags.None) { Size = 1 << 20 };
        root.AddChild(users); users.AddChild(me); me.AddChild(downloads); downloads.AddChild(iso);
        me.AddChild(appData); appData.AddChild(local); local.AddChild(cache);
        cache.AddChild(new DiskNode("x.bin", NodeFlags.None) { Size = 1 << 20 });
        root.AddChild(windows); windows.AddChild(sys);

        ScanPostProcessor.Run(root, totalBytes: 100L << 30, freeBytes: 100L << 30, CancellationToken.None);

        Assert.False(users.CanDelete);
        Assert.False(me.CanDelete);
        Assert.False(downloads.CanDelete);
        Assert.False(appData.CanDelete);
        Assert.False(local.CanDelete);
        Assert.True(cache.CanDelete);
        Assert.True(iso.CanDelete);
        Assert.Equal(Category.Archives, iso.Category);
        Assert.False(sys.CanDelete);
        Assert.True(sys.IsProtected);
    }
}

public class DeleteServiceTests
{
    [Fact]
    public async Task Deletes_trees_with_read_only_files_and_reports_locked_ones()
    {
        var dir = Directory.CreateTempSubdirectory("diskmap-del");
        var tree = Directory.CreateDirectory(Path.Combine(dir.FullName, "tree", "deep"));
        var ro = Path.Combine(tree.FullName, "ro.txt");
        System.IO.File.WriteAllText(ro, "x");
        System.IO.File.SetAttributes(ro, FileAttributes.ReadOnly);
        var lockedPath = Path.Combine(dir.FullName, "locked.bin");
        System.IO.File.WriteAllBytes(lockedPath, new byte[10]);
        try
        {
            using (System.IO.File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var report = await Actions.DeleteService.DeleteAsync(
                    [Path.Combine(dir.FullName, "tree"), lockedPath], permanently: true, progress: null, CancellationToken.None);
                Assert.Contains(Path.Combine(dir.FullName, "tree"), report.Removed);
                Assert.False(Directory.Exists(Path.Combine(dir.FullName, "tree")));
                var failure = Assert.Single(report.Failures);
                Assert.Equal(lockedPath, failure.Path);
                Assert.Equal("In use", failure.Reason);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class FormatTests
{
    [Fact]
    public void Formats_like_explorer()
    {
        Assert.Equal((18.4).ToString("0.0") + " GB", Format.Bytes((long)(18.4 * (1L << 30))));
        Assert.StartsWith("+", Format.Delta(1L << 30));
        Assert.StartsWith("−", Format.Delta(-(1L << 30)));
    }
}
