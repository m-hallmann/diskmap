using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskMap.Core.Model;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Scans an NTFS volume by reading its master file table in large sequential chunks.
/// Needs admin rights. Each file record is counted once, so hardlinks are never double-counted.
/// </summary>
public sealed class MftScanner
{
    private const int RootRecord = 5;
    private const int FirstUserRecord = 24;
    private const uint ReparseTagMountPoint = 0xA0000003;

    public static bool CanScan(string driveRoot) =>
        Elevation.IsElevated && string.Equals(Volume.FileSystem(driveRoot), "NTFS", StringComparison.OrdinalIgnoreCase);

    public unsafe ScanResult Scan(string driveRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string letter = driveRoot.TrimEnd('\\');
        using var volume = Kernel32.CreateFile(@"\\.\" + letter, Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE, 0, Kernel32.OPEN_EXISTING, 0, 0);
        if (volume.IsInvalid) throw new IOException($"Could not open volume {letter} (error {Marshal.GetLastPInvokeError()}).");

        NtfsVolumeData data;
        if (!Kernel32.DeviceIoControl(volume, Kernel32.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &data, sizeof(NtfsVolumeData), out _, 0))
        {
            throw new IOException($"{letter} is not an NTFS volume (error {Marshal.GetLastPInvokeError()}).");
        }

        int recordSize = (int)data.BytesPerFileRecordSegment;
        long clusterSize = data.BytesPerCluster;
        int sector = (int)Math.Max(512, data.BytesPerSector);
        long recordCount = data.MftValidDataLength / recordSize;

        // Record 0 describes the $MFT itself; its data runs say where the rest of the table lives.
        var runs = ReadMftRuns(volume, data, recordSize, sector);

        var store = new RecordStore((int)recordCount);
        var names = new List<MftName>(4);
        var warnings = new List<string>();
        const int ChunkBytes = 8 << 20;
        byte* buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, 4096);
        try
        {
            long record = 0;
            foreach (var (lcn, clusters) in runs)
            {
                long runBytes = clusters * clusterSize;
                long done = 0;
                while (done < runBytes && record < recordCount)
                {
                    ct.ThrowIfCancellationRequested();
                    long remainingRecords = recordCount - record;
                    int toRead = (int)Math.Min(ChunkBytes, Math.Min(runBytes - done, remainingRecords * recordSize));
                    toRead = (toRead + sector - 1) / sector * sector;
                    var span = new Span<byte>(buffer, toRead);
                    if (lcn >= 0) ReadResilient(volume, span, lcn * clusterSize + done, sector, warnings);
                    else span.Clear();

                    int inChunk = toRead / recordSize;
                    for (int i = 0; i < inChunk && record < recordCount; i++, record++)
                    {
                        var rec = span.Slice(i * recordSize, recordSize);
                        if (!MftRecord.ApplyFixup(rec)) continue;
                        var info = default(MftRecordInfo);
                        names.Clear();
                        try
                        {
                            if (!MftRecord.Parse(rec, ref info, names) || !info.InUse) continue;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            continue; // malformed record: skip it rather than abandon the scan
                        }
                        store.Add((uint)record, in info, names);
                    }
                    done += toRead;
                    if ((record & 0xFFFF) < (uint)inChunk || done >= runBytes)
                    {
                        progress?.Report(new ScanProgress(record, 0, null, 0.85 * record / Math.Max(1, recordCount)));
                    }
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        ct.ThrowIfCancellationRequested();
        var root = store.BuildTree(driveRoot, RootRecord, FirstUserRecord, out long items);
        Volume.TryGetFreeSpace(driveRoot, out long free, out long total);
        ScanPostProcessor.Run(root, total, free, ct);
        progress?.Report(new ScanProgress(items, root.Size, null, 1));
        return new ScanResult
        {
            Root = root,
            DriveRoot = driveRoot,
            Method = ScanMethod.MasterFileTable,
            TotalBytes = total,
            FreeBytes = free,
            Duration = watch.Elapsed,
            ScannedAt = DateTime.Now,
            ItemCount = items,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Reads a chunk of the MFT. If the device refuses a large read, retries in 64 KB pieces and
    /// zero-fills pieces that still fail, so one bad region doesn't sink the whole scan.
    /// </summary>
    private static void ReadResilient(SafeFileHandle volume, Span<byte> span, long offset, int sector, List<string> warnings)
    {
        try
        {
            int read = RandomAccess.Read(volume, span, offset);
            if (read < span.Length) span[read..].Clear();
            return;
        }
        catch (IOException ex)
        {
            if (warnings.Count < 20) warnings.Add($"Read of {span.Length:N0} bytes at {offset:N0} failed ({ex.HResult & 0xFFFF}: {ex.Message}); retrying in pieces.");
        }
        int piece = Math.Max(sector, 64 * 1024);
        for (int pos = 0; pos < span.Length; pos += piece)
        {
            var part = span.Slice(pos, Math.Min(piece, span.Length - pos));
            try
            {
                int read = RandomAccess.Read(volume, part, offset + pos);
                if (read < part.Length) part[read..].Clear();
            }
            catch (IOException ex)
            {
                part.Clear();
                if (warnings.Count < 20) warnings.Add($"Skipped {part.Length:N0} unreadable bytes at {offset + pos:N0} ({ex.HResult & 0xFFFF}).");
            }
        }
    }

    private static unsafe List<(long Lcn, long Clusters)> ReadMftRuns(SafeFileHandle volume, NtfsVolumeData data, int recordSize, int sector)
    {
        int size = Math.Max(recordSize, sector);
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            var span = new Span<byte>(buf, size);
            long offset = data.MftStartLcn * data.BytesPerCluster;
            try
            {
                RandomAccess.Read(volume, span, offset);
            }
            catch (IOException ex)
            {
                throw new IOException($"Reading the $MFT record at byte {offset:N0} ({size} bytes, sector {data.BytesPerSector}, " +
                                      $"cluster {data.BytesPerCluster}, record {recordSize}) failed: {ex.Message}", ex);
            }
            var rec = span[..recordSize];
            if (!MftRecord.ApplyFixup(rec)) throw new IOException("The $MFT record is damaged.");
            return MftRecord.DataRuns(rec, out _) ?? throw new IOException("The $MFT is too fragmented to read directly.");
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    /// <summary>Column store of parsed records, indexed by record number.</summary>
    private sealed class RecordStore(int count)
    {
        private readonly long[] _alloc = new long[count];
        private readonly long[] _logical = new long[count];
        private readonly long[] _mtime = new long[count];
        private readonly uint[] _parent = new uint[count];
        private readonly string?[] _name = new string?[count];
        private readonly byte[] _nameNs = new byte[count];
        private readonly byte[] _state = new byte[count]; // 1 in use, 2 directory
        private readonly uint[] _reparse = new uint[count];
        private readonly uint[] _attrs = new uint[count];
        private readonly Dictionary<uint, List<(uint Parent, string Name)>> _extraLinks = new();

        public void Add(uint index, in MftRecordInfo info, List<MftName> names)
        {
            uint target = info.BaseRecord != 0 && info.BaseRecord < (uint)count ? info.BaseRecord : index;
            if (target == index)
            {
                _state[index] = (byte)(1 | (info.IsDirectory ? 2 : 0));
                if (info.HasStandardInfo)
                {
                    _mtime[index] = info.ModifiedTime;
                    _attrs[index] = info.Attributes;
                }
            }
            _alloc[target] += info.AllocatedSize;
            if (info.HasUnnamedData) _logical[target] = info.LogicalSize;
            if (info.ReparseTag != 0) _reparse[target] = info.ReparseTag;
            foreach (var n in names) AddName(target, n);
        }

        private void AddName(uint target, MftName n)
        {
            var current = _name[target];
            if (current == null)
            {
                _name[target] = n.Name;
                _parent[target] = n.Parent;
                _nameNs[target] = n.Namespace;
                return;
            }
            if (n.Namespace == MftRecord.NamespaceDos) return; // 8.3 alias of a name we already have
            if (_nameNs[target] == MftRecord.NamespaceDos)
            {
                _name[target] = n.Name;
                _parent[target] = n.Parent;
                _nameNs[target] = n.Namespace;
                return;
            }
            if (_parent[target] == n.Parent && string.Equals(current, n.Name, StringComparison.Ordinal)) return;
            if (!_extraLinks.TryGetValue(target, out var list)) _extraLinks[target] = list = new();
            list.Add((n.Parent, n.Name));
        }

        public DiskNode BuildTree(string driveRoot, int rootRecord, int firstUserRecord, out long items)
        {
            var nodes = new DiskNode?[count];
            items = 0;
            for (int i = 0; i < count; i++)
            {
                if ((_state[i] & 1) == 0 || _name[i] == null) continue;
                bool dir = (_state[i] & 2) != 0;
                var flags = dir ? NodeFlags.Directory : NodeFlags.None;
                if (IsLink(_reparse[i])) flags |= NodeFlags.ReparsePoint;
                var node = new DiskNode(i == rootRecord ? driveRoot : _name[i]!, flags);
                node.Modified = _mtime[i] > 0 ? DateTime.FromFileTimeUtc(_mtime[i]).ToLocalTime() : default;
                // Allocated clusters are what the record really occupies, so links naturally count ~0
                // and compressed, sparse, deduplicated or cloud-only files count what they actually use.
                node.Size = _alloc[i];
                if (!dir)
                {
                    node.Logical = _logical[i];
                    node.FileCount = 1;
                }
                nodes[i] = node;
                items++;
            }

            var root = nodes[rootRecord] ?? throw new IOException("The root folder record is missing.");
            var metadata = new DiskNode("NTFS metadata", NodeFlags.Directory | NodeFlags.Synthetic | NodeFlags.Protected);
            var orphans = new DiskNode("Files without a folder", NodeFlags.Directory | NodeFlags.Synthetic | NodeFlags.NoDelete);

            for (int i = 0; i < count; i++)
            {
                var node = nodes[i];
                if (node == null || i == rootRecord) continue;
                uint p = _parent[i];
                var parent = p < count ? nodes[p] : null;
                if (i < firstUserRecord && node.Name.StartsWith('$'))
                {
                    metadata.AddChild(node);
                    MarkProtected(node);
                }
                else if (parent != null && parent.IsDirectory && !ReferenceEquals(parent, node))
                {
                    parent.AddChild(node);
                }
                else
                {
                    orphans.AddChild(node);
                }
            }
            root.AddChild(metadata);
            if (orphans.Children!.Count > 0) root.AddChild(orphans);

            ResolveHardlinks(root, nodes);
            return root;
        }

        private static void MarkProtected(DiskNode node)
        {
            node.Flags |= NodeFlags.Protected;
            if (node.Children == null) return;
            foreach (var c in node.Children) MarkProtected(c);
        }

        /// <summary>
        /// A file with several names is counted once. If its primary name lives in WinSxS and another
        /// name does not, move it to the other folder so System32 shows the bytes and WinSxS only its own.
        /// </summary>
        private void ResolveHardlinks(DiskNode root, DiskNode?[] nodes)
        {
            if (_extraLinks.Count == 0) return;
            var winsxs = root.Child("Windows")?.Child("WinSxS");
            foreach (var (record, links) in _extraLinks)
            {
                var node = nodes[record];
                if (node == null || node.IsDirectory || node.Parent == null) continue;
                node.Flags |= NodeFlags.Hardlinked;
                bool primaryInSxs = winsxs != null && (ReferenceEquals(node.Parent, winsxs) || winsxs.IsAncestorOf(node.Parent));
                if (primaryInSxs)
                {
                    foreach (var (parentRecord, name) in links)
                    {
                        var other = parentRecord < nodes.Length ? nodes[parentRecord] : null;
                        if (other == null || !other.IsDirectory) continue;
                        if (ReferenceEquals(other, winsxs) || winsxs!.IsAncestorOf(other)) continue;
                        var oldParent = node.Parent;
                        oldParent.Children!.Remove(node);
                        oldParent.LinkedElsewhere += node.Size;
                        node.Name = name;
                        other.AddChild(node);
                        break;
                    }
                }
                foreach (var (parentRecord, _) in links)
                {
                    var other = parentRecord < nodes.Length ? nodes[parentRecord] : null;
                    if (other != null && !ReferenceEquals(other, node.Parent)) other.LinkedElsewhere += node.Size;
                }
            }
        }

        /// <summary>Reparse tags that point somewhere else instead of holding data.</summary>
        private static bool IsLink(uint tag) => tag is ReparseTagMountPoint
            or 0xA000000C   // symbolic link
            or 0x8000001B   // app execution alias (e.g. WindowsApps\notepad.exe)
            or 0xA000001D;  // WSL symlink
    }
}
