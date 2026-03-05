using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AxfsExplorer;

static class BE
{
    public static byte R8(byte[] d, int o) => d[o];
    public static ushort R16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    public static uint R32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    public static int RI32(byte[] d, int o) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(o));

    public static void W8(byte[] d, int o, byte v) => d[o] = v;
    public static void W16(byte[] d, int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o), v);
    public static void W32(byte[] d, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(o), v);
    public static void WI32(byte[] d, int o, int v) => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(o), v);

    public static string RStr(byte[] d, int o, int n)
    {
        int end = o + n;
        if (end > d.Length) end = d.Length;
        int len = 0;
        for (int i = o; i < end; i++) { if (d[i] == 0) break; len++; }
        return Encoding.ASCII.GetString(d, o, len);
    }

    public static void WStr(byte[] d, int o, string s, int n)
    {
        Array.Clear(d, o, n);
        var bytes = Encoding.ASCII.GetBytes(s ?? "");
        Array.Copy(bytes, 0, d, o, Math.Min(bytes.Length, n));
    }

    public static byte[] Pad(byte[] d, int n)
    {
        if (d.Length >= n) return d[..n];
        var r = new byte[n];
        Array.Copy(d, r, d.Length);
        return r;
    }
}

static class Crc
{
    static readonly uint[] Table;
    static Crc()
    {
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) == 1 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            Table[i] = c;
        }
    }

    public static uint Crc32(byte[] data, int offset = 0, int length = -1)
    {
        if (length < 0) length = data.Length - offset;
        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + length; i++)
            crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        return crc ^ 0xFFFFFFFF;
    }

    public static uint AmigaChecksum(byte[] block, int csOffset)
    {
        uint sum = 0;
        for (int i = 0; i < block.Length; i += 4)
            if (i != csOffset) sum += BE.R32(block, i);
        return (uint)(0x100000000L - sum);
    }

    public static bool AmigaVerify(byte[] block)
    {
        uint sum = 0;
        for (int i = 0; i < block.Length; i += 4) sum += BE.R32(block, i);
        return sum == 0;
    }
}

static class BinExtractor
{
    public static (string? tempPath, List<string> log) TryExtract(string binPath)
    {
        var log = new List<string>();
        byte[] raw;
        try { raw = File.ReadAllBytes(binPath); }
        catch (Exception ex) { log.Add($"Cannot read file: {ex.Message}"); return (null, log); }
        log.Add($"File size: {raw.Length:N0} bytes");
        if (raw.Length >= 4 && Encoding.ASCII.GetString(raw, 0, 4) == "RDSK") { log.Add("Raw RDSK"); return (null, log); }
        if (raw.Length >= 4 && Encoding.ASCII.GetString(raw, 0, 4) == "AXF2") { log.Add("Raw AXF2"); return (null, log); }
        byte[]? decompressed = TryGZip(raw, log);
        if (decompressed == null) decompressed = TryDeflate(raw, log);
        if (decompressed == null && raw.Length > 16)
        {
            for (int skip = 1; skip <= 16; skip++)
            {
                decompressed = TryGZip(raw.AsSpan(skip).ToArray(), null);
                if (decompressed != null) { log.Add($"GZip after skip {skip}"); break; }
                decompressed = TryDeflate(raw.AsSpan(skip).ToArray(), null);
                if (decompressed != null) { log.Add($"Deflate after skip {skip}"); break; }
            }
        }
        if (decompressed == null) { log.Add("Not compressed"); return (null, log); }
        log.Add($"Decompressed: {decompressed.Length:N0} bytes");
        int rdskOffset = ScanForMagic(decompressed, "RDSK");
        if (rdskOffset >= 0) { log.Add($"RDSK at {rdskOffset}"); return (WriteTempFile(decompressed, rdskOffset, log), log); }
        int axfsOffset = ScanForMagic(decompressed, "AXF2");
        if (axfsOffset >= 0) { log.Add($"AXF2 at {axfsOffset}"); return (WriteTempFile(decompressed, axfsOffset, log), log); }
        if (decompressed.Length >= 512) { log.Add("No magic found"); return (WriteTempFile(decompressed, 0, log), log); }
        log.Add("Too small"); return (null, log);
    }
    static byte[]? TryGZip(byte[] data, List<string>? log) { if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B) return null; try { using var ms = new MemoryStream(data); using var gz = new GZipStream(ms, CompressionMode.Decompress); using var o = new MemoryStream(); gz.CopyTo(o); log?.Add($"GZip: {data.Length:N0} -> {o.Length:N0}"); return o.ToArray(); } catch { return null; } }
    static byte[]? TryDeflate(byte[] data, List<string>? log) { try { using var ms = new MemoryStream(data); using var df = new DeflateStream(ms, CompressionMode.Decompress); using var o = new MemoryStream(); df.CopyTo(o); if (o.Length <= data.Length && o.Length < 512) return null; log?.Add($"Deflate: {data.Length:N0} -> {o.Length:N0}"); return o.ToArray(); } catch { return null; } }
    static int ScanForMagic(byte[] data, string magic) { var mb = Encoding.ASCII.GetBytes(magic); for (int i = 0; i + mb.Length <= data.Length; i += 512) if (data.AsSpan(i, mb.Length).SequenceEqual(mb)) return i; for (int i = 0; i + mb.Length <= data.Length; i++) if (data.AsSpan(i, mb.Length).SequenceEqual(mb)) return i; return -1; }
    static string? WriteTempFile(byte[] data, int offset, List<string> log) { try { int ds = Math.Min(data.Length - offset, data.Length - offset); ds = (ds / 512) * 512; var tp = Path.Combine(Path.GetTempPath(), $"axfs_{Guid.NewGuid():N}.img"); File.WriteAllBytes(tp, data[offset..(offset + ds)]); log.Add($"{ds:N0} bytes written"); return tp; } catch (Exception ex) { log.Add($"Error: {ex.Message}"); return null; } }
}

