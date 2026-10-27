using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskMap.Core.Scanning;

internal readonly record struct MftName(uint Parent, string Name, byte Namespace);

/// <summary>What one FILE record (base or extension) contributes.</summary>
internal struct MftRecordInfo
{
    public bool InUse;
    public bool IsDirectory;
    public uint BaseRecord;
    public bool HasUnnamedData;
    public long LogicalSize;
    public long AllocatedSize;
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

    /// <summary>Reads the record header and the attributes diskmap cares about. Call after <see cref="ApplyFixup"/>.</summary>
    public static bool Parse(ReadOnlySpan<byte> rec, ref MftRecordInfo info, List<MftName> names)
    {
        if (rec.Length < 48 || U32(rec, 0) != Signature) return false;
        ushort flags = U16(rec, 0x16);
        info.InUse = (flags & 1) != 0;
        info.IsDirectory = (flags & 2) != 0;
        info.BaseRecord = (uint)(U64(rec, 0x20) & 0xFFFF_FFFF_FFFF);
        if (!info.InUse) return true;

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
            ushort attrFlags = U16(attr, 0x0C);

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
                    var value = ResidentValue(attr);
                    if (value.Length >= 0x42)
                    {
                        uint parent = (uint)(U64(value, 0) & 0xFFFF_FFFF_FFFF);
                        int chars = value[0x40];
                        byte ns = value[0x41];
                        if (0x42 + chars * 2 <= value.Length)
                        {
                            var name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(0x42, chars * 2)));
                            names.Add(new MftName(parent, name, ns));
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
                    else if (attr.Length >= 0x40 && U64(attr, 0x10) == 0)
                    {
                        long allocated = (long)U64(attr, 0x28);
                        long dataSize = (long)U64(attr, 0x30);
                        bool compressedOrSparse = (attrFlags & 0x8001) != 0;
                        if (compressedOrSparse && attr.Length >= 0x48 && U16(attr, 0x20) >= 0x48)
                        {
                            allocated = (long)U64(attr, 0x40);
                        }
                        info.AllocatedSize += allocated;
                        if (nameLength == 0)
                        {
                            info.LogicalSize = dataSize;
                            info.HasUnnamedData = true;
                        }
                    }
                    break;
                }
                case AttrIndexAllocation when nonResident:
                    if (attr.Length >= 0x40 && U64(attr, 0x10) == 0) info.AllocatedSize += (long)U64(attr, 0x28);
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
        return true;
    }

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
