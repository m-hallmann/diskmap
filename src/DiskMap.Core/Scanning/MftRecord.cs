using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskMap.Core.Scanning;

internal readonly record struct MftName(uint Parent, string Name, byte Namespace);

/// <summary>What one FILE record (base or extension) contributes.</summary>
internal struct MftRecordInfo
{
    public bool InUse;
    public bool IsDirectory;
    /// <summary>Set for extension records, which continue the base record named by BaseRecord.</summary>
    public bool IsExtension;
    public uint BaseRecord;
    /// <summary>The base record's sequence number as the extension record remembers it.</summary>
    public ushort BaseSequence;
    /// <summary>Bumped by NTFS each time the record is freed, so stale references can be told apart.</summary>
    public ushort Sequence;
    public bool HasUnnamedData;
    public long LogicalSize;
    /// <summary>Clusters actually allocated on disk, counted from the run lists of every stream.</summary>
    public long AllocatedClusters;
    public long ModifiedTime;
    public bool HasStandardInfo;
    public uint Attributes;
    public uint ReparseTag;
}

/// <summary>Parses raw NTFS FILE records. Pure functions, no I/O.</summary>
internal static class MftRecord
{
    public const uint Signature = 0x454C4946; // "FILE"
    public const byte NamespaceDos = 2;

    private const uint AttrStandardInformation = 0x10;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrIndexAllocation = 0xA0;
    private const uint AttrReparsePoint = 0xC0;
    private const uint AttrEnd = 0xFFFFFFFF;

    private static ushort U16(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
    private static ulong U64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);

