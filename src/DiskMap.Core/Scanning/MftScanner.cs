using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskMap.Core.Model;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Scans an NTFS volume by reading its master file table in large sequential chunks.
/// Needs admin rights. Each file record is counted once, so hardlinks are never double-counted.
/// Disk reads run on a background thread while the previous chunk is parsed on all cores.
/// </summary>
public sealed class MftScanner
{
    private const int RootRecord = 5;
    private const int FirstUserRecord = 24;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const int ChunkBytes = 8 << 20;
    private const int ReadsInFlight = 4;
    private const int ParseBatch = 512;

    /// <summary>A contiguous piece of the $MFT on disk. DiskOffset is -1 for a sparse run.</summary>
    private readonly record struct Chunk(long DiskOffset, long FirstRecord, int Bytes);

    public static bool CanScan(string driveRoot) =>
        Elevation.IsElevated && string.Equals(Volume.FileSystem(driveRoot), "NTFS", StringComparison.OrdinalIgnoreCase);

    public unsafe ScanResult Scan(string driveRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string letter = driveRoot.TrimEnd('\\');
        string volumePath = @"\\.\" + letter;
        using var volume = OpenDevice(volumePath, 0);
        if (volume.IsInvalid) throw new IOException($"Could not open volume {letter} (error {Marshal.GetLastPInvokeError()}).");

        NtfsVolumeData data;
        if (!Kernel32.DeviceIoControl(volume, Kernel32.FSCTL_GET_NTFS_VOLUME_DATA, null, 0, &data, sizeof(NtfsVolumeData), out _, 0))
        {
            throw new IOException($"{letter} is not an NTFS volume (error {Marshal.GetLastPInvokeError()}).");
        }

        int recordSize = (int)data.BytesPerFileRecordSegment;
        long clusterSize = data.BytesPerCluster;
        // Some drives refuse reads smaller than their 4 KB physical sectors even when the logical
        // sector is 512 bytes, so every read is aligned to at least 4 KB and to the cluster size.
        int align = (int)Math.Max(4096, Math.Max(clusterSize, data.BytesPerSector));
        long recordCount = data.MftValidDataLength / recordSize;

        // Installed games are looked up from the registry and launcher files while the table is read.
        var catalog = Task.Run(Games.GameCatalog.Load, ct);
        var warnings = new ConcurrentQueue<string>();
        using var source = TableSource.Open(volumePath, volume, data, recordSize, align, recordCount, warnings);
        var chunks = source.Chunks;
        var handles = source.Handles;

        var store = new RecordStore((int)recordCount, clusterSize);
        var phase = Stopwatch.StartNew();
        var spare = new Stack<nint>();
        for (int i = 0; i <= ReadsInFlight; i++) spare.Push((nint)NativeMemory.AlignedAlloc(ChunkBytes, 4096));
        var pending = new Queue<(int Chunk, Task Read, nint Buffer)>();
        int nextRead = 0;
        void StartReads()
        {
            while (pending.Count < ReadsInFlight && nextRead < chunks.Count && spare.Count > 0)
            {
                int c = nextRead++;
                nint buf = spare.Pop();
                var h = handles[c % handles.Length];
                var chunk = chunks[c];
                pending.Enqueue((c, Task.Run(() => source.Read(h, chunk, buf, recordSize, warnings)), buf));
            }
        }

        try
        {
            StartReads();
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (c, read, buffer) = pending.Dequeue();
                read.GetAwaiter().GetResult();
                StartReads(); // keep the drive busy while this chunk is parsed
                ParseChunk(chunks[c], buffer, recordSize, recordCount, store, !source.PerRecord);
                spare.Push(buffer);
                if ((c & 7) == 7 || c == chunks.Count - 1)
                {
                    long done = Math.Min(recordCount, chunks[c].FirstRecord + chunks[c].Bytes / recordSize);
                    progress?.Report(new ScanProgress(done, 0, null, 0.85 * done / Math.Max(1, recordCount)));
                }
            }
        }
        finally
        {
            foreach (var (_, read, buffer) in pending)
            {
                try { read.Wait(); } catch (AggregateException) { }
                spare.Push(buffer);
            }
            while (spare.Count > 0) NativeMemory.AlignedFree((void*)spare.Pop());
        }
        store.ApplyDeferred(warnings);
        long readMs = phase.ElapsedMilliseconds;