class RdbHeader
{
    public uint Flags; public int PartList = -1;
    public uint Cylinders = 1, Sectors = 1, Heads = 1;
    public uint BlockBytes;
    public string Vendor = "", Product = "", Revision = "", Label = "";
    public uint TotalSectors, Generation;
}

class RdbPartition
{
    // ── Flag constants ──
    public const uint FLAG_BOOTABLE = 0x01;
    public const uint FLAG_AUTOMOUNT = 0x02;
    public const uint FLAG_READONLY = 0x04;

    public int Sector; public int Next = -1; public uint Flags;
    public string DeviceName = "DH0";
    public uint StartSector, SizeSectors, FsType;
    public int BootPriority; public uint DhIndex; public string FsLabel = "";
    public string FsTypeName => FsType switch
    {
        0x41584632 => "AXFS v2",
        0x41584631 => "AXFS v1",
        0x41584546 => "AXEFI",
        0x46415400 => "FAT",
        0x53575000 => "Swap",
        0 => "Raw",
        _ => $"0x{FsType:X8}"
    };
    public bool IsEfi => FsType == 0x41584546;
    public bool IsBootable => (Flags & FLAG_BOOTABLE) != 0;
    public bool IsReadOnly => (Flags & FLAG_READONLY) != 0;
    public bool IsAutoMount => (Flags & FLAG_AUTOMOUNT) != 0;
}

/// <summary>Writes partition metadata back to disk (flags, label).</summary>
static class RdbWriter
{
    public static bool WritePartitionFlags(DiskImage disk, RdbPartition part, uint newFlags)
    {
        var sector = disk.ReadSector(part.Sector);
        if (sector == null) return false;

        // Work on first 256 bytes (Amiga block)
        var block = new byte[256];
        Array.Copy(sector, block, Math.Min(sector.Length, 256));
        if (Encoding.ASCII.GetString(block, 0, 4) != "PART") return false;

        BE.W32(block, 20, newFlags);

        // Recompute Amiga checksum
        BE.W32(block, 8, 0);
        BE.W32(block, 8, Crc.AmigaChecksum(block, 8));

        Array.Copy(block, 0, sector, 0, 256);
        disk.WriteSector(part.Sector, BE.Pad(sector, disk.SectorSize));
        disk.Save();
        part.Flags = newFlags;
        return true;
    }

    public static bool WritePartitionLabel(DiskImage disk, RdbPartition part, string newLabel)
    {
        var sector = disk.ReadSector(part.Sector);
        if (sector == null) return false;

        var block = new byte[256];
        Array.Copy(sector, block, Math.Min(sector.Length, 256));
        if (Encoding.ASCII.GetString(block, 0, 4) != "PART") return false;

        BE.WStr(block, 80, newLabel, 16);

        BE.W32(block, 8, 0);
        BE.W32(block, 8, Crc.AmigaChecksum(block, 8));

        Array.Copy(block, 0, sector, 0, 256);
        disk.WriteSector(part.Sector, BE.Pad(sector, disk.SectorSize));
        disk.Save();
        part.FsLabel = newLabel;
        return true;
    }

    public static bool WritePartitionBootPriority(DiskImage disk, RdbPartition part, int newPriority)
    {
        var sector = disk.ReadSector(part.Sector);
        if (sector == null) return false;

        var block = new byte[256];
        Array.Copy(sector, block, Math.Min(sector.Length, 256));
        if (Encoding.ASCII.GetString(block, 0, 4) != "PART") return false;

        BE.WI32(block, 68, newPriority);

        BE.W32(block, 8, 0);
        BE.W32(block, 8, Crc.AmigaChecksum(block, 8));

        Array.Copy(block, 0, sector, 0, 256);
        disk.WriteSector(part.Sector, BE.Pad(sector, disk.SectorSize));
        disk.Save();
        part.BootPriority = newPriority;
        return true;
    }
}

