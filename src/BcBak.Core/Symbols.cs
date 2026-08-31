using System.IO.Compression;
using System.Text.Json;

namespace BcBak;

public sealed record AlField(int Id, string Name, string TypeName, string FieldClass);
public sealed record AlTable(int Id, string Name, string AppId, string AppName, List<AlField> Fields);

/// <summary>
/// Loads AL table/field metadata from `SymbolReference.json` files as shipped inside
/// Business Central `.app` packages. This is the schema *input*: pointing the reader at
/// the apps a database was built from (Base Application, Business Foundation, customer
/// extensions, …) lets output use AL table/field names and AL types instead of SQL ones.
///
/// Accepted inputs per path: a `.json` file, a NAVX `.app` package (zip content after
/// the 40-byte NAVX header), or the runtime wrapper `.app` Microsoft ships in BC
/// artifacts (a zip whose payload contains the inner NAVX `.app`).
/// Tables and table extensions may be nested in namespaces (AL namespaces, BC 24+);
/// the loader walks them recursively. Structure observed on the shipped Base
/// Application packages of BC 27.5 / 28.1.
/// </summary>
public sealed class SymbolStore
{
    public List<AlTable> Tables { get; } = new();

    public static SymbolStore Load(IEnumerable<string> paths)
    {
        var store = new SymbolStore();
        foreach (var p in paths) store.LoadOne(p);
        return store;
    }

    void LoadOne(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"symbols file not found: {path}");
        byte[] json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(path)
            : ExtractSymbolReference(File.ReadAllBytes(path), path);
        using var doc = JsonDocument.Parse(StripBom(json));
        var root = doc.RootElement;
        string appId = root.TryGetProperty("AppId", out var a) ? a.GetString() ?? "" : "";
        string appName = root.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
        if (appId.Length == 0)
            throw new InvalidDataException($"{path}: SymbolReference.json has no AppId");
        WalkNamespace(root, appId, appName);
    }

    static byte[] StripBom(byte[] b)
        => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b[3..] : b;

    /// <summary>Unwrap .app packaging until SymbolReference.json is found.</summary>
    static byte[] ExtractSymbolReference(byte[] bytes, string origin, int depth = 0)
    {
        if (depth > 3) throw new InvalidDataException($"{origin}: nested deeper than any observed .app packaging");
        // NAVX header: magic "NAVX", u32 header length at +4; the zip follows.
        if (bytes.Length > 8 && bytes[0] == 'N' && bytes[1] == 'A' && bytes[2] == 'V' && bytes[3] == 'X')
        {
            int hdrLen = BitConverter.ToInt32(bytes, 4);
            if (hdrLen <= 0 || hdrLen >= bytes.Length) throw new InvalidDataException($"{origin}: NAVX header length {hdrLen} out of range");
            bytes = bytes[hdrLen..];
        }
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
            throw new InvalidDataException($"{origin}: not a NAVX/zip .app package and not a .json file");
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sr = zip.Entries.FirstOrDefault(e => e.Name.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        if (sr != null)
        {
            using var ms = new MemoryStream();
            sr.Open().CopyTo(ms);
            return ms.ToArray();
        }
        // runtime wrapper: exactly one inner .app entry
        var inner = zip.Entries.Where(e => e.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)).ToList();
        if (inner.Count == 1)
        {
            using var ms = new MemoryStream();
            inner[0].Open().CopyTo(ms);
            return ExtractSymbolReference(ms.ToArray(), origin + " -> " + inner[0].Name, depth + 1);
        }
        throw new InvalidDataException($"{origin}: no SymbolReference.json and no single inner .app in the package");
    }

    void WalkNamespace(JsonElement ns, string appId, string appName)
    {
        if (ns.TryGetProperty("Tables", out var tables) && tables.ValueKind == JsonValueKind.Array)
            foreach (var t in tables.EnumerateArray())
                Tables.Add(ParseTable(t, appId, appName));
        if (ns.TryGetProperty("Namespaces", out var subs) && subs.ValueKind == JsonValueKind.Array)
            foreach (var sub in subs.EnumerateArray())
                WalkNamespace(sub, appId, appName);
    }

    static AlTable ParseTable(JsonElement t, string appId, string appName)
    {
        int id = t.TryGetProperty("Id", out var i) ? i.GetInt32() : 0;
        string name = t.GetProperty("Name").GetString()!;
        var fields = new List<AlField>();
        if (t.TryGetProperty("Fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var f in fs.EnumerateArray())
            {
                string tn = "?";
                if (f.TryGetProperty("TypeDefinition", out var td))
                {
                    tn = td.TryGetProperty("Name", out var tnn) ? tnn.GetString() ?? "?" : "?";
                    if (tn == "Enum" && td.TryGetProperty("Subtype", out var st) && st.TryGetProperty("Name", out var stn))
                        tn = $"Enum \"{stn.GetString()}\"";
                }
                string fieldClass = "Normal";
                if (f.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Array)
                    foreach (var pr in props.EnumerateArray())
                        if (pr.TryGetProperty("Name", out var pn) && pn.GetString() == "FieldClass")
                            fieldClass = pr.GetProperty("Value").GetString() ?? "Normal";
                fields.Add(new AlField(
                    f.TryGetProperty("Id", out var fi) ? fi.GetInt32() : 0,
                    f.GetProperty("Name").GetString()!, tn, fieldClass));
            }
        return new AlTable(id, name, appId, appName, fields);
    }

    /// <summary>All symbol tables whose AL name matches (several apps can define same-named tables).</summary>
    public List<AlTable> Find(string alName)
        => Tables.Where(t => t.Name.Equals(alName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The AL table whose name and app id match a SQL object's parsed name parts.</summary>
    public AlTable? FindForSqlTable(string normalizedTableName, string? appId)
        => Tables.FirstOrDefault(t =>
            NameCompat(t.Name, normalizedTableName)
            && (appId is null || t.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)));

    static bool NameCompat(string alName, string sqlName)
        => SqlNames.Normalize(alName).Equals(sqlName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// BC's AL-name → SQL-identifier normalization: characters invalid in SQL identifiers
/// are replaced with '_' (observed across all demo-database object/column names).
/// </summary>
public static class SqlNames
{
    public static string Normalize(string alName)
    {
        var sb = new System.Text.StringBuilder(alName.Length);
        foreach (var ch in alName) sb.Append(ch is '.' or '"' or '\\' or '/' or '\'' or '%' or '[' or ']' ? '_' : ch);
        return sb.ToString();
    }
}
