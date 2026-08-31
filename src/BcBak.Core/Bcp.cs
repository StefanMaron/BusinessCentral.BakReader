using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Reads rows out of a bacpac's native-BCP data stream.
///
/// The stream is a bare sequence of rows — no header, no trailer, no row delimiter. Each
/// row is its columns in model.xml order, each column a length prefix (of a width the type
/// and nullability decide) followed by that many value bytes; a prefix of all one-bits is
/// SQL NULL. The prefix widths were derived from probe tables whose every value is chosen
/// (tools/typeprobe.sql, probe and probe_notnull) and re-checked against 567 tables of a
/// production export; see PROVENANCE.md "bacpac: native BCP row framing".
///
///   0 bytes  the fixed-length numeric and temporal types, and char(n), when NOT NULL
///   1 byte   those same types when nullable; bit, uniqueidentifier and decimal/numeric always
///   2 bytes  char(n) when nullable, and always nchar/nvarchar/varchar/binary/varbinary/rowversion
///   4 bytes  text / ntext / image
///   8 bytes  any (max) type
///
/// Values are emitted as <see cref="Cell"/>s in the same storage form the .bak reader
/// produces, so <see cref="SqlTypes.Decode"/> is the single value decoder for both
/// containers. Two encodings genuinely differ and are converted here — datetime, whose
/// halves are the other way round, and time/datetime2, always written at full precision —
/// and one differs in a way the decoder is told about: DacFx writes char/varchar/text as
/// UTF-16 rather than in the column's collation code page (textIsUtf16).
/// </summary>
public sealed class BcpRowReader
{
    readonly string _table;
    readonly IReadOnlyList<BacpacColumn> _cols;

    public BcpRowReader(string table, IReadOnlyList<BacpacColumn> cols) { _table = table; _cols = cols; }

    /// <summary>Width in bytes of a column's value, for the types whose prefix must state exactly that width.</summary>
    public static int FixedWidth(BacpacColumn c) => c.XType switch
    {
        104 or 48 => 1, 52 => 2, 56 or 59 => 4, 127 or 62 => 8,
        61 => 8, 40 => 3, 41 => 5, 42 => 8,        // datetime; date; time; datetime2 (always full precision)
        36 => 16, 189 => 8, 106 or 108 => 19,      // uniqueidentifier; rowversion; decimal = prec+scale+sign+16
        175 => 2 * c.Length,                       // char(n), written as UTF-16
        _ => -1,
    };

    public static int PrefixLength(BacpacColumn c)
    {
        if (c.IsMax) return 8;
        return c.XType switch
        {
            34 or 35 or 99 => 4,                                   // image / text / ntext
            231 or 239 or 167 or 165 or 173 or 189 => 2,           // nvarchar/nchar/varchar/varbinary/binary/rowversion
            104 or 36 or 106 or 108 => 1,                          // bit / uniqueidentifier / decimal / numeric
            175 => c.Nullable ? 2 : 0,                             // char(n)
            48 or 52 or 56 or 127 or 59 or 62 or 61 or 40 or 41 or 42 => c.Nullable ? 1 : 0,
            _ => -1,
        };
    }

    /// <summary>
    /// Rows from one data stream, each an array of cells in <paramref name="want"/> order.
    /// The stream must end exactly on a row boundary.
    /// </summary>
    public IEnumerable<Cell[]> Read(Stream stream, IReadOnlyList<BacpacColumn> want)
    {
        var wanted = new int[_cols.Count];
        for (int i = 0; i < wanted.Length; i++) wanted[i] = -1;
        for (int w = 0; w < want.Count; w++)
        {
            int i = IndexOfColumn(want[w].Name);
            if (wanted[i] >= 0)
                throw new ArgumentException($"bacpac table {_table}: column {want[w].Name} was asked for twice");
            wanted[i] = w;
        }
        var cur = new Cursor(stream, _table);
        while (!cur.AtEnd)
        {
            var row = new Cell[want.Count];
            for (int i = 0; i < _cols.Count; i++)
            {
                var c = _cols[i];
                int pl = PrefixLength(c);
                if (pl < 0)
                    throw new NotSupportedException(
                        $"bacpac table {_table}, column {c.Name}: type {c.ModelType} has no derived native-BCP framing — refusing to guess");
                long len;
                if (pl == 0) len = FixedWidth(c);
                else
                {
                    len = cur.ReadPrefix(pl, c.Name);
                    if (len == AllOnes(pl))
                    {
                        if (wanted[i] >= 0) row[wanted[i]] = Cell.Null;
                        continue;
                    }
                    if (pl == 8 && len == unchecked((long)0xFFFFFFFFFFFFFFFEUL))
                        throw new NotSupportedException(
                            $"bacpac table {_table}, column {c.Name}: chunked (unknown-length) MAX value — that form is not derived, refusing to guess");
                    if (len < 0 || len > int.MaxValue)
                        throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: length prefix {(ulong)len} is larger than this reader can address");
                    int fw = FixedWidth(c);
                    if (fw >= 0 && !c.IsMax && len != fw)
                        throw new InvalidDataException(
                            $"bacpac table {_table}, column {c.Name}: {c.ModelType} length prefix {len}, expected {fw}");
                }
                if (wanted[i] < 0) { cur.Skip(len, c.Name); continue; }
                row[wanted[i]] = ToCell(c, cur.Read((int)len, c.Name));
            }
            yield return row;
        }
    }

    int IndexOfColumn(string name)
    {
        for (int i = 0; i < _cols.Count; i++)
            if (string.Equals(_cols[i].Name, name, StringComparison.Ordinal)) return i;
        throw new ArgumentException($"bacpac table {_table} has no column {name}");
    }