class RdbDisk
{
    public RdbHeader? Header; public List<RdbPartition> Partitions = new(); public int SectorSize;
    public static RdbDisk? Read(DiskImage disk)
    {
        var sec0 = disk.ReadSector(0);
        if (sec0 == null || sec0.Length < 128) return null;
        if (Encoding.ASCII.GetString(sec0, 0, 4) != "RDSK") return null;
        var block = BE.Pad(sec0, 256);
        var rdb = new RdbDisk { SectorSize = disk.SectorSize };
        rdb.Header = new RdbHeader
        {
            Flags = BE.R32(block, 20),
            PartList = BE.RI32(block, 24),
            BlockBytes = BE.R32(block, 16),
            Cylinders = BE.R32(block, 40),
            Sectors = BE.R32(block, 44),
            Heads = BE.R32(block, 48),
            Vendor = BE.RStr(block, 64, 16),
            Product = BE.RStr(block, 80, 16),
            Revision = BE.RStr(block, 96, 4),
            Label = BE.RStr(block, 100, 16),
            TotalSectors = BE.R32(block, 116),
            Generation = BE.R32(block, 120),
        };
        int ns = rdb.Header.PartList;
        for (int safety = 0; ns >= 0 && safety < 16; safety++)
        {
            var ps = disk.ReadSector(ns);
            if (ps == null || ps.Length < 96) break;
            var pb = BE.Pad(ps, 256);
            if (Encoding.ASCII.GetString(pb, 0, 4) != "PART") break;
            int nameLen = Math.Min(pb[24], (byte)30);
            var part = new RdbPartition
            {
                Sector = ns,
                Next = BE.RI32(pb, 16),
                Flags = BE.R32(pb, 20),
                DeviceName = Encoding.ASCII.GetString(pb, 25, nameLen).TrimEnd('\0'),
                StartSector = BE.R32(pb, 56),
                SizeSectors = BE.R32(pb, 60),
                FsType = BE.R32(pb, 64),
                BootPriority = BE.RI32(pb, 68),
                DhIndex = BE.R32(pb, 76),
                FsLabel = BE.RStr(pb, 80, 16),
            };
            rdb.Partitions.Add(part);
            ns = part.Next;
        }
        return rdb;
    }
}

class AxfsSuperblock
{
    public byte Version; public ushort SectorSize; public uint TotalSectors;
    public ushort MaxInodes, MaxBlocks, FreeInodes, FreeBlocks;
    public ushort DataStart, ItableStart, BbmpStart; public byte BbmpSec;
    public string Label = ""; public uint Ctime, Mtime, Generation; public ushort Flags;
    public bool CrcValid;
}

class AxfsInode
{
    public const int SIZE = 80; public const byte F_INLINE = 0x01;
    public ushort IType, Mode, Uid, Gid; public uint Size, Ctime, Mtime;
    public ushort Links; public byte Flags, NExtents;
    public (ushort Start, ushort Count)[] Extents = Array.Empty<(ushort, ushort)>();
    public ushort Indirect; public byte[]? InlineData;
    public bool IsInline => (Flags & F_INLINE) != 0;
    public bool IsFile => IType == 1; public bool IsDir => IType == 2;
}

class AxfsDirEntry { public const int SIZE = 32; public const int NAME_MAX = 27; public ushort Inode; public byte IType; public string Name = ""; }

class AxfsVolume
{
    readonly DiskImage _disk; readonly uint _partOffset;
    public AxfsSuperblock Super { get; private set; } = null!;
    int _ips, _dpb, _ppb;
    byte[] _ibmp = Array.Empty<byte>(); byte[][] _bbmp = Array.Empty<byte[]>(); bool _dirty;

    AxfsVolume(DiskImage disk, uint partOffset) { _disk = disk; _partOffset = partOffset; }
    byte[] PartReadSector(int n) => _disk.ReadSector((int)(_partOffset + n)) ?? new byte[_disk.SectorSize];
    void PartWriteSector(int n, byte[] data) => _disk.WriteSector((int)(_partOffset + n), data);

    public static AxfsVolume? Mount(DiskImage disk, uint partOffset)
    {
        var vol = new AxfsVolume(disk, partOffset);
        AxfsSuperblock? bestSb = null;
        for (int secIdx = 0; secIdx <= 1; secIdx++)
        {
            var sb = vol.PartReadSector(secIdx);
            if (sb.Length < 60) continue;
            if (Encoding.ASCII.GetString(sb, 0, 4) != "AXF2") continue;
            var parsed = new AxfsSuperblock
            {
                Version = sb[4],
                SectorSize = BE.R16(sb, 5),
                TotalSectors = BE.R32(sb, 7),
                MaxInodes = BE.R16(sb, 11),
                MaxBlocks = BE.R16(sb, 13),
                FreeInodes = BE.R16(sb, 15),
                FreeBlocks = BE.R16(sb, 17),
                DataStart = BE.R16(sb, 19),
                ItableStart = BE.R16(sb, 21),
                BbmpStart = BE.R16(sb, 23),
                BbmpSec = sb[25],
                Label = BE.RStr(sb, 26, 16),
                Ctime = BE.R32(sb, 42),
                Mtime = BE.R32(sb, 46),
                Generation = BE.R32(sb, 50),
                Flags = BE.R16(sb, 54),
                CrcValid = BE.R32(sb, 56) == Crc.Crc32(sb, 0, 56),
            };
            if (bestSb == null || (!bestSb.CrcValid && parsed.CrcValid) ||
                (bestSb.CrcValid == parsed.CrcValid && parsed.Generation > bestSb.Generation))
            { bestSb = parsed; }
        }
        if (bestSb == null) return null;
        vol.Super = bestSb;
        int ss = bestSb.SectorSize;
        if (ss == 0 || ss < 128 || ss > 4096) ss = disk.SectorSize;
        if (bestSb.DataStart == 0 || bestSb.ItableStart == 0) return null;
        if (bestSb.DataStart <= bestSb.ItableStart) return null;
        if (bestSb.MaxInodes == 0 || bestSb.MaxBlocks == 0) return null;
        if (bestSb.FreeBlocks > bestSb.MaxBlocks) bestSb.FreeBlocks = bestSb.MaxBlocks;
        if (bestSb.FreeInodes > bestSb.MaxInodes) bestSb.FreeInodes = bestSb.MaxInodes;

        vol._ips = ss / AxfsInode.SIZE; vol._dpb = ss / AxfsDirEntry.SIZE; vol._ppb = ss / 4;
        if (vol._ips <= 0) vol._ips = 1; if (vol._dpb <= 0) vol._dpb = 1; if (vol._ppb <= 0) vol._ppb = 1;
        vol._ibmp = vol.PartReadSector(2);
        vol._bbmp = new byte[bestSb.BbmpSec][];
        for (int i = 0; i < bestSb.BbmpSec; i++) vol._bbmp[i] = vol.PartReadSector(bestSb.BbmpStart + i);
        return vol;
    }

