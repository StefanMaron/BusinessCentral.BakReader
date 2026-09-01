using System.Buffers.Binary;

namespace BusinessCentral.DbReader;

/// <summary>
/// Structural parser for the Microsoft Tape Format (MTF) container that a SQL Server
/// native backup (.bak) is written in.
///
/// Layout (observed on the BC demo backups; block/stream framing per the historically
/// published Microsoft Tape Format Specification 1.00a — see PROVENANCE.md "Backup
/// container"):
///
///   TAPE  descriptor block — media header
///   SFMB  soft filemark block (no stream chain)
///   SSET  start-of-set — one backup set
///   VOLB  volume descriptor
///   MSCI  SQL Server: database/configuration info (MQCI stream)
///   MSDA  SQL Server: one per data-copy section; carries an MQDA stream of raw
///         8192-byte blocks (see <see cref="PageFile"/> for what the blocks mean)
///   MSTL  SQL Server: transaction-log section; MQTL stream of log blocks
///   MSLS  SQL Server: trailing section (not needed for reading data)
///
/// Every descriptor block has a 4-byte type tag, a u32 attribute word at +4 and a u16
/// "offset to first event" at +8 that points at its stream chain. Streams have a 22-byte
/// header: id[4], u16 fs-attributes, u16 media-attributes, u64 length, u16 encryption
/// algorithm, u16 compression algorithm, u16 checksum. Stream data is 4-byte aligned;
/// an SPAD stream pads to the next block boundary and ends the chain.
/// All facts validated by walking both BC demo backups end to end; see PROVENANCE.md.
/// </summary>
internal sealed class MtfFile
{
    static readonly HashSet<string> KnownDblks = new()
        { "TAPE", "SSET", "VOLB", "ESET", "EOTM", "SFMB", "MSCI", "MSDA", "MSTL", "MSLS" };

    /// <summary>A data-bearing stream extracted from the container (past its 2-byte lead, see remarks).</summary>
    public sealed record DataRegion(string Dblk, string StreamId, long DataOffset, long BlockCount);

    /// <summary>MQDA regions in file order: raw 8192-byte block sections of the data copy.</summary>
    public List<DataRegion> MqdaRegions { get; } = new();

    /// <summary>Total bytes of MQTL (transaction log) stream data. The log is NOT replayed by this reader; see README limitations.</summary>
    public long LogStreamBytes { get; private set; }

    public MtfFile(Microsoft.Win32.SafeHandles.SafeFileHandle fh, long size)
    {
        static void ReadAt(Microsoft.Win32.SafeHandles.SafeFileHandle h, long off, Span<byte> buf)
        {
            int n = 0;
            while (n < buf.Length)
            {
                int r = RandomAccess.Read(h, buf[n..], off + n);
                if (r == 0) throw new EndOfStreamException($"unexpected end of file at offset 0x{off + n:x}");
                n += r;
            }
        }
        Span<byte> hdr = stackalloc byte[60];
        Span<byte> sh = stackalloc byte[22];
        Span<byte> lead = stackalloc byte[2];
        long pos = 0;
        while (pos >= 0 && pos + 60 <= size)
        {
            ReadAt(fh, pos, hdr);
            string tag = System.Text.Encoding.ASCII.GetString(hdr[..4]);
            if (!KnownDblks.Contains(tag))
                throw new InvalidDataException($"unknown MTF descriptor block '{tag}' at offset 0x{pos:x} — not a SQL Server native backup, or a layout this reader has not seen");
            int firstEvent = BinaryPrimitives.ReadUInt16LittleEndian(hdr[8..]);
            if (tag is "ESET" or "EOTM") break;               // end of backup set
            if (tag == "SFMB") { pos += firstEvent; continue; } // soft filemark: no stream chain
            long spos = pos + firstEvent;
            long next = -1;
            while (spos + 22 <= size)
            {
                ReadAt(fh, spos, sh);
                string sid = System.Text.Encoding.ASCII.GetString(sh[..4]);
                if (sid.Any(c => c < ' ' || c > '~'))
                    throw new InvalidDataException($"invalid stream id at offset 0x{spos:x} inside {tag} block");
                long slen = (long)BinaryPrimitives.ReadUInt64LittleEndian(sh[8..]);
                ushort enc = BinaryPrimitives.ReadUInt16LittleEndian(sh[16..]);
                ushort comp = BinaryPrimitives.ReadUInt16LittleEndian(sh[18..]);
                if (enc != 0)
                    throw new NotSupportedException($"stream {sid}: encryption algorithm {enc} — encrypted backups are not supported");
                if (comp != 0)
                    throw new NotSupportedException($"stream {sid}: compression algorithm {comp} — backups taken WITH COMPRESSION are not supported; take the backup without compression");
                if (sid is "MQDA" or "MQTL" && slen > 2)
                {
                    // Stream data starts with 2 bytes (observed always 0x0000, meaning unknown),
                    // then whole 8192-byte blocks. Validated: (len-2) is an exact multiple of 8192
                    // on every MQDA/MQTL stream of both demo backups.
                    ReadAt(fh, spos + 22, lead);
                    if (lead[0] != 0 || lead[1] != 0)
                        throw new InvalidDataException($"stream {sid} at 0x{spos:x}: lead bytes {lead[0]:x2}{lead[1]:x2} != 0000 — layout differs from every observed backup, refusing to guess");
                    if ((slen - 2) % PageFile.PageSize != 0)
                        throw new InvalidDataException($"stream {sid}: data length {slen}-2 is not a multiple of 8192");
                    if (sid == "MQDA")
                        MqdaRegions.Add(new DataRegion(tag, sid, spos + 22 + 2, (slen - 2) / PageFile.PageSize));
                    else
                        LogStreamBytes += slen - 2;
                }
                long dataEnd = spos + 22 + slen;
                if (sid == "SPAD") { next = dataEnd; break; }   // SPAD pads to the block boundary and ends the chain
                spos = (dataEnd + 3) & ~3L;                     // 4-byte stream alignment
                if (spos >= size) break;
            }
            if (next < 0) break;
            pos = next;
        }
        if (MqdaRegions.Count == 0)
            throw new InvalidDataException("no MQDA data stream found — not a SQL Server full database backup");
    }
}
