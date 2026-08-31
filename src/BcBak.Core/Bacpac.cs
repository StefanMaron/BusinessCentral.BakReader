using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace BcBak;

/// <summary>
/// One column of a table as model.xml declares it, plus the SQL type facts the value
/// decoders need. A bacpac has no storage layout: model.xml is the whole schema source,
/// standing in for sysrscols/syscolpars, and the order columns appear in a table's
/// "Columns" relationship is the order their values appear in the data stream (validated
/// on every probe table and on 567 tables of a production export — PROVENANCE.md).
/// </summary>
public sealed record BacpacColumn(string Name, string ModelType, byte XType, bool Nullable,
    bool IsMax, int Length, byte Precision, byte Scale)
{
    public static BacpacColumn FromModel(string name, string modelType, bool nullable, bool isMax,
        int length, byte precision, byte scale)
        => new(name, modelType, XTypeOf(modelType), nullable, isMax, length, precision, scale);

    /// <summary>
    /// The system type id syscolpars would carry for this type, so the shared decoders and
    /// the CLI see one column shape whichever container the row came from. 0 = a model type
    /// with no SQL Server system type id known to this reader; reading such a column throws.
    /// </summary>
    static byte XTypeOf(string modelType) => modelType switch
    {
        "bit" => 104, "tinyint" => 48, "smallint" => 52, "int" => 56, "bigint" => 127,
        "real" => 59, "float" => 62, "decimal" => 106, "numeric" => 108,
        "money" => 60, "smallmoney" => 122,
        "datetime" => 61, "smalldatetime" => 58, "date" => 40, "time" => 41,
        "datetime2" => 42, "datetimeoffset" => 43,
        "uniqueidentifier" => 36, "rowversion" => 189, "timestamp" => 189,
        "char" => 175, "varchar" => 167, "nchar" => 239, "nvarchar" => 231,
        "text" => 35, "ntext" => 99, "xml" => 241,
        "binary" => 173, "varbinary" => 165, "image" => 34,
        _ => 0,
    };

    /// <summary>MaxLength in the syscolpars convention: bytes for string/binary types, −1 for MAX.</summary>
    public short MaxLength => XType switch
    {
        231 or 239 => IsMax ? (short)-1 : (short)(2 * Length),      // nvarchar / nchar
        167 or 175 or 165 or 173 => IsMax ? (short)-1 : (short)Length, // varchar / char / varbinary / binary
        34 or 35 or 99 or 241 => -1,                                 // image / text / ntext / xml
        106 or 108 => (short)(Precision <= 9 ? 5 : Precision <= 19 ? 9 : Precision <= 28 ? 13 : 17),
        104 or 48 => 1, 52 => 2, 56 or 59 or 58 or 122 => 4,
        127 or 62 or 61 or 60 or 189 => 8, 36 => 16, 40 => 3,
        41 => (short)TimeWidth(Scale),
        42 => (short)(TimeWidth(Scale) + 3),
        43 => (short)(TimeWidth(Scale) + 5),
        _ => 0,
    };

    internal static int TimeWidth(int scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    public SysColumn ToSysColumn(int colId) => new(colId, Name, XType, MaxLength, Precision, Scale);
}

/// <summary>A table as model.xml declares it: its columns in data-stream order and its primary key.</summary>
public sealed record BacpacTable(string Schema, string Name, IReadOnlyList<BacpacColumn> Columns)
{
    /// <summary>Primary-key columns in key order — the clustered key an $ext companion joins on.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();
    /// <summary>Data-stream entries in (batch, sequence) order; empty for a table with no rows.</summary>
    public IReadOnlyList<string> DataEntries { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A .bacpac: an Open Packaging (zip) container holding model.xml (the schema), Origin.xml
/// (package metadata and a checksum over model.xml), and one folder of native-BCP data
/// streams per table with rows, named Data/&lt;schema&gt;.&lt;url-encoded table&gt;/TableData-NNN-MMMMM.BCP.
///
/// Structure derived by inspection of exports produced by sqlpackage 170.5.76 from
/// SQL Server 2022, and validated by importing the same file back into a SQL Server and
/// comparing full tables (PROVENANCE.md "bacpac container").
/// </summary>
public sealed class BacpacFile : IDisposable
{
    /// <summary>The only Data stream format version whose row framing this reader has derived.</summary>
    public const string SupportedDataStreamVersion = "2.0.0.0";

    static readonly XNamespace Dac = "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";
    static readonly XName ElementName = Dac + "Element", RelationshipName = Dac + "Relationship",
        EntryName = Dac + "Entry", ReferencesName = Dac + "References", PropertyName = Dac + "Property";

    readonly ZipArchive _zip;
    readonly FileStream _file;
    Dictionary<string, BacpacTable>? _tables;

    public string Path { get; }
    public string DatabaseName { get; }
    public string ProductVersion { get; }

    public BacpacFile(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { _zip = new ZipArchive(_file, ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { _file.Dispose(); throw new InvalidDataException($"{path} is not a zip container — a .bacpac is an Open Packaging archive ({ex.Message})"); }
        var origin = LoadXml("Origin.xml");
        var dataVersion = origin.Descendants(Dac + "Version")
            .FirstOrDefault(v => (string?)v.Attribute("StreamName") == "Data")?.Value;
        if (dataVersion is null)
            throw new InvalidDataException($"{path}: Origin.xml declares no Data stream version — refusing to guess the row format");
        if (dataVersion != SupportedDataStreamVersion)
            throw new InvalidDataException($"{path}: Data stream version {dataVersion}, but only {SupportedDataStreamVersion} has a derived row format — refusing to guess");
        ModelChecksum = origin.Descendants(Dac + "Checksum")
            .FirstOrDefault(c => (string?)c.Attribute("Uri") == "/model.xml")?.Value;
        ProductVersion = origin.Descendants(Dac + "ProductVersion").FirstOrDefault()?.Value ?? "?";
        DatabaseName = LoadXml("DacMetadata.xml").Descendants(Dac + "Name").FirstOrDefault()?.Value
                       ?? System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>The SHA-256 Origin.xml declares over model.xml, or null when the package declares none.</summary>
    public string? ModelChecksum { get; }

    ZipArchiveEntry Entry(string name)
        => _zip.GetEntry(name) ?? throw new InvalidDataException($"{Path}: the package has no {name} — not a bacpac?");

    XDocument LoadXml(string name)
    {
        using var s = Entry(name).Open();
        return XDocument.Load(s);
    }

    public IReadOnlyDictionary<string, BacpacTable> Tables => _tables ??= LoadModel();

    /// <summary>
    /// Streams model.xml once (113 MB in a real export — never as a DOM), keeping only table
    /// and primary-key elements, and verifies Origin.xml's checksum over the same bytes as
    /// they pass. Called on first use, not at open, so the cost is paid by the first query.
    /// </summary>
    Dictionary<string, BacpacTable> LoadModel()
    {
        var columns = new Dictionary<string, List<BacpacColumn>>(StringComparer.Ordinal);
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        using (var raw = Entry("model.xml").Open())
        using (var sha = SHA256.Create())
        {
            using (var hashing = new CryptoStream(raw, sha, CryptoStreamMode.Read))
            {
                var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit };
                using (var r = XmlReader.Create(hashing, settings))
                {
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element || r.LocalName != "Element") continue;
                        string? type = r.GetAttribute("Type");
                        if (type != "SqlTable" && type != "SqlPrimaryKeyConstraint") continue;
                        var el = XElement.Load(r.ReadSubtree());
                        if (type == "SqlTable") ParseTable(el, columns, schemas);
                        else ParseKey(el, keys);
                    }
                }
                // A read-mode CryptoStream hashes only what it reads, and XmlReader may stop
                // before the last byte; draining it to the end both completes the hash and
                // proves the entry decompresses in full.
                hashing.CopyTo(Stream.Null);
            }
            string actual = Convert.ToHexString(sha.Hash!);
            if (ModelChecksum != null && !actual.Equals(ModelChecksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{Path}: model.xml checksum {actual} does not match the {ModelChecksum} Origin.xml declares — the package is damaged or was edited");
        }

        var entries = DataEntriesByTable();
        // Ordinal, and duplicates throw: a case-sensitive-collation database can hold two
        // tables that differ only in case, and silently keeping one of them would be a
        // table quietly missing from every listing.
        var tables = new Dictionary<string, BacpacTable>(StringComparer.Ordinal);
        foreach (var (key, cols) in columns)
        {
            string schema = schemas[key], name = key[(schema.Length + 1)..];
            string display = schema == "dbo" ? name : key;
            entries.Remove(key, out var files);
            keys.TryGetValue(key, out var pk);
            if (tables.ContainsKey(display))
                throw new InvalidDataException($"{Path}: model.xml declares '{display}' twice");
            tables[display] = new BacpacTable(schema, name, cols)
            {
                KeyColumns = pk ?? (IReadOnlyList<string>)Array.Empty<string>(),
                DataEntries = files ?? (IReadOnlyList<string>)Array.Empty<string>(),
            };
        }
        if (entries.Count > 0)
            throw new InvalidDataException($"{Path}: data stream for '{entries.Keys.First()}' has no table in model.xml — schema/data mismatch, refusing to guess");
        return tables;
    }

    /// <summary>
    /// The Relationship[@Name=<paramref name="relationship"/>]/Entry/<paramref name="leaf"/>
    /// children of an element — the one shape every lookup in model.xml has.
    ///
    /// This was a chain of LINQ operators per lookup, and the lookups are per column:
    /// a production export has ~196,000 of them, each allocating a dozen iterators and
    /// re-walking the same children. Written out as loops it allocates nothing per step.
    /// </summary>
    static IEnumerable<XElement> Under(XElement el, string relationship, XName leaf)
    {
        foreach (var rel in el.Elements(RelationshipName))
        {
            if ((string?)rel.Attribute("Name") != relationship) continue;
            foreach (var entry in rel.Elements(EntryName))
                foreach (var child in entry.Elements(leaf))
                    yield return child;
        }
    }

    static XElement? FirstUnder(XElement el, string relationship, XName leaf)
    {
        foreach (var child in Under(el, relationship, leaf)) return child;
        return null;
    }

    static void ParseTable(XElement el, Dictionary<string, List<BacpacColumn>> columns, Dictionary<string, string> schemas)
    {
        var (schema, table, _) = SplitName(el.Attribute("Name")!.Value);
        var cols = new List<BacpacColumn>();
        foreach (var colEl in Under(el, "Columns", ElementName))
        {
            // Computed columns (SqlComputedColumn) are not stored and carry no data.
            if ((string?)colEl.Attribute("Type") != "SqlSimpleColumn") continue;
            string qualified = colEl.Attribute("Name")!.Value;
            string colName = SplitName(qualified).Part3
                ?? throw new InvalidDataException($"model.xml: column identifier '{qualified}' does not name a column of a table");
            bool nullable = Prop(colEl, "IsNullable") != "False";
            var spec = FirstUnder(colEl, "TypeSpecifier", ElementName)
                ?? throw new InvalidDataException($"model.xml: column [{schema}].[{table}].[{colName}] has no type specifier");
            string typeName = FirstUnder(spec, "Type", ReferencesName)?.Attribute("Name")?.Value
                ?.Trim('[', ']')
                ?? throw new InvalidDataException($"model.xml: column [{schema}].[{table}].[{colName}] names no type");
            // One pass over the specifier's properties instead of one pass per property.
            bool isMax = false;
            int length = 0;
            byte precision = 0, scale = 0;
            foreach (var p in spec.Elements(PropertyName))
            {
                string? value = (string?)p.Attribute("Value");
                switch ((string?)p.Attribute("Name"))
                {
                    case "IsMax": isMax = value == "True"; break;
                    case "Length": length = int.Parse(value ?? "0"); break;
                    case "Precision": precision = byte.Parse(value ?? "0"); break;
                    case "Scale": scale = byte.Parse(value ?? "0"); break;
                }
            }
            cols.Add(BacpacColumn.FromModel(colName, typeName, nullable, isMax, length, precision, scale));
        }
        columns[schema + "." + table] = cols;
        schemas[schema + "." + table] = schema;
    }

    static void ParseKey(XElement el, Dictionary<string, List<string>> keys)
    {
        string? target = FirstUnder(el, "DefiningTable", ReferencesName)?.Attribute("Name")?.Value;
        if (target is null) return;
        var (schema, table, _) = SplitName(target);
        var cols = new List<string>();
        foreach (var spec in Under(el, "ColumnSpecifications", ElementName))
        {
            string? n = FirstUnder(spec, "Column", ReferencesName)?.Attribute("Name")?.Value;
            if (n != null) cols.Add(SplitName(n).Part3!);
        }
        if (cols.Count > 0) keys[schema + "." + table] = cols;
    }

    static string? Prop(XElement el, string name)
    {
        foreach (var p in el.Elements(PropertyName))
            if ((string?)p.Attribute("Name") == name) return (string?)p.Attribute("Value");
        return null;
    }

    /// <summary>Splits a model.xml identifier "[a].[b]" or "[a].[b].[c]" into its bracketed parts.</summary>
    static (string Part1, string Part2, string? Part3) SplitName(string bracketed)
    {
        var parts = new List<string>();
        for (int i = 0; i < bracketed.Length;)
        {
            if (bracketed[i] != '[') throw new InvalidDataException($"model.xml: malformed identifier '{bracketed}'");
            int end = bracketed.IndexOf(']', i + 1);
            while (end >= 0 && end + 1 < bracketed.Length && bracketed[end + 1] == ']') end = bracketed.IndexOf(']', end + 2);
            if (end < 0) throw new InvalidDataException($"model.xml: malformed identifier '{bracketed}'");
            parts.Add(bracketed[(i + 1)..end].Replace("]]", "]"));
            i = end + 1;
            if (i < bracketed.Length && bracketed[i] == '.') i++;
        }
        if (parts.Count is not (2 or 3)) throw new InvalidDataException($"model.xml: identifier '{bracketed}' has {parts.Count} parts");
        return (parts[0], parts[1], parts.Count == 3 ? parts[2] : null);
    }

    /// <summary>
    /// Data entries grouped by "&lt;schema&gt;.&lt;table&gt;" and ordered by (NNN, MMMMM). NNN is an
    /// export batch, MMMMM its continuation once a batch passes ~4 MiB; both boundaries fall
    /// between rows in every export measured, but concatenating in this order is correct
    /// either way.
    /// </summary>
    Dictionary<string, List<string>> DataEntriesByTable()
    {
        var byTable = new Dictionary<string, List<(int Batch, int Seq, string Name)>>(StringComparer.Ordinal);
        foreach (var e in _zip.Entries)
        {
            string n = e.FullName;
            if (!n.StartsWith("Data/", StringComparison.Ordinal)) continue;
            int slash = n.IndexOf('/', 5);
            if (slash < 0) throw new InvalidDataException($"{Path}: unexpected data entry '{n}'");
            string folder = n[5..slash], file = n[(slash + 1)..];
            if (!file.StartsWith("TableData-", StringComparison.Ordinal) || !file.EndsWith(".BCP", StringComparison.Ordinal))
                throw new InvalidDataException($"{Path}: unexpected data entry '{n}' — expected TableData-NNN-MMMMM.BCP");
            var nums = file["TableData-".Length..^".BCP".Length].Split('-');
            if (nums.Length != 2 || !int.TryParse(nums[0], out int batch) || !int.TryParse(nums[1], out int seq))
                throw new InvalidDataException($"{Path}: cannot read the batch/sequence numbers of data entry '{n}'");
            int dot = folder.IndexOf('.');
            if (dot < 0) throw new InvalidDataException($"{Path}: data folder '{folder}' is not <schema>.<table>");
            string key = Uri.UnescapeDataString(folder[..dot]) + "." + Uri.UnescapeDataString(folder[(dot + 1)..]);
            if (!byTable.TryGetValue(key, out var list)) byTable[key] = list = new();
            list.Add((batch, seq, n));
        }
        return byTable.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderBy(x => x.Batch).ThenBy(x => x.Seq).Select(x => x.Name).ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The table's rows, decoded to storage-form cells in the order of <paramref name="want"/>.
    /// Columns not wanted are stepped over without materialising their bytes, so a --select of
    /// one column does not pay for a table's blobs.
    /// </summary>
    public IEnumerable<Cell[]> ReadRows(BacpacTable t, IReadOnlyList<BacpacColumn> want)
    {
        var reader = new BcpRowReader(t.Name, t.Columns);
        foreach (var entry in t.DataEntries)
        {
            using var s = Entry(entry).Open();
            foreach (var row in reader.Read(s, want)) yield return row;
        }
    }

    public long CountRows(BacpacTable t)
    {
        long n = 0;
        foreach (var _ in ReadRows(t, Array.Empty<BacpacColumn>())) n++;
        return n;
    }

    public void Dispose() { _zip.Dispose(); _file.Dispose(); }
}