    public AxfsInode? ReadInode(int n)
    {
        if (n < 0 || n >= Super.MaxInodes) return null;
        int sec = Super.ItableStart + n / _ips, off = (n % _ips) * AxfsInode.SIZE;
        var sd = PartReadSector(sec);
        if (sd.Length < off + AxfsInode.SIZE) return null;
        var ino = new AxfsInode
        {
            IType = BE.R16(sd, off),
            Mode = BE.R16(sd, off + 2),
            Uid = BE.R16(sd, off + 4),
            Gid = BE.R16(sd, off + 6),
            Size = BE.R32(sd, off + 8),
            Ctime = BE.R32(sd, off + 12),
            Mtime = BE.R32(sd, off + 16),
            Links = BE.R16(sd, off + 20),
            Flags = sd[off + 22],
            NExtents = sd[off + 23],
            Indirect = BE.R16(sd, off + 76)
        };
        if (ino.IType > 3) return null;
        if (ino.IsInline) { int dl = (int)Math.Min(ino.Size, 52); ino.InlineData = new byte[dl]; Array.Copy(sd, off + 24, ino.InlineData, 0, dl); }
        else
        {
            int ne = Math.Min((int)ino.NExtents, 13);
            ino.Extents = new (ushort, ushort)[ne];
            for (int i = 0; i < ne; i++) { int eo = off + 24 + i * 4; ino.Extents[i] = (BE.R16(sd, eo), BE.R16(sd, eo + 2)); }
        }
        return ino;
    }

    void WriteInode(int n, AxfsInode ino)
    {
        int ss = _disk.SectorSize, sec = Super.ItableStart + n / _ips, off = (n % _ips) * AxfsInode.SIZE;
        var sd = PartReadSector(sec); if (sd.Length < ss) sd = BE.Pad(sd, ss);
        BE.W16(sd, off, ino.IType); BE.W16(sd, off + 2, ino.Mode);
        BE.W16(sd, off + 4, ino.Uid); BE.W16(sd, off + 6, ino.Gid);
        BE.W32(sd, off + 8, ino.Size); BE.W32(sd, off + 12, ino.Ctime);
        BE.W32(sd, off + 16, ino.Mtime); BE.W16(sd, off + 20, ino.Links);
        sd[off + 22] = ino.Flags; sd[off + 23] = ino.NExtents;
        if (ino.IsInline && ino.InlineData != null) { Array.Clear(sd, off + 24, 52); Array.Copy(ino.InlineData, 0, sd, off + 24, Math.Min(ino.InlineData.Length, 52)); }
        else { Array.Clear(sd, off + 24, 52); for (int i = 0; i < Math.Min((int)ino.NExtents, 13); i++) if (i < ino.Extents.Length) { BE.W16(sd, off + 24 + i * 4, ino.Extents[i].Start); BE.W16(sd, off + 24 + i * 4 + 2, ino.Extents[i].Count); } }
        BE.W16(sd, off + 76, ino.Indirect);
        PartWriteSector(sec, sd);
    }

    byte[] ReadBlock(int n) => PartReadSector(Super.DataStart + n);
    void WriteBlock(int n, byte[] d) => PartWriteSector(Super.DataStart + n, BE.Pad(d, _disk.SectorSize));

    List<int> GetBlocks(AxfsInode ino)
    {
        var r = new List<int>(); if (ino.IsInline) return r;
        foreach (var (s, c) in ino.Extents) { if (c == 0) continue; for (int j = 0; j < c && r.Count < 65536; j++) { int blk = s + j; if (blk >= 0 && blk < Super.MaxBlocks + 16) r.Add(blk); } }
        if (ino.NExtents > 13 && ino.Indirect > 0 && ino.Indirect < Super.MaxBlocks) { var si = ReadBlock(ino.Indirect); for (int i = 0; i < _ppb; i++) { ushort es = BE.R16(si, i * 4), ec = BE.R16(si, i * 4 + 2); if (ec == 0) continue; for (int j = 0; j < ec && r.Count < 65536; j++) { int blk = es + j; if (blk >= 0 && blk < Super.MaxBlocks + 16) r.Add(blk); } } }
        return r;
    }