    /// <summary>Undoes the update-sequence protection at the end of every 512-byte stride.</summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        if (rec.Length < 48 || U32(rec, 0) != Signature) return false;
        int usaOffset = U16(rec, 4);
        int usaCount = U16(rec, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > rec.Length) return false;
        int stride = rec.Length / (usaCount - 1);
        if (stride < 2) return false;
        ushort usn = U16(rec, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * stride - 2;
            if (pos + 2 > rec.Length || U16(rec, pos) != usn) return false;
            BinaryPrimitives.WriteUInt16LittleEndian(rec[pos..], U16(rec, usaOffset + i * 2));
        }
        return true;
    }

    /// <summary>The number a record stores for itself (since NTFS 3.1), or -1 when it has none.</summary>
    public static long RecordNumber(ReadOnlySpan<byte> rec) =>
        rec.Length >= 0x30 && U16(rec, 4) >= 0x30 ? U32(rec, 0x2C) : -1;

    /// <summary>Where a $FILE_NAME's characters sit in the record; strings are only built for names we keep.</summary>
    private readonly record struct NameRef(uint Parent, byte Namespace, int Offset, int Chars);

    /// <summary>Reads the record header and the attributes diskmap cares about. Call after <see cref="ApplyFixup"/>.</summary>
    public static bool Parse(ReadOnlySpan<byte> rec, ref MftRecordInfo info, List<MftName> names)
    {
        if (rec.Length < 48 || U32(rec, 0) != Signature) return false;
        ushort flags = U16(rec, 0x16);
        info.InUse = (flags & 1) != 0;
        info.IsDirectory = (flags & 2) != 0;
        info.Sequence = U16(rec, 0x10);
        ulong baseRef = U64(rec, 0x20);
        info.IsExtension = baseRef != 0;
        info.BaseRecord = (uint)(baseRef & 0xFFFF_FFFF_FFFF);
        info.BaseSequence = (ushort)(baseRef >> 48);
        if (!info.InUse) return true;

        // Most files carry a long name plus an 8.3 short name; building strings only for the names
        // we keep saves a large share of the allocations on a big volume.
        Span<NameRef> found = stackalloc NameRef[8];
        int foundCount = 0;

        int offset = U16(rec, 0x14);
        int limit = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (offset + 16 <= limit)
        {
            uint type = U32(rec, offset);
            if (type == AttrEnd) break;
            int length = (int)U32(rec, offset + 4);
            if (length < 16 || offset + length > limit) break;
            var attr = rec.Slice(offset, length);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                {
                    var value = ResidentValue(attr);
                    if (value.Length >= 0x24)
                    {
                        info.ModifiedTime = (long)U64(value, 0x08);
                        info.Attributes = U32(value, 0x20);
                        info.HasStandardInfo = true;
                    }
                    break;
                }
                case AttrFileName when !nonResident:
                {
                    int valueLength = (int)U32(attr, 0x10);
                    int valueOffset = U16(attr, 0x14);
                    if (valueLength >= 0x42 && valueOffset + valueLength <= attr.Length)
                    {
                        var value = attr.Slice(valueOffset, valueLength);
                        uint parent = (uint)(U64(value, 0) & 0xFFFF_FFFF_FFFF);
                        int chars = value[0x40];
                        byte ns = value[0x41];
                        if (0x42 + chars * 2 <= value.Length)
                        {
                            var nameRef = new NameRef(parent, ns, offset + valueOffset + 0x42, chars);
                            if (foundCount < found.Length) found[foundCount++] = nameRef;
                            else names.Add(Materialize(rec, nameRef));
                        }
                    }
                    break;
                }
                case AttrData:
                {
                    if (!nonResident)
                    {
                        if (nameLength == 0)
                        {
                            info.LogicalSize = U32(attr, 0x10);
                            info.HasUnnamedData = true;
                        }
                    }
                    else if (attr.Length >= 0x40)
                    {
                        // The header's "allocated size" can't be trusted for streams that are sparse
                        // without saying so: $BadClus:$Bad claims the whole volume. The run list says
                        // exactly which clusters are in use, for every piece of a split attribute.
                        info.AllocatedClusters += AllocatedClusters(attr, U16(attr, 0x20));
                        if (nameLength == 0 && U64(attr, 0x10) == 0)
                        {
                            info.LogicalSize = (long)U64(attr, 0x30);
                            info.HasUnnamedData = true;
                        }
                    }
                    break;
                }
                case AttrIndexAllocation when nonResident:
                    if (attr.Length >= 0x40) info.AllocatedClusters += AllocatedClusters(attr, U16(attr, 0x20));
                    break;
                case AttrReparsePoint:
                    if (!nonResident)
                    {
                        var value = ResidentValue(attr);
                        if (value.Length >= 4) info.ReparseTag = U32(value, 0);
                    }
                    else
                    {
                        info.ReparseTag = 1;
                    }
                    break;
            }
            offset += length;
        }

        bool hasLongName = names.Count > 0;
        for (int i = 0; i < foundCount; i++) hasLongName |= found[i].Namespace != NamespaceDos;
        for (int i = 0; i < foundCount; i++)
        {
            if (hasLongName && found[i].Namespace == NamespaceDos) continue;
            names.Add(Materialize(rec, found[i]));
        }
        return true;
    }

    private static long AllocatedClusters(ReadOnlySpan<byte> attr, int runOffset) =>
        runOffset > 0 && runOffset < attr.Length ? CountAllocatedClusters(attr[runOffset..]) : 0;

    /// <summary>Sums the clusters of a run list, skipping sparse runs (runs without a disk location).</summary>
    internal static long CountAllocatedClusters(ReadOnlySpan<byte> runs)
    {
        long total = 0;
        int i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            int lengthSize = runs[i] & 0x0F;
            int offsetSize = runs[i] >> 4;
            i++;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || i + lengthSize + offsetSize > runs.Length) break;
            long clusters = 0;
            for (int b = 0; b < lengthSize; b++) clusters |= (long)runs[i + b] << (8 * b);
            if (offsetSize != 0 && clusters > 0) total += clusters;
            i += lengthSize + offsetSize;
        }
        return total;
    }

    private static MftName Materialize(ReadOnlySpan<byte> rec, NameRef r) =>
        new(r.Parent, new string(MemoryMarshal.Cast<byte, char>(rec.Slice(r.Offset, r.Chars * 2))), r.Namespace);

    private static ReadOnlySpan<byte> ResidentValue(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < 0x18) return default;
        int length = (int)U32(attr, 0x10);
        int offset = U16(attr, 0x14);
        if (offset + length > attr.Length) return default;
        return attr.Slice(offset, length);
    }

    /// <summary>Decodes the data run list of the unnamed $DATA attribute (used for the $MFT itself).</summary>
    public static List<(long Lcn, long Clusters)>? DataRuns(ReadOnlySpan<byte> rec, out long totalClusters)
    {
        totalClusters = 0;
        int offset = U16(rec, 0x14);
        int limit = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (offset + 16 <= limit)
        {
            uint type = U32(rec, offset);
            if (type == AttrEnd) break;
            int length = (int)U32(rec, offset + 4);
            if (length < 16 || offset + length > limit) break;
            var attr = rec.Slice(offset, length);
            if (type == AttrData && attr[8] != 0 && attr[9] == 0 && U64(attr, 0x10) == 0)
            {
                long lastVcn = (long)U64(attr, 0x18);
                var runs = DecodeRuns(attr[U16(attr, 0x20)..]);
                foreach (var r in runs) totalClusters += r.Clusters;
                return totalClusters >= lastVcn + 1 ? runs : null;
            }
            offset += length;
        }
        return null;
    }

    internal static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        int i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            int lengthSize = runs[i] & 0x0F;
            int offsetSize = runs[i] >> 4;
            i++;
            if (lengthSize == 0 || i + lengthSize + offsetSize > runs.Length) break;
            long clusters = 0;
            for (int b = 0; b < lengthSize; b++) clusters |= (long)runs[i + b] << (8 * b);
            i += lengthSize;
            if (offsetSize == 0)
            {
                result.Add((-1, clusters)); // sparse run
                continue;
            }
            long delta = 0;
            for (int b = 0; b < offsetSize; b++) delta |= (long)runs[i + b] << (8 * b);
            if ((runs[i + offsetSize - 1] & 0x80) != 0) delta |= -1L << (8 * offsetSize);
            i += offsetSize;
            lcn += delta;
            result.Add((lcn, clusters));
        }
        return result;
    }
}
