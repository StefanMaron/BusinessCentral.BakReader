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
                "companies" => Companies(cat),
                "describe" => Describe(pf, cat, opts),
                "check" => Check(pf),
                "validate" => Validate(pf, opts),
                "read" => Read(pf, cat, opts),
                "verify" => Verify(pf, cat, opts),
                "serve" => Serve(pf, cat, opts, Console.In, Console.Out),
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
              bcbak tables <file.bak> [--symbols <apps>]           list readable BC tables
              bcbak companies <file.bak>                           list the companies in the database
              bcbak describe <file.bak> --table <name> --symbols <apps>   AL schema of a table (field ids, AL types, SQL columns)
              bcbak check  <file.bak>                              cross-check the structural page map against page self-identification
              bcbak validate <file.bak> --against <restored.mdf>   byte-compare every mapped page against a restored copy
              bcbak read   <file.bak> --table <name> [--company <c>] [--top N] [--select "A,B"]
              bcbak serve  <file.bak> [--symbols <apps>]     open once, answer requests over stdin/stdout
                                                             (one JSON request per line: {"id": .., "cmd": "read"|"tables"|"companies"|"describe"|"quit",
                                                              "table": .., "company": .., "top": .., "select": .., "sha256": ..}; one JSON response line each)
              bcbak verify <file.bak> --fixture <fixture.tsv> --table <name> --select "A,B"
            Table name may be the AL table name (e.g. "No. Series") or the raw SQL object name.
            --symbols takes a comma-separated list of .app packages or SymbolReference.json
            files (e.g. the shipped Base Application .app); with it, output uses AL field
            names and AL types. The schema is an input: point it at the apps the database
            was actually built from.
            """);
        return 64;
    }

    static SymbolStore? LoadSymbols(Dictionary<string, string> opts)
        => opts.TryGetValue("symbols", out var s)
            ? SymbolStore.Load(s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            : null;

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

    /// <summary>
    /// Cross-check the structural page map against the empirical "last self-identified
    /// image wins" method. Disagreements are pages where a stale page image elsewhere in
    /// the file carries the same page id; the structural map is the one that matches
    /// RESTORE (validated on both BC demo backups, see PROVENANCE.md).
    /// </summary>
    static int Check(PageFile pf)
    {
        var (agree, stale, unident, disagreements) = pf.CrossCheck();
        Console.WriteLine($"data regions:        {pf.Mtf.MqdaRegions.Count}");
        Console.WriteLine($"GAM intervals:       {pf.GamIntervalCount}");
        Console.WriteLine($"mapped pages:        {pf.PageCount}");
        Console.WriteLine($"superseded by re-read: {pf.SupersededPageCount}");
        Console.WriteLine($"log stream bytes (not replayed): {pf.Mtf.LogStreamBytes}");
        Console.WriteLine($"self-id agreeing blocks:  {agree}");
        Console.WriteLine($"stale self-id headers:    {stale}");
        Console.WriteLine($"unidentifiable blocks:    {unident}");
        Console.WriteLine($"pages where last-image-wins would differ: {disagreements.Count}");
        foreach (var p in disagreements.Take(50)) Console.WriteLine($"  page 1:{p}");
        return 0;
    }

    /// <summary>Byte-compare the structural page map against a restored copy of the same backup.</summary>
    static int Validate(PageFile pf, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("against", out var mdf)) throw new ArgumentException("--against <restored.mdf> is required");
        var (exact, hdrOnly, body) = pf.CompareAgainst(mdf);
        Console.WriteLine($"mapped pages:     {pf.PageCount}");
        Console.WriteLine($"byte-identical:   {exact}");
        Console.WriteLine($"header-only diff: {hdrOnly}");
        Console.WriteLine($"body diff:        {body.Count}");
        foreach (var pid in body.Take(2000)) Console.WriteLine($"  page 1:{pid}");
        if (body.Count > 2000) Console.WriteLine($"  ... {body.Count - 2000} more");
        return body.Count == 0 ? 0 : 1;
    }

    static int Tables(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        var sym = LoadSymbols(opts);
        foreach (var t in BcTables(cat).OrderBy(t => t.Company).ThenBy(t => t.TableName))
        {
            var al = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
            string alcol = sym is null ? "" : al is null ? "\t-" : $"\t{al.Id} \"{al.Name}\" ({al.AppName})";
            Console.WriteLine($"{t.RowSet.Rows,8}  {(t.RowSet.CompressionLevel switch { 0 => "none", 1 => "row ", 2 => "page", var x => x.ToString() })}  {t.Company ?? "-"}\t{t.TableName}{alcol}");
        }
        Console.Error.WriteLine($"[{cat.Objects.Count} objects, {pf.PageCount} pages, {pf.SupersededPageCount} pages superseded by the changed-extent re-read]");
        return 0;
    }

    /// <summary>Companies = the distinct company segments of per-company table names.</summary>
    static int Companies(Catalog cat)
    {
        foreach (var c in BcTables(cat).Where(t => t.Company is { Length: > 0 })
                     .Select(t => t.Company!).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine(c);
        return 0;
    }

    static string StripExt(string tableName)
        => tableName.EndsWith("$ext", StringComparison.Ordinal) ? tableName[..^4] : tableName;

    /// <summary>AL schema of one table: field ids, AL names and types, and the SQL columns they map to.</summary>
    static int Describe(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        var sym = LoadSymbols(opts) ?? throw new ArgumentException("describe requires --symbols (a .app package or SymbolReference.json)");
        var t = ResolveTable(cat, opts);
        var al = sym.FindForSqlTable(StripExt(t.TableName), t.AppId)
            ?? throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        cat.LoadColumnMetadata(t.Obj.ObjectId);
        var cols = cat.Columns[t.Obj.ObjectId];
        Console.WriteLine($"Table {al.Id} \"{al.Name}\" — app \"{al.AppName}\" ({al.AppId})");
        Console.WriteLine($"SQL object: {t.Obj.Name}");
        Console.WriteLine($"{"Id",6}  {"AL name",-40} {"AL type",-28} {"SQL column",-40} SQL type");
        foreach (var f in al.Fields)
        {
            if (f.FieldClass != "Normal")
            {
                Console.WriteLine($"{f.Id,6}  {f.Name,-40} {f.TypeName,-28} {"-",-40} ({f.FieldClass}: computed, not stored)");
                continue;
            }
            var sqlCol = cols.FirstOrDefault(c => c.Name.Equals(SqlNames.Normalize(f.Name), StringComparison.OrdinalIgnoreCase));
            if (sqlCol is null)
                Console.WriteLine($"{f.Id,6}  {f.Name,-40} {f.TypeName,-28} {"-",-40} (no SQL column — removed or disabled)");
            else
                Console.WriteLine($"{f.Id,6}  {f.Name,-40} {f.TypeName,-28} {sqlCol.Name,-40} {SqlTypes.Name(sqlCol.XType)}{Len(sqlCol)}");
        }
        foreach (var c in cols.Where(c => c.Name.StartsWith('$') || c.Name == "timestamp"))
            Console.WriteLine($"{"-",6}  {"-",-40} {"-",-28} {c.Name,-40} {SqlTypes.Name(c.XType)}{Len(c)} (system column)");
        return 0;
    }

    static string Len(SysColumn c) => c.XType switch
    {
        231 or 239 => c.MaxLength < 0 ? "(max)" : $"({c.MaxLength / 2})",
        167 or 175 or 165 or 173 => c.MaxLength < 0 ? "(max)" : $"({c.MaxLength})",
        106 or 108 => $"({c.Precision},{c.Scale})",
        _ => "",
    };

    static IEnumerable<(List<SysColumn> cols, List<string> headers, List<object?[]> rows)> ReadCore(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        var t = ResolveTable(cat, opts);
        var sym = LoadSymbols(opts);
        var alTable = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
        if (sym is not null && alTable is null)
            throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        cat.LoadColumnMetadata(t.Obj.ObjectId);
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
        var headers = selected.Select(c =>
            alTable?.Fields.FirstOrDefault(f => f.FieldClass == "Normal"
                && SqlNames.Normalize(f.Name).Equals(c.Name, StringComparison.OrdinalIgnoreCase))?.Name ?? c.Name).ToList();
        int top = opts.TryGetValue("top", out var ts) ? int.Parse(ts) : int.MaxValue;
        // --sha256 "A,B": replace those columns' binary values by "sha256:<hex>" — lets
        // fixtures assert large blobs without storing them (export side: HASHBYTES).
        var shaCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (opts.TryGetValue("sha256", out var sh))
            foreach (var n in sh.Split(',')) shaCols.Add(BcNormalize(n.Trim()));
        bool compressed = t.RowSet.CompressionLevel > 0;
        var lob = new LobReader(pf);
        var outRows = new List<object?[]>();
        foreach (var row in new TableReader(pf, cat).ReadRows(t.Obj.ObjectId))
        {
            if (outRows.Count >= top) break;
            outRows.Add(selected.Select(c =>
            {
                var v = SqlTypes.Decode(row[c.Name], c, compressed, lob);
                if (shaCols.Contains(c.Name))
                {
                    if (v is null) return null;
                    if (v is not string s || !s.StartsWith("0x", StringComparison.Ordinal))
                        throw new ArgumentException($"--sha256 column '{c.Name}' did not decode to binary data");
                    return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(s[2..])));
                }
                return v;
            }).ToArray());
        }
        yield return (selected, headers, outRows);
    }

    static int Read(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        foreach (var (selected, headers, rows) in ReadCore(pf, cat, opts))
        {
            bool json = opts.TryGetValue("format", out var fm) && fm == "json";
            if (json)
            {
                Console.WriteLine("[");
                for (int r = 0; r < rows.Count; r++)
                {
                    var parts = headers.Select((h, i) => $"{J(h)}: {JVal(rows[r][i])}");
                    Console.WriteLine("  {" + string.Join(", ", parts) + "}" + (r < rows.Count - 1 ? "," : ""));
                }
                Console.WriteLine("]");
            }
            else
            {
                Console.WriteLine(string.Join("|", headers));
                foreach (var r in rows)
                    Console.WriteLine(string.Join("|", r.Select(v => Fmt(v))));
            }
        }
        return 0;
    }

    static string Fmt(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    static string J(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)ch).ToString("x4")); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
    static string JVal(object? v) => v switch
    {
        null => "null", bool b => b ? "true" : "false",
        long or byte or int => v.ToString()!,
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => J(v.ToString()!)
    };

    /// <summary>
    /// Serve mode: the backup is opened once, then requests arrive one JSON object per
    /// stdin line and each is answered with one JSON line — the process-per-read tax
    /// (~1 s of open state plus .NET startup, measured) is paid once. Requests:
    /// {"id": any, "cmd": "read"|"tables"|"companies"|"describe"|"quit", "table": ..,
    /// "company": .., "top": .., "select": .., "sha256": ..}. The id is echoed back
    /// verbatim. A failed request answers {"ok": false, "error": ..} and the session
    /// stays up; value formatting matches `read --format json`.
    /// </summary>
    public static int Serve(PageFile pf, Catalog cat, Dictionary<string, string> startupOpts, TextReader input, TextWriter output)
    {
        var sym = LoadSymbols(startupOpts);
        cat.LoadColumnMetadata();   // serve answers many tables: one full load beats per-object walks
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            string idJson = "null";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                idJson = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
                string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                if (cmd == "quit") { output.WriteLine($"{{\"id\": {idJson}, \"ok\": true}}"); break; }
                var reqOpts = new Dictionary<string, string>();
                foreach (var name in new[] { "table", "company", "top", "select", "sha256" })
                    if (root.TryGetProperty(name, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null)
                        reqOpts[name] = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : v.GetRawText();
                output.WriteLine(cmd switch
                {
                    "read" => ServeRead(pf, cat, reqOpts, idJson),
                    "tables" => ServeTables(cat, sym, idJson),
                    "companies" => ServeCompanies(cat, idJson),
                    "describe" => ServeDescribe(cat, sym, reqOpts, idJson),
                    _ => throw new ArgumentException($"unknown cmd '{cmd}' — expected read, tables, companies, describe, or quit"),
                });
            }
            catch (Exception ex)
            {
                output.WriteLine($"{{\"id\": {idJson}, \"ok\": false, \"error\": {J(ex.Message)}}}");
            }
        }
        return 0;
    }

    static string ServeRead(PageFile pf, Catalog cat, Dictionary<string, string> opts, string idJson)
    {
        foreach (var (_, headers, rows) in ReadCore(pf, cat, opts))
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\": ").Append(idJson).Append(", \"ok\": true, \"headers\": [")
              .Append(string.Join(", ", headers.Select(J))).Append("], \"rows\": [");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('[').Append(string.Join(", ", rows[i].Select(JVal))).Append(']');
            }
            return sb.Append("]}").ToString();
        }
        throw new InvalidOperationException("unreachable: ReadCore yields exactly once");
    }

    static string ServeTables(Catalog cat, SymbolStore? sym, string idJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\": ").Append(idJson).Append(", \"ok\": true, \"tables\": [");
        bool first = true;
        foreach (var t in BcTables(cat).OrderBy(t => t.Company).ThenBy(t => t.TableName))
        {
            var al = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("{\"company\": ").Append(t.Company is null ? "null" : J(t.Company))
              .Append(", \"name\": ").Append(J(t.TableName))
              .Append(", \"rows\": ").Append(t.RowSet.Rows)
              .Append(", \"compression\": ").Append(J(t.RowSet.CompressionLevel switch { 0 => "none", 1 => "row", 2 => "page", var x => x.ToString() }));
            if (al != null)
                sb.Append(", \"al\": {\"id\": ").Append(al.Id).Append(", \"name\": ").Append(J(al.Name))
                  .Append(", \"app\": ").Append(J(al.AppName)).Append('}');
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    static string ServeCompanies(Catalog cat, string idJson)
        => "{\"id\": " + idJson + ", \"ok\": true, \"companies\": ["
         + string.Join(", ", BcTables(cat).Where(t => t.Company is { Length: > 0 })
               .Select(t => t.Company!).Distinct().OrderBy(x => x, StringComparer.Ordinal).Select(J))
         + "]}";

    static string ServeDescribe(Catalog cat, SymbolStore? sym, Dictionary<string, string> opts, string idJson)
    {
        if (sym is null) throw new ArgumentException("describe requires serve to be started with --symbols (a .app package or SymbolReference.json)");
        var t = ResolveTable(cat, opts);
        var al = sym.FindForSqlTable(StripExt(t.TableName), t.AppId)
            ?? throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        cat.LoadColumnMetadata(t.Obj.ObjectId);
        var cols = cat.Columns[t.Obj.ObjectId];
        var sb = new StringBuilder();
        sb.Append("{\"id\": ").Append(idJson).Append(", \"ok\": true, \"table\": {\"id\": ").Append(al.Id)
          .Append(", \"name\": ").Append(J(al.Name)).Append(", \"app\": ").Append(J(al.AppName))
          .Append(", \"appId\": ").Append(J(al.AppId)).Append(", \"sqlObject\": ").Append(J(t.Obj.Name)).Append("}, \"fields\": [");
        bool first = true;
        foreach (var f in al.Fields)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("{\"id\": ").Append(f.Id).Append(", \"name\": ").Append(J(f.Name))
              .Append(", \"type\": ").Append(J(f.TypeName));
            if (f.FieldClass != "Normal")
                sb.Append(", \"fieldClass\": ").Append(J(f.FieldClass)).Append(", \"sqlColumn\": null");
            else
            {
                var sqlCol = cols.FirstOrDefault(c => c.Name.Equals(SqlNames.Normalize(f.Name), StringComparison.OrdinalIgnoreCase));
                sb.Append(", \"sqlColumn\": ").Append(sqlCol is null ? "null" : J(sqlCol.Name))
                  .Append(", \"sqlType\": ").Append(sqlCol is null ? "null" : J(SqlTypes.Name(sqlCol.XType) + Len(sqlCol)));
            }
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>Compare decoded rows against a fixture exported from a restored SQL Server (the oracle). Order-insensitive.</summary>
    static int Verify(PageFile pf, Catalog cat, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("fixture", out var fixPath)) throw new ArgumentException("--fixture is required");
        // Fixture lines may end with "|#" (a sentinel the oracle export appends so that
        // trailing spaces inside the last real column survive); strip it before comparing.
        var expected = File.ReadAllLines(fixPath).Where(l => l.Length > 0)
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var (_, _, rows) in ReadCore(pf, cat, opts))
        {
            var actual = rows.Select(r => string.Join("|", r.Select(v => Fmt(v))))
                             .OrderBy(x => x, StringComparer.Ordinal).ToList();
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