    static bool BGet(byte[] bmp, int n) { int by = n / 8, bi = n % 8; if (by >= bmp.Length) return false; return ((bmp[by] >> bi) & 1) == 1; }
    static void BSet(byte[] bmp, int n, bool v) { int by = n / 8, bi = n % 8; if (by >= bmp.Length) return; if (v) bmp[by] |= (byte)(1 << bi); else bmp[by] &= (byte)~(1 << bi); }
    bool BbGet(int n) { int ss = _disk.SectorSize, bps = ss * 8, si = n / bps, bi = n % bps; return si < _bbmp.Length && BGet(_bbmp[si], bi); }
    void BbSet(int n, bool v) { int ss = _disk.SectorSize, bps = ss * 8, si = n / bps, bi = n % bps; if (si < _bbmp.Length) BSet(_bbmp[si], bi, v); }

    int? AllocInode() { for (int i = 0; i < Super.MaxInodes; i++) if (!BGet(_ibmp, i)) { BSet(_ibmp, i, true); Super.FreeInodes = (ushort)Math.Max(0, Super.FreeInodes - 1); _dirty = true; return i; } return null; }
    void FreeInode(int n) { if (BGet(_ibmp, n)) { BSet(_ibmp, n, false); Super.FreeInodes = (ushort)Math.Min(Super.MaxInodes, Super.FreeInodes + 1); _dirty = true; } }
    int? AllocBlock() { for (int i = 0; i < Super.MaxBlocks; i++) if (!BbGet(i)) { BbSet(i, true); Super.FreeBlocks = (ushort)Math.Max(0, Super.FreeBlocks - 1); _dirty = true; return i; } return null; }
    void FreeBlock(int n) { if (n >= 0 && n < Super.MaxBlocks && BbGet(n)) { BbSet(n, false); Super.FreeBlocks = (ushort)Math.Min(Super.MaxBlocks, Super.FreeBlocks + 1); _dirty = true; } }
    void FreeInodeBlocks(AxfsInode ino) { if (ino.IsInline) return; foreach (var b in GetBlocks(ino)) FreeBlock(b); if (ino.Indirect > 0) FreeBlock(ino.Indirect); }

    int? DirLookup(AxfsInode di, string name)
    {
        foreach (int bn in GetBlocks(di))
        {
            var sd = ReadBlock(bn);
            for (int i = 0; i < _dpb; i++)
            {
                int o = i * AxfsDirEntry.SIZE; ushort inoNum = BE.R16(sd, o);
                if (inoNum == 0 || inoNum >= Super.MaxInodes) continue;
                int nl = sd[o + 3]; if (nl > AxfsDirEntry.NAME_MAX) nl = AxfsDirEntry.NAME_MAX; if (nl <= 0) continue;
                string n = Encoding.ASCII.GetString(sd, o + 4, nl).TrimEnd('\0');
                if (n == name) return inoNum;
            }
        }
        return null;
    }