    static long AllOnes(int prefixBytes) => prefixBytes == 8 ? -1L : (1L << (8 * prefixBytes)) - 1;

    /// <summary>Turns a BCP value into the storage-form bytes <see cref="SqlTypes.Decode"/> expects.</summary>
    Cell ToCell(BacpacColumn c, byte[] raw) => c.XType switch
    {
        106 or 108 => Cell.Of(DecimalToStorage(c, raw)),
        61 => Cell.Of(SwapDatetimeHalves(c, raw)),
        41 => Cell.Of(TimeToStorage(c, raw, 0)),
        42 => Cell.Of(TimeToStorage(c, raw, 3)),
        _ => Cell.Of(raw),
    };

    /// <summary>
    /// BCP decimal is [precision][scale][sign: 1 = positive][16-byte magnitude, little-endian];
    /// storage is [sign][magnitude] narrowed to the 4/8/12/16 bytes the precision needs. The
    /// magnitude bytes dropped must be zero, and the precision and scale the value carries
    /// must be the ones model.xml declares — either mismatch means the pairing of this stream
    /// with this schema is wrong.
    /// </summary>
    byte[] DecimalToStorage(BacpacColumn c, byte[] raw)
    {
        if (raw.Length != 19) throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: decimal value of {raw.Length} bytes, expected 19");
        if (raw[0] != c.Precision || raw[1] != c.Scale)
            throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: value carries decimal({raw[0]},{raw[1]}) but model.xml declares decimal({c.Precision},{c.Scale})");
        int keep = c.MaxLength - 1;
        for (int i = 3 + keep; i < 19; i++)
            if (raw[i] != 0)
                throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: magnitude does not fit the {c.MaxLength - 1} bytes precision {c.Precision} allows");
        var storage = new byte[c.MaxLength];
        storage[0] = raw[2];
        Array.Copy(raw, 3, storage, 1, keep);
        return storage;
    }

    /// <summary>BCP datetime is [i32 days since 1900][u32 ticks of 1/300 s]; storage has the two the other way round.</summary>
    byte[] SwapDatetimeHalves(BacpacColumn c, byte[] raw)
    {
        if (raw.Length != 8) throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: datetime value of {raw.Length} bytes, expected 8");
        var storage = new byte[8];
        Array.Copy(raw, 4, storage, 0, 4);
        Array.Copy(raw, 0, storage, 4, 4);
        return storage;
    }

    /// <summary>
    /// BCP writes time (and the time half of datetime2) as five bytes of 100-nanosecond units
    /// whatever the declared scale; storage uses units of 10^−scale seconds in 3, 4 or 5 bytes.
    /// A value that does not divide exactly is one the declared scale cannot hold.
    /// </summary>
    byte[] TimeToStorage(BacpacColumn c, byte[] raw, int trailingDateBytes)
    {
        if (raw.Length != 5 + trailingDateBytes)
            throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: {c.ModelType} value of {raw.Length} bytes, expected {5 + trailingDateBytes}");
        long units = 0;
        for (int i = 4; i >= 0; i--) units = (units << 8) | raw[i];
        long divisor = 1;
        for (int i = c.Scale; i < 7; i++) divisor *= 10;
        if (units % divisor != 0)
            throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: time value {units} (100 ns units) does not fit the declared scale {c.Scale}");
        units /= divisor;
        int width = BacpacColumn.TimeWidth(c.Scale);
        var storage = new byte[width + trailingDateBytes];
        for (int i = 0; i < width; i++) storage[i] = (byte)(units >> (8 * i));
        if ((units >> (8 * width)) != 0)
            throw new InvalidDataException($"bacpac table {_table}, column {c.Name}: time value does not fit {width} bytes at scale {c.Scale}");
        Array.Copy(raw, 5, storage, width, trailingDateBytes);
        return storage;
    }

    /// <summary>A forward-only byte cursor that can tell a clean end of stream from a truncated row.</summary>
    sealed class Cursor
    {
        readonly Stream _s;
        readonly string _table;
        readonly byte[] _buf = new byte[64 * 1024];
        int _pos, _len;
        bool _eof;

        public Cursor(Stream s, string table) { _s = s; _table = table; }

        bool Fill()
        {
            if (_pos < _len) return true;
            if (_eof) return false;
            _pos = 0;
            _len = _s.Read(_buf, 0, _buf.Length);
            if (_len == 0) { _eof = true; return false; }
            return true;
        }

        public bool AtEnd => !Fill();

        public long ReadPrefix(int width, string column)
        {
            Span<byte> p = stackalloc byte[8];
            Take(p[..width], column);
            return width switch
            {
                1 => p[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(p),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(p),
                _ => BinaryPrimitives.ReadInt64LittleEndian(p),
            };
        }

        public byte[] Read(int n, string column)
        {
            var b = new byte[n];
            Take(b, column);
            return b;
        }

        public void Skip(long n, string column)
        {
            while (n > 0)
            {
                if (!Fill()) throw Truncated(column);
                int take = (int)Math.Min(n, _len - _pos);
                _pos += take;
                n -= take;
            }
        }

        void Take(Span<byte> dst, string column)
        {
            int got = 0;
            while (got < dst.Length)
            {
                if (!Fill()) throw Truncated(column);
                int take = Math.Min(dst.Length - got, _len - _pos);
                _buf.AsSpan(_pos, take).CopyTo(dst[got..]);
                _pos += take; got += take;
            }
        }

        InvalidDataException Truncated(string column)
            => new($"bacpac table {_table}: the data stream ends inside column {column} — a data file must hold whole rows");
    }
}
