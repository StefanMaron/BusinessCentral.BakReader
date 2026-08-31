using System.Text;

namespace BcBak;

/// <summary>
/// bcbak — purpose-built reader for the Business Central CRONUS demo database backup
/// (BusinessCentral-W1.bak inside BC artifacts). Independent implementation; see README.md
/// and PROVENANCE.md.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) return Usage();
            var cmd = args[0];
            var bakPath = args[1];
            if (!File.Exists(bakPath)) { Console.Error.WriteLine($"error: file not found: {bakPath}"); return 2; }
            var opts = ParseOpts(args.Skip(2));
            using var pf = new PageFile(bakPath);
            var cat = new Catalog(pf);
            return cmd switch
            {
                "tables" => Tables(pf, cat, opts),
                "read" => Read(pf, cat, opts),
                "verify" => Verify(pf, cat, opts),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            usage:
              bcbak tables <file.bak>                              list readable BC tables
              bcbak read   <file.bak> --table <name> [--company <c>] [--top N] [--select "A,B"]
              bcbak verify <file.bak> --fixture <fixture.tsv> --table <name> --select "A,B"
            Table name may be the AL table name (e.g. "No. Series") or the raw SQL object name.
            """);
        return 64;
    }

    static Dictionary<string, string> ParseOpts(IEnumerable<string> args)
    {
        var d = new Dictionary<string, string>();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--")) { key = a[2..]; d[key] = "true"; }
            else if (key != null) { d[key] = a; key = null; }
        }
        return d;
    }

    // BC replaces characters invalid in SQL identifiers with '_' when building object names.
    static string BcNormalize(string alName)
    {
        var sb = new StringBuilder();
        foreach (var ch in alName) sb.Append(ch is '.' or '"' or '\\' or '/' or '\'' or '%' or '[' or ']' ? '_' : ch);
        return sb.ToString();
    }

    sealed record BcTable(SysObject Obj, string? Company, string TableName, string? AppId, RowSet RowSet);

    static List<BcTable> BcTables(Catalog cat)
    {
        var list = new List<BcTable>();
        foreach (var o in cat.Objects.Values)
        {
            if (o.Type != "U") continue;
            RowSet? rs;
            try { rs = cat.RowsetFor(o.ObjectId, 1, 0); } catch { continue; }
            var segs = o.Name.Split('$');
            string? company = null, appId = null; string tableName;
            bool isExt = segs[^1] == "ext";
            var core = isExt ? segs[..^1] : segs;
            if (core.Length >= 3 && Guid.TryParse(core[^1], out _)) { company = string.Join("$", core[..^2]); tableName = core[^2]; appId = core[^1]; }
            else if (core.Length == 2 && Guid.TryParse(core[^1], out _)) { tableName = core[0]; appId = core[1]; }
            else if (core.Length == 2) { company = core[0]; tableName = core[1]; }
            else tableName = core[0];
            if (isExt) tableName += "$ext";
            list.Add(new BcTable(o, company, tableName, appId, rs));
        }
        return list;
    }

    static BcTable ResolveTable(Catalog cat, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("table", out var want)) throw new ArgumentException("--table is required");
        var norm = BcNormalize(want);
        var all = BcTables(cat);
        var matches = all.Where(t => t.Obj.Name.Equals(want, StringComparison.OrdinalIgnoreCase)
                                  || t.TableName.Equals(norm, StringComparison.OrdinalIgnoreCase)).ToList();
        if (opts.TryGetValue("company", out var comp))
            matches = matches.Where(t => t.Company != null && t.Company.StartsWith(comp, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new ArgumentException($"no table matches '{want}'");
        if (matches.Select(m => m.Company).Distinct().Count() > 1)
        {
            var withRows = matches.Where(m => m.RowSet.Rows > 0).ToList();
            if (withRows.Select(m => m.Company).Distinct().Count() == 1) matches = withRows;
            else throw new ArgumentException(
                $"table '{want}' exists in multiple companies ({string.Join(", ", matches.Select(m => m.Company).Distinct())}) — use --company");
        }
        if (matches.Count > 1) throw new ArgumentException($"ambiguous table '{want}': {string.Join(" | ", matches.Select(m => m.Obj.Name))}");
        return matches[0];
    }

    static int Tables(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        foreach (var t in BcTables(cat).OrderBy(t => t.Company).ThenBy(t => t.TableName))
            Console.WriteLine($"{t.RowSet.Rows,8}  {(t.RowSet.CompressionLevel switch { 0 => "none", 1 => "row ", 2 => "page", var x => x.ToString() })}  {t.Company ?? "-"}\t{t.TableName}");
        Console.Error.WriteLine($"[{cat.Objects.Count} objects, {pf.PageCount} pages, {pf.DuplicateImageCount} superseded page images]");
        return 0;
    }

    static IEnumerable<(List<SysColumn> cols, List<object?[]> rows)> ReadCore(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        var t = ResolveTable(cat, opts);
        cat.LoadColumnMetadata();
        var tr = new TableReader(pf, cat);
        var cols = cat.Columns[t.Obj.ObjectId];
        List<SysColumn> selected;
        if (opts.TryGetValue("select", out var sel))
        {
            selected = new();
            foreach (var name in sel.Split(','))
            {
                var n = BcNormalize(name.Trim());
                selected.Add(cols.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"column '{name.Trim()}' not found; available: {string.Join(", ", cols.Select(c => c.Name))}"));
            }
        }
        else selected = cols;
        int top = opts.TryGetValue("top", out var ts) ? int.Parse(ts) : int.MaxValue;
        bool compressed = t.RowSet.CompressionLevel > 0;
        var outRows = new List<object?[]>();
        foreach (var row in new TableReader(pf, cat).ReadRows(t.Obj.ObjectId))
        {
            if (outRows.Count >= top) break;
            outRows.Add(selected.Select(c => SqlTypes.Decode(row[c.Name], c, compressed)).ToArray());
        }
        yield return (selected, outRows);
    }

    static int Read(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        foreach (var (selected, rows) in ReadCore(pf, cat, opts))
        {
            bool json = opts.TryGetValue("format", out var fm) && fm == "json";
            if (json)
            {
                Console.WriteLine("[");
                for (int r = 0; r < rows.Count; r++)
                {
                    var parts = selected.Select((c, i) => $"{J(c.Name)}: {JVal(rows[r][i])}");
                    Console.WriteLine("  {" + string.Join(", ", parts) + "}" + (r < rows.Count - 1 ? "," : ""));
                }
                Console.WriteLine("]");
            }
            else
            {
                Console.WriteLine(string.Join("|", selected.Select(c => c.Name)));
                foreach (var r in rows)
                    Console.WriteLine(string.Join("|", r.Select(v => v switch { null => "NULL", bool b => b ? "1" : "0", decimal d => d.ToString(), _ => v.ToString() })));
            }
        }
        return 0;
    }

    static string J(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static string JVal(object? v) => v switch
    {
        null => "null", bool b => b ? "true" : "false",
        long or byte or int or decimal => v.ToString()!,
        _ => J(v.ToString()!)
    };

    /// <summary>Compare decoded rows against a fixture exported from a restored SQL Server (the oracle). Order-insensitive.</summary>
    static int Verify(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("fixture", out var fixPath)) throw new ArgumentException("--fixture is required");
        var expected = File.ReadAllLines(fixPath).Where(l => l.Length > 0).OrderBy(x => x).ToList();
        foreach (var (selected, rows) in ReadCore(pf, cat, opts))
        {
            var actual = rows.Select(r => string.Join("|", r.Select(v => v switch { null => "NULL", bool b => b ? "1" : "0", _ => v.ToString() })))
                             .OrderBy(x => x).ToList();
            if (expected.SequenceEqual(actual))
            {
                Console.WriteLine($"OK: {actual.Count} rows match fixture {Path.GetFileName(fixPath)}");
                return 0;
            }
            Console.Error.WriteLine($"FAIL: {actual.Count} decoded rows vs {expected.Count} fixture rows");
            foreach (var miss in expected.Except(actual).Take(5)) Console.Error.WriteLine($"  missing: {miss}");
            foreach (var extra in actual.Except(expected).Take(5)) Console.Error.WriteLine($"  extra:   {extra}");
            return 1;
        }
        return 1;
    }
}