    public int? Resolve(string path)
    {
        int cur = 1; var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in parts)
        {
            if (seg == "." || seg == "") continue;
            if (seg == "..") { var pi = ReadInode(cur); if (pi == null || !pi.IsDir) return null; cur = DirLookup(pi, "..") ?? 1; continue; }
            var di = ReadInode(cur); if (di == null || !di.IsDir) return null;
            var next = DirLookup(di, seg); if (next == null) return null; cur = next.Value;
        }
        return cur;
    }

    (int? parentIno, string baseName) ResolvParent(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return (1, "");
        string baseName = parts[^1]; parts.RemoveAt(parts.Count - 1);
        int? pino = Resolve("/" + string.Join("/", parts));
        return (pino, baseName);
    }

    static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (char c in name) if (c < 32 || c > 126) return false;
        return true;
    }

    public List<AxfsDirEntry> ListDir(string path)
    {
        int? n = Resolve(path); if (n == null) return new();
        var ino = ReadInode(n.Value); if (ino == null || !ino.IsDir) return new();
        var result = new List<AxfsDirEntry>();
        foreach (int bn in GetBlocks(ino))
        {
            var sd = ReadBlock(bn);
            for (int i = 0; i < _dpb; i++)
            {
                int o = i * AxfsDirEntry.SIZE; ushort inoNum = BE.R16(sd, o);
                if (inoNum == 0) continue;
                if (inoNum >= Super.MaxInodes) continue;
                if (!BGet(_ibmp, inoNum)) continue;
                byte iType = sd[o + 2];
                if (iType == 0 || iType > 3) continue;
                int nl = Math.Min((int)sd[o + 3], AxfsDirEntry.NAME_MAX); if (nl <= 0) continue;
                string name = Encoding.ASCII.GetString(sd, o + 4, nl).TrimEnd('\0');
                if (name == "." || name == "..") continue;
                if (!IsValidName(name)) continue;
                var refIno = ReadInode(inoNum);
                if (refIno == null || refIno.IType == 0) continue;
                result.Add(new AxfsDirEntry { Inode = inoNum, IType = iType, Name = name });
            }
        }
        return result.OrderBy(e => e.IType != 2).ThenBy(e => e.Name).ToList();
    }

    public byte[]? ReadFile(string path)
    {
        int? n = Resolve(path); if (n == null) return null;
        var ino = ReadInode(n.Value); if (ino == null || !ino.IsFile) return null;
        if (ino.IsInline) return ino.InlineData?[..(int)Math.Min(ino.Size, ino.InlineData.Length)] ?? Array.Empty<byte>();
        int ss = _disk.SectorSize; using var ms = new MemoryStream(); int rem = (int)ino.Size;
        foreach (int bn in GetBlocks(ino)) { var sd = ReadBlock(bn); int take = Math.Min(rem, ss); ms.Write(sd, 0, take); rem -= take; if (rem <= 0) break; }
        return ms.ToArray();
    }

    public bool WriteFile(string path, byte[] data)
    {
        var (parentIno, baseName) = ResolvParent(path);
        if (parentIno == null || baseName.Length == 0 || baseName.Length > AxfsDirEntry.NAME_MAX) return false;
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); int ss = _disk.SectorSize;
        var pino = ReadInode(parentIno.Value); if (pino == null || !pino.IsDir) return false;
        int? existing = DirLookup(pino, baseName); int inoNum;
        if (existing != null) { inoNum = existing.Value; var old = ReadInode(inoNum); if (old != null) FreeInodeBlocks(old); }
        else { var alloc = AllocInode(); if (alloc == null) return false; inoNum = alloc.Value; }
        var ino = new AxfsInode { IType = 1, Mode = 0x1B6, Size = (uint)data.Length, Ctime = now, Mtime = now, Links = 1 };
        if (data.Length <= 52) { ino.Flags = AxfsInode.F_INLINE; ino.InlineData = new byte[data.Length]; Array.Copy(data, ino.InlineData, data.Length); }
        else
        {
            int nBlocks = (data.Length + ss - 1) / ss; var extents = new List<(ushort Start, ushort Count)>(); int written = 0, rem = nBlocks;
            while (rem > 0) { int? bn = AllocBlock(); if (bn == null) return false; WriteBlock(bn.Value, data.AsSpan(written * ss, Math.Min(ss, data.Length - written * ss)).ToArray()); if (extents.Count > 0 && extents[^1].Start + extents[^1].Count == bn.Value) extents[^1] = (extents[^1].Start, (ushort)(extents[^1].Count + 1)); else extents.Add(((ushort)bn.Value, 1)); written++; rem--; }
            ino.NExtents = (byte)extents.Count; ino.Extents = extents.ToArray();
        }
        WriteInode(inoNum, ino);
        if (existing == null) DirAdd(parentIno.Value, (ushort)inoNum, 1, baseName);
        _dirty = true; Flush(); return true;
    }

    public bool Mkdir(string path)
    {
        var (parentIno, baseName) = ResolvParent(path);
        if (parentIno == null || baseName.Length == 0) return false;
        var pino = ReadInode(parentIno.Value); if (pino == null || !pino.IsDir) return false;
        if (DirLookup(pino, baseName) != null) return false;
        int? inoNum = AllocInode(); if (inoNum == null) return false;
        int? bn = AllocBlock(); if (bn == null) { FreeInode(inoNum.Value); return false; }
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); int ss = _disk.SectorSize;
        var dirBlock = new byte[ss];
        WriteDirEntry(dirBlock, 0, (ushort)inoNum.Value, 2, ".");
        WriteDirEntry(dirBlock, AxfsDirEntry.SIZE, (ushort)parentIno.Value, 2, "..");
        WriteBlock(bn.Value, dirBlock);
        var ino = new AxfsInode { IType = 2, Mode = 0x1FF, Size = (uint)(AxfsDirEntry.SIZE * 2), Ctime = now, Mtime = now, Links = 2, NExtents = 1, Extents = new[] { ((ushort)bn.Value, (ushort)1) } };
        WriteInode(inoNum.Value, ino); DirAdd(parentIno.Value, (ushort)inoNum.Value, 2, baseName);
        _dirty = true; Flush(); return true;
    }

    public bool Remove(string path)
    {
        var (parentIno, baseName) = ResolvParent(path);
        if (parentIno == null || baseName.Length == 0) return false;
        var pino = ReadInode(parentIno.Value); if (pino == null) return false;
        int? n = DirLookup(pino, baseName); if (n == null) return false;
        var ino = ReadInode(n.Value); if (ino == null) return false;
        if (ino.IsDir)
        {
            var entries = ListDir(path);
            foreach (var entry in entries)
            {
                string childPath = path.TrimEnd('/') + "/" + entry.Name;
                if (!Remove(childPath)) return false;
            }
        }
        FreeInodeBlocks(ino);
        ino.IType = 0; WriteInode(n.Value, ino);
        FreeInode(n.Value); DirRemove(parentIno.Value, baseName);
        _dirty = true; Flush(); return true;
    }

    static void WriteDirEntry(byte[] sd, int o, ushort inoNum, byte iType, string name)
    {
        name = name.Length > AxfsDirEntry.NAME_MAX ? name[..AxfsDirEntry.NAME_MAX] : name;
        BE.W16(sd, o, inoNum); sd[o + 2] = iType; sd[o + 3] = (byte)name.Length;
        Array.Clear(sd, o + 4, 28); Encoding.ASCII.GetBytes(name).CopyTo(sd, o + 4);
    }

    bool DirAdd(int dirIno, ushort childIno, byte childType, string name)
    {
        var di = ReadInode(dirIno); if (di == null) return false;
        foreach (int bn in GetBlocks(di))
        {
            var sd = ReadBlock(bn);
            for (int i = 0; i < _dpb; i++) { int o = i * AxfsDirEntry.SIZE; if (BE.R16(sd, o) == 0) { WriteDirEntry(sd, o, childIno, childType, name); WriteBlock(bn, sd); di.Size += (uint)AxfsDirEntry.SIZE; di.Mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); WriteInode(dirIno, di); return true; } }
        }
        int? nb = AllocBlock(); if (nb == null) return false;
        var newBlock = new byte[_disk.SectorSize]; WriteDirEntry(newBlock, 0, childIno, childType, name); WriteBlock(nb.Value, newBlock);
        var exts = di.Extents.ToList(); exts.Add(((ushort)nb.Value, (ushort)1)); di.Extents = exts.ToArray(); di.NExtents = (byte)exts.Count;
        di.Size += (uint)AxfsDirEntry.SIZE; WriteInode(dirIno, di); return true;
    }

    bool DirRemove(int dirIno, string name)
    {
        var di = ReadInode(dirIno); if (di == null) return false;
        foreach (int bn in GetBlocks(di))
        {
            var sd = ReadBlock(bn);
            for (int i = 0; i < _dpb; i++)
            {
                int o = i * AxfsDirEntry.SIZE; ushort inoNum = BE.R16(sd, o); if (inoNum == 0) continue;
                int nl = Math.Min((int)sd[o + 3], AxfsDirEntry.NAME_MAX);
                string n = Encoding.ASCII.GetString(sd, o + 4, nl).TrimEnd('\0');
                if (n == name) { Array.Clear(sd, o, AxfsDirEntry.SIZE); WriteBlock(bn, sd); WriteInode(dirIno, di); return true; }
            }
        }
        return false;
    }

    public void Flush()
    {
        if (!_dirty) return; int ss = _disk.SectorSize;
        PartWriteSector(2, BE.Pad(_ibmp, ss));
        for (int i = 0; i < _bbmp.Length; i++) PartWriteSector(Super.BbmpStart + i, BE.Pad(_bbmp[i], ss));
        Super.Mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); Super.Generation++;
        var sb = WriteSuperblock(); PartWriteSector(0, sb); PartWriteSector(1, sb); _dirty = false;
    }

    byte[] WriteSuperblock()
    {
        int ss = _disk.SectorSize; var sb = new byte[ss];
        Encoding.ASCII.GetBytes("AXF2").CopyTo(sb, 0); sb[4] = Super.Version;
        BE.W16(sb, 5, Super.SectorSize); BE.W32(sb, 7, Super.TotalSectors);
        BE.W16(sb, 11, Super.MaxInodes); BE.W16(sb, 13, Super.MaxBlocks);
        BE.W16(sb, 15, Super.FreeInodes); BE.W16(sb, 17, Super.FreeBlocks);
        BE.W16(sb, 19, Super.DataStart); BE.W16(sb, 21, Super.ItableStart);
        BE.W16(sb, 23, Super.BbmpStart); sb[25] = Super.BbmpSec;
        BE.WStr(sb, 26, Super.Label, 16); BE.W32(sb, 42, Super.Ctime);
        BE.W32(sb, 46, Super.Mtime); BE.W32(sb, 50, Super.Generation);
        BE.W16(sb, 54, Super.Flags); BE.W32(sb, 56, Crc.Crc32(sb, 0, 56));
        return sb;
    }

    public AxfsInode? Stat(string path) { int? n = Resolve(path); if (n == null) return null; return ReadInode(n.Value); }
}