        ct.ThrowIfCancellationRequested();
        phase.Restart();
        var root = store.BuildTree(driveRoot, RootRecord, FirstUserRecord, out long items);
        long treeMs = phase.ElapsedMilliseconds;
        phase.Restart();
        Volume.TryGetFreeSpace(driveRoot, out long free, out long total);
        ScanPostProcessor.Run(root, total, free, ct, catalog.GetAwaiter().GetResult());
        long postMs = phase.ElapsedMilliseconds;
        if (root.Size > (total - free) + (total - free) / 20)
        {
            // Should never happen; say where the extra bytes are so it can be fixed.
            var meta = root.Child("NTFS metadata")?.Children?.Where(c => c.Size > 1L << 30)
                .Select(c => $"{c.Name} {c.Size / (1 << 20):N0} MB") ?? [];
            warnings.Enqueue($"Scanned {root.Size / (1 << 20):N0} MB but the drive reports {(total - free) / (1 << 20):N0} MB used. " +
                             $"Large metadata: {string.Join(", ", meta)}");
        }
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
            Warnings = warnings.ToList(),
            Timings = $"{recordCount:N0} records, {(long)recordCount * recordSize / (1 << 20):N0} MB from the {source.How}: read+parse {readMs} ms, " +
                      $"tree {treeMs} ms, classify+sum {postMs} ms",
        };
    }

    private static SafeFileHandle OpenDevice(string path, uint flags) =>
        Kernel32.CreateFile(path, Kernel32.GENERIC_READ, Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE | Kernel32.FILE_SHARE_DELETE,
            0, Kernel32.OPEN_EXISTING, flags, 0);

    /// <summary>
    /// Where the table is read from. Reading the volume directly is fastest, but some drivers refuse
    /// raw volume reads. Then the disk under the volume is read at the volume's offset, and if that
    /// fails too (an encrypted or spanned volume, or a very fragmented $MFT), NTFS is asked for one
    /// record at a time.
    /// </summary>
    private sealed class TableSource : IDisposable
    {
        private TableSource(string how, SafeFileHandle[] handles, List<Chunk> chunks, bool perRecord)
        {
            How = how;
            Handles = handles;
            Chunks = chunks;
            PerRecord = perRecord;
        }

        public string How { get; }

        /// <summary>
        /// NVMe drives only reach full speed with several requests in flight, and requests on one
        /// synchronous handle are serialised, so each in-flight read gets its own handle.
        /// </summary>
        public SafeFileHandle[] Handles { get; }

        public List<Chunk> Chunks { get; }

        /// <summary>Records come from NTFS one at a time, already unprotected, instead of raw from disk.</summary>
        public bool PerRecord { get; }

        public static TableSource Open(string volumePath, SafeFileHandle volume, in NtfsVolumeData data, int recordSize, int align,
            long recordCount, ConcurrentQueue<string> warnings)
        {
            long clusterSize = data.BytesPerCluster;
            long mftOffset = data.MftStartLcn * clusterSize;

            // Record 0 describes the $MFT itself; its data runs say where the rest of the table lives.
            var handles = OpenHandles(volumePath, Kernel32.FILE_FLAG_NO_BUFFERING);
            if (TryReadMftRuns(handles[0], mftOffset, recordSize, align, out var runs, out string volumeError))
                return new("volume", handles, PlanChunks(runs, 0, clusterSize, recordSize, recordCount, align), false);
            DisposeAll(handles);

            string diskError = "the volume spans several disks";
            if (TryDiskExtent(volume, out int disk, out long start))
            {
                handles = OpenHandles(@"\\.\PhysicalDrive" + disk, Kernel32.FILE_FLAG_NO_BUFFERING);
                if (TryReadMftRuns(handles[0], start + mftOffset, recordSize, align, out runs, out diskError))
                {
                    warnings.Enqueue($"Reading the volume directly failed ({volumeError}); read the table from disk {disk} instead.");
                    return new("disk under the volume", handles, PlanChunks(runs, start, clusterSize, recordSize, recordCount, align), false);
                }
                DisposeAll(handles);
            }

            handles = OpenHandles(volumePath, 0);
            if (!TryFileRecord(handles[0], recordSize, out string recordError))
            {
                DisposeAll(handles);
                throw new IOException($"Could not read the file table: volume: {volumeError}; disk: {diskError}; NTFS: {recordError}.");
            }
            warnings.Enqueue($"Reading the volume ({volumeError}) and the disk ({diskError}) failed; asked NTFS for each record instead.");
            var chunks = new List<Chunk>();
            int perChunk = ChunkBytes / recordSize;
            for (long r = 0; r < recordCount; r += perChunk)
                chunks.Add(new Chunk(0, r, (int)Math.Min(perChunk, recordCount - r) * recordSize));
            return new("file system", handles, chunks, true);
        }

        public unsafe void Read(SafeFileHandle handle, Chunk chunk, nint buffer, int recordSize, ConcurrentQueue<string> warnings)
        {
            var span = new Span<byte>((void*)buffer, chunk.Bytes);
            if (PerRecord) ReadRecords(handle, chunk, span, recordSize);
            else if (chunk.DiskOffset < 0) span.Clear();
            else ReadResilient(handle, span, chunk.DiskOffset, warnings);
        }

        public void Dispose() => DisposeAll(Handles);

        private static SafeFileHandle[] OpenHandles(string path, uint flags)
        {
            var handles = new SafeFileHandle[ReadsInFlight];
            handles[0] = OpenDevice(path, flags);
            for (int i = 1; i < handles.Length; i++)
            {
                var h = handles[0].IsInvalid ? handles[0] : OpenDevice(path, flags);
                handles[i] = h.IsInvalid ? handles[0] : h;
            }
            return handles;
        }

        private static void DisposeAll(SafeFileHandle[] handles)
        {
            foreach (var h in handles) h.Dispose(); // a handle shared by two slots tolerates a second Dispose
        }
    }

    private static List<Chunk> PlanChunks(List<(long Lcn, long Clusters)> runs, long baseOffset, long clusterSize, int recordSize,
        long recordCount, int align)
    {
        var chunks = new List<Chunk>();
        long record = 0;
        foreach (var (lcn, clusters) in runs)
        {
            long runBytes = clusters * clusterSize, done = 0;
            while (done < runBytes && record < recordCount)
            {
                long remaining = (recordCount - record) * recordSize;
                long bytes = Math.Min(ChunkBytes, Math.Min(runBytes - done, remaining));
                bytes = Math.Min(runBytes - done, (bytes + align - 1) / align * align);
                chunks.Add(new Chunk(lcn >= 0 ? baseOffset + lcn * clusterSize + done : -1, record, (int)bytes));
                record += bytes / recordSize;
                done += bytes;
            }
        }
        return chunks;
    }

    /// <summary>Parses one chunk of records on all cores. Records write only to their own slots.</summary>
    private static unsafe void ParseChunk(Chunk chunk, nint buffer, int recordSize, long recordCount, RecordStore store, bool raw)
    {
        int n = (int)Math.Min(chunk.Bytes / recordSize, recordCount - chunk.FirstRecord);
        if (n <= 0 || chunk.DiskOffset < 0) return;
        int batches = (n + ParseBatch - 1) / ParseBatch;
        Parallel.For(0, batches, () => new List<MftName>(4), (b, _, names) =>
        {
            int end = Math.Min(n, (b + 1) * ParseBatch);
            for (int i = b * ParseBatch; i < end; i++)
            {
                var rec = new Span<byte>((byte*)buffer + (long)i * recordSize, recordSize);
                // Raw records must pass the update-sequence check; records from NTFS are already unprotected.
                if (!MftRecord.ApplyFixup(rec) && raw) continue;
                uint number = (uint)(chunk.FirstRecord + i);
                long own = MftRecord.RecordNumber(rec);
                if (own >= 0 && own != number)
                {
                    store.CountMisplaced(); // never expected; a sign the table was mapped wrongly
                    continue;
                }
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
                store.Add(number, in info, names);
            }
            return names;
        }, _ => { });
    }

    /// <summary>
    /// Reads a chunk of the MFT. If the device refuses a large read, retries in 64 KB pieces and
    /// zero-fills pieces that still fail, so one bad region doesn't sink the whole scan.
    /// </summary>
    private static void ReadResilient(SafeFileHandle volume, Span<byte> span, long offset, ConcurrentQueue<string> warnings)
    {
        try
        {
            int read = RandomAccess.Read(volume, span, offset);
            if (read < span.Length) span[read..].Clear();
            return;
        }
        catch (IOException ex)
        {
            if (warnings.Count < 20) warnings.Enqueue($"Read of {span.Length:N0} bytes at {offset:N0} failed ({ex.HResult & 0xFFFF}: {ex.Message}); retrying in pieces.");
        }
        const int Piece = 64 * 1024;
        for (int pos = 0; pos < span.Length; pos += Piece)
        {
            var part = span.Slice(pos, Math.Min(Piece, span.Length - pos));
            try
            {
                int read = RandomAccess.Read(volume, part, offset + pos);
                if (read < part.Length) part[read..].Clear();
            }
            catch (IOException ex)
            {
                part.Clear();
                if (warnings.Count < 20) warnings.Enqueue($"Skipped {part.Length:N0} unreadable bytes at {offset + pos:N0} ({ex.HResult & 0xFFFF}).");
            }
        }
    }

    /// <summary>Reads record 0 raw from a volume or disk and returns where the rest of the $MFT lives.</summary>
    private static unsafe bool TryReadMftRuns(SafeFileHandle device, long offset, int recordSize, int align,
        out List<(long Lcn, long Clusters)> runs, out string error)
    {
        runs = [];
        if (device.IsInvalid)
        {
            error = $"could not open it (error {Marshal.GetLastPInvokeError()})";
            return false;
        }
        int size = Math.Max(recordSize, align);
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            var span = new Span<byte>(buf, size);
            try
            {
                RandomAccess.Read(device, span, offset);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"{ex.Message.TrimEnd('.', ' ')} at byte {offset:N0}";
                return false;
            }
            var rec = span[..recordSize];
            if (!MftRecord.ApplyFixup(rec) || MftRecord.RecordNumber(rec) > 0)
            {
                error = $"no $MFT record at byte {offset:N0}";
                return false;
            }
            var found = MftRecord.DataRuns(rec, out _);
            if (found == null)
            {
                error = "the $MFT is too fragmented to read in one pass";
                return false;
            }
            runs = found;
            error = "";
            return true;
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    /// <summary>The disk a volume lives on and the byte offset it starts at, if it sits on a single disk.</summary>
    private static unsafe bool TryDiskExtent(SafeFileHandle volume, out int disk, out long start)
    {
        // VOLUME_DISK_EXTENTS: an extent count, then { disk number, start offset, length } from byte 8.
        byte* buf = stackalloc byte[64];
        disk = 0;
        start = 0;
        if (!Kernel32.DeviceIoControl(volume, Kernel32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 64, out _, 0) || *(int*)buf != 1)
            return false;
        disk = *(int*)(buf + 8);
        start = *(long*)(buf + 16);
        return true;
    }

    private static unsafe bool TryFileRecord(SafeFileHandle volume, int recordSize, out string error)
    {
        int outSize = 16 + recordSize;
        byte* output = stackalloc byte[outSize];
        var rec = new byte[recordSize];
        if (!volume.IsInvalid && FileRecord(volume, 0, output, outSize, rec) && MftRecord.RecordNumber(rec) <= 0)
        {
            error = "";
            return true;
        }
        error = $"error {Marshal.GetLastPInvokeError()}";
        return false;
    }

    private static unsafe void ReadRecords(SafeFileHandle volume, Chunk chunk, Span<byte> span, int recordSize)
    {
        int outSize = 16 + recordSize;
        byte* output = stackalloc byte[outSize];
        for (int i = 0; i < chunk.Bytes / recordSize; i++)
        {
            var target = span.Slice(i * recordSize, recordSize);
            if (!FileRecord(volume, chunk.FirstRecord + i, output, outSize, target)) target.Clear();
        }
    }

    /// <summary>
    /// Asks NTFS for one record (NTFS_FILE_RECORD_OUTPUT_BUFFER: number, length, then the record).
    /// For a record that isn't in use NTFS returns the nearest one below it, which is rejected.
    /// </summary>
    private static unsafe bool FileRecord(SafeFileHandle volume, long number, byte* output, int outSize, Span<byte> target)
    {
        long input = number;
        if (!Kernel32.DeviceIoControl(volume, Kernel32.FSCTL_GET_NTFS_FILE_RECORD, &input, 8, output, outSize, out _, 0)) return false;
        if ((*(long*)output & 0xFFFF_FFFF_FFFF) != number || *(int*)(output + 8) != target.Length) return false;
        new ReadOnlySpan<byte>(output + 12, target.Length).CopyTo(target);
        return true;
    }

    /// <summary>
    /// Column store of parsed records, indexed by record number. Base records are written in
    /// parallel (each to its own slot); extension records are queued and applied afterwards.
    /// </summary>
    internal sealed class RecordStore(int count, long clusterSize)
    {
        private readonly long[] _alloc = new long[count];
        private readonly long[] _logical = new long[count];
        private readonly long[] _mtime = new long[count];
        private readonly uint[] _parent = new uint[count];
        private readonly string?[] _name = new string?[count];
        private readonly byte[] _nameNs = new byte[count];
        private readonly byte[] _state = new byte[count]; // 1 in use, 2 directory
        private readonly ushort[] _seq = new ushort[count];
        private readonly uint[] _reparse = new uint[count];
        private readonly Dictionary<uint, List<(uint Parent, string Name)>> _extraLinks = new();
        private readonly ConcurrentQueue<(uint Target, MftRecordInfo Info, MftName[] Names)> _deferred = new();
        private int _misplaced;

        public void CountMisplaced() => Interlocked.Increment(ref _misplaced);

        public void Add(uint index, in MftRecordInfo info, List<MftName> names)
        {
            uint target = info.IsExtension && info.BaseRecord < (uint)count ? info.BaseRecord : index;
            if (target != index)
            {
                _deferred.Enqueue((target, info, names.ToArray()));
                return;
            }
            _state[index] = (byte)(1 | (info.IsDirectory ? 2 : 0));
            _seq[index] = info.Sequence;
            if (info.HasStandardInfo) _mtime[index] = info.ModifiedTime;
            Merge(index, in info, names);
        }

        private void Merge(uint target, in MftRecordInfo info, IReadOnlyList<MftName> names)
        {
            _alloc[target] += info.AllocatedClusters * clusterSize;
            if (info.HasUnnamedData) _logical[target] = info.LogicalSize;
            if (info.ReparseTag != 0) _reparse[target] = info.ReparseTag;
            for (int i = 0; i < names.Count; i++) AddName(target, names[i]);
        }

        /// <summary>
        /// Applies extension records (records that continue a base record) once parsing is done.
        /// An extension record whose base has since been freed or reused (its sequence number moved
        /// on) is left over from a deleted file; merging it would hand that file's clusters to
        /// whatever file now owns the record number.
        /// </summary>
        public void ApplyDeferred(ConcurrentQueue<string> warnings)
        {
            int stale = 0;
            long staleBytes = 0;
            var wouldHaveGrown = new Dictionary<uint, long>();
            while (_deferred.TryDequeue(out var d))
            {
                bool baseInUse = (_state[d.Target] & 1) != 0;
                if (baseInUse && (d.Info.BaseSequence == 0 || d.Info.BaseSequence == _seq[d.Target]))
                {
                    Merge(d.Target, in d.Info, d.Names);
                    continue;
                }
                long bytes = d.Info.AllocatedClusters * clusterSize;
                stale++;
                staleBytes += bytes;
                if (baseInUse) wouldHaveGrown[d.Target] = wouldHaveGrown.GetValueOrDefault(d.Target) + bytes;
            }
            if (stale > 0)
            {
                var names = wouldHaveGrown.OrderByDescending(kv => kv.Value).Take(5)
                    .Select(kv => $"{_name[kv.Key] ?? "?"} +{kv.Value >> 20:N0} MB");
                warnings.Enqueue($"Ignored {stale:N0} leftover extension records of deleted files ({staleBytes >> 20:N0} MB)" +
                                 (wouldHaveGrown.Count > 0 ? $"; they point at records now reused by {string.Join(", ", names)}." : "."));
            }
            if (_misplaced > 0) warnings.Enqueue($"Skipped {_misplaced:N0} records found at the wrong position in the table.");
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
            lock (_extraLinks)
            {
                if (!_extraLinks.TryGetValue(target, out var list)) _extraLinks[target] = list = new();
                list.Add((n.Parent, n.Name));
            }
        }

        public DiskNode BuildTree(string driveRoot, int rootRecord, int firstUserRecord, out long items)
        {
            // Count children first so every folder's list is allocated once at the right size.
            var childCount = new int[count];
            for (int i = 0; i < count; i++)
            {
                if ((_state[i] & 1) == 0 || _name[i] == null) continue;
                uint p = _parent[i];
                if (p < (uint)count && (_state[p] & 2) != 0) childCount[p]++;
            }

            var nodes = new DiskNode?[count];
            long created = 0;
            Parallel.For(0, (count + 65535) / 65536, () => 0L, (block, _, local) =>
            {
                int end = Math.Min(count, (block + 1) * 65536);
                for (int i = block * 65536; i < end; i++)
                {
                    if ((_state[i] & 1) == 0 || _name[i] == null) continue;
                    bool dir = (_state[i] & 2) != 0;
                    var flags = dir ? NodeFlags.Directory : NodeFlags.None;
                    if (IsLink(_reparse[i])) flags |= NodeFlags.ReparsePoint;
                    var node = new DiskNode(i == rootRecord ? driveRoot : _name[i]!, flags, dir ? childCount[i] : 0)
                    {
                        // Kept in UTC; converted to local time only when shown.
                        Modified = _mtime[i] > 0 ? DateTime.FromFileTimeUtc(_mtime[i]) : default,
                        // Allocated clusters are what the record really occupies, so links count ~0 and
                        // compressed, sparse, deduplicated or cloud-only files count what they actually use.
                        Size = _alloc[i],
                    };
                    if (!dir)
                    {
                        node.Logical = _logical[i];
                        node.FileCount = 1;
                    }
                    nodes[i] = node;
                    local++;
                }
                return local;
            }, local => Interlocked.Add(ref created, local));
            items = created;

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
                    node.Flags |= NodeFlags.Protected;
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