class DiskImage : IDisposable
{
    FileStream _fs; public int SectorSize { get; }
    public int SectorCount { get; }
    public string FilePath { get; }
    public string? ExtractedFromBin { get; set; }
    DiskImage(FileStream fs, int sectorSize, string path) { _fs = fs; SectorSize = sectorSize; SectorCount = (int)(fs.Length / sectorSize); FilePath = path; }
    public static DiskImage Open(string path, int ss = 512) => new(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read), ss, path);
    public static DiskImage OpenReadOnly(string path, int ss = 512) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ss, path);
    public static DiskImage Create(string path, int totalSectors, int ss = 512) { var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read); fs.SetLength((long)totalSectors * ss); return new DiskImage(fs, ss, path); }
    public byte[] ReadSector(int n) { long o = (long)n * SectorSize; if (o + SectorSize > _fs.Length) return new byte[SectorSize]; var buf = new byte[SectorSize]; _fs.Position = o; _fs.ReadExactly(buf, 0, SectorSize); return buf; }
    public void WriteSector(int n, byte[] data) { long o = (long)n * SectorSize; _fs.Position = o; _fs.Write(data, 0, Math.Min(data.Length, SectorSize)); if (data.Length < SectorSize) _fs.Write(new byte[SectorSize - data.Length], 0, SectorSize - data.Length); }
    public void Save() => _fs.Flush();
    public void Dispose() { _fs?.Flush(); _fs?.Dispose(); }
}

record ScanResult(RdbDisk? Rdb, AxfsVolume? Vol, uint AxfsOffset, List<string> Log);

static class ImageScanner
{
    public static ScanResult Scan(DiskImage disk)
    {
        var log = new List<string>();
        log.Add($"File: {Path.GetFileName(disk.FilePath)}"); log.Add($"Size: {(long)disk.SectorCount * disk.SectorSize:N0} bytes");
        var sec0 = disk.ReadSector(0);
        RdbDisk? rdb = null;
        try { rdb = RdbDisk.Read(disk); } catch (Exception ex) { log.Add($"RDB error: {ex.Message}"); }
        if (rdb != null)
        {
            log.Add($"RDB: \"{rdb.Header?.Label}\" partitions={rdb.Partitions.Count}");
            foreach (var p in rdb.Partitions)
            {
                log.Add($"  {p.DeviceName}: {p.FsTypeName} \"{p.FsLabel}\" start={p.StartSector} size={p.SizeSectors}");
                if (p.FsType == 0x41584632)
                {
                    try
                    {
                        var vol = AxfsVolume.Mount(disk, p.StartSector);
                        if (vol != null) { log.Add($"  AXFS mounted: \"{vol.Super.Label}\""); return new ScanResult(rdb, vol, p.StartSector, log); }
                        log.Add($"  AXFS mount failed at sector {p.StartSector}");
                    }
                    catch (Exception ex) { log.Add($"  Error: {ex.Message}"); }
                }
            }
            return new ScanResult(rdb, null, 0, log);
        }
        var offsets = new int[] { 0, 1, 2, 16, 17, 18, 32, 33, 64, 65, 128, 129 };
        foreach (int sec in offsets)
        {
            if (sec >= disk.SectorCount) continue;
            try { var data = disk.ReadSector(sec); if (data.Length >= 4 && Encoding.ASCII.GetString(data, 0, 4) == "AXF2") { var vol = AxfsVolume.Mount(disk, (uint)sec); if (vol != null) { log.Add($"AXF2 at sector {sec}"); return new ScanResult(null, vol, (uint)sec, log); } } } catch { }
        }
        log.Add("No filesystem found"); return new ScanResult(null, null, 0, log);
    }
}

class FileItem
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "\uE8A5";
    public bool IsDir { get; set; }
    public uint Size { get; set; }
    public uint Mtime { get; set; }
    public ushort InodeNum { get; set; }

    // ── Fancy visual properties ──
    public string PreviewText { get; set; } = "";
    public bool HasPreview { get; set; }
    public Visibility PreviewVisibility => HasPreview ? Visibility.Visible : Visibility.Collapsed;

    public string SizeText => IsDir ? "" : FormatSize(Size);
    public string TypeText => IsDir ? "Folder" : GetExtType();
    public string DateText
    {
        get
        {
            if (Mtime == 0) return "";
            try { return DateTimeOffset.FromUnixTimeSeconds(Mtime).LocalDateTime.ToString("yyyy-MM-dd HH:mm"); }
            catch { return ""; }
        }
    }

    // Color-coded icon backgrounds
    public SolidColorBrush IconBackground => new(GetIconBgColor());
    public SolidColorBrush IconForeground => new(GetIconFgColor());

    Windows.UI.Color GetIconBgColor()
    {
        if (IsDir) return ColorHelper.FromArgb(20, 255, 213, 79);
        if (Name == "..") return ColorHelper.FromArgb(15, 128, 128, 128);
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        return ext switch
        {
            ".lua" => ColorHelper.FromArgb(20, 79, 195, 247),
            ".cfg" or ".ini" or ".toml" => ColorHelper.FromArgb(20, 255, 183, 77),
            ".txt" or ".md" => ColorHelper.FromArgb(20, 129, 199, 132),
            ".log" or ".vbl" => ColorHelper.FromArgb(20, 206, 147, 216),
            ".sig" => ColorHelper.FromArgb(20, 239, 83, 80),
            ".json" or ".xml" => ColorHelper.FromArgb(20, 100, 181, 246),
            _ => ColorHelper.FromArgb(12, 144, 164, 174),
        };
    }

    Windows.UI.Color GetIconFgColor()
    {
        if (IsDir) return ColorHelper.FromArgb(255, 255, 213, 79);
        if (Name == "..") return ColorHelper.FromArgb(180, 128, 128, 128);
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        return ext switch
        {
            ".lua" => ColorHelper.FromArgb(255, 79, 195, 247),
            ".cfg" or ".ini" or ".toml" => ColorHelper.FromArgb(255, 255, 183, 77),
            ".txt" or ".md" => ColorHelper.FromArgb(255, 129, 199, 132),
            ".log" or ".vbl" => ColorHelper.FromArgb(255, 206, 147, 216),
            ".sig" => ColorHelper.FromArgb(255, 239, 83, 80),
            ".json" or ".xml" => ColorHelper.FromArgb(255, 100, 181, 246),
            _ => ColorHelper.FromArgb(200, 144, 164, 174),
        };
    }

    string GetExtType()
    {
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        return ext switch
        {
            ".lua" => "Lua Script",
            ".cfg" => "Config",
            ".txt" => "Text",
            ".sig" => "Signature",
            ".vbl" => "Boot Log",
            ".log" => "Log File",
            _ => string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.')} File"
        };
    }

    static string FormatSize(uint s) => s switch
    {
        < 1024 => $"{s} B",
        < 1024 * 1024 => $"{s / 1024.0:F1} KB",
        _ => $"{s / (1024.0 * 1024):F1} MB"
    };
}