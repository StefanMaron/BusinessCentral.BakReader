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
            if (!CliKeys.ContainsKey(cmd)) return Usage();
            var path = args[1];
            if (!File.Exists(path)) { Console.Error.WriteLine($"error: file not found: {path}"); return 2; }
            var opts = ParseOpts(args.Skip(2), cmd);
            using var src = BcSource.Open(path, prefetch: opts.ContainsKey("prefetch"));
            // check and validate are about the page map a backup has and a bacpac does not.
            if (cmd is "check" or "validate")
            {
                if (src is not BakSource bak)
                    return Fail($"{cmd} works on a .bak: a .bacpac has no page map to check");
                return cmd == "check" ? Check(bak.PageFile) : Validate(bak.PageFile, opts);
            }
            return cmd switch
            {
                "tables" => Tables(src, opts),
                "companies" => Companies(src),
                "describe" => Describe(src, opts),
                "read" => Read(src, opts),
                "verify" => Verify(src, opts),
                "serve" => Serve(src, opts, Console.In, Console.Out),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int Fail(string message) { Console.Error.WriteLine($"error: {message}"); return 2; }

    static int Usage()
    {
        Console.Error.WriteLine("""
            usage:  <file> is a SQL Server backup (.bak) or a BC cloud export (.bacpac)
              bcbak tables <file> [--symbols <apps>]               list readable BC tables
              bcbak companies <file>                               list the companies in the database
              bcbak describe <file> --table <name> --symbols <apps>   AL schema of a table (field ids, AL types, SQL columns)
              bcbak check  <file.bak>                              cross-check the structural page map against page self-identification
              bcbak validate <file.bak> --against <restored.mdf>   byte-compare every mapped page against a restored copy
              bcbak read   <file> --table <name> [--company <c>] [--app <id-prefix>] [--top N] [--select "A,B"] [--merge-extensions] [--format tsv|json]
              bcbak serve  <file> [--symbols <apps>] [--prefetch]      open once, answer requests over stdin/stdout
                                                             (--prefetch: read the whole file into the OS cache in the background; .bak only)
                                                             (one JSON request per line: {"id": .., "cmd": "read"|"tables"|"companies"|"describe"|"quit",
                                                              "table": .., "company": .., "app": .., "top": .., "select": .., "sha256": ..,
                                                              "merge-extensions": ..}; one JSON response line each. A key the command
                                                              does not accept fails the request instead of being ignored.)
              bcbak verify <file> --fixture <fixture.tsv> --table <name> --select "A,B"
            check and validate are page-map commands and need a .bak.
            --prefetch works with any command. An option the command does not accept fails
            the command instead of being ignored, so a mistyped --compayn or --mergeExtensions
            is an error and not a plausible wrong answer with exit 0.
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

    static readonly string[] ReadOpts =
        { "table", "company", "app", "top", "select", "sha256", "merge-extensions", "symbols", "format" };

    /// <summary>
    /// The options each subcommand accepts. `verify` reads through the same path as
    /// `read`, so it accepts every read option as well as its own.
    /// </summary>
    static readonly Dictionary<string, string[]> CliKeys = new(StringComparer.Ordinal)
    {
        ["tables"] = new[] { "symbols" },
        ["companies"] = Array.Empty<string>(),
        ["describe"] = new[] { "table", "company", "app", "symbols" },
        ["check"] = Array.Empty<string>(),
        ["validate"] = new[] { "against" },
        ["read"] = ReadOpts,
        ["verify"] = ReadOpts.Concat(new[] { "fixture" }).ToArray(),
        ["serve"] = new[] { "symbols" },
    };

    /// <summary>Accepted by every subcommand: it is applied when the file is opened.</summary>
    static readonly string[] GlobalOpts = { "prefetch" };

    /// <summary>Options that are switches — they take no value.</summary>
    static readonly HashSet<string> ValuelessOpts = new(StringComparer.Ordinal) { "prefetch", "merge-extensions" };

    /// <summary>
    /// The command line's options for one subcommand.
    ///
    /// An option the subcommand does not accept fails the command instead of being
    /// dropped, the same way a serve request refuses a key it does not know. The command
    /// line is a programmatic surface too — callers invoke bcbak per table from a script
    /// — and nothing there reads the output: a mistyped --compayn silently reading every
    /// company, a mistyped --tpo silently losing the row limit, or --mergeExtensions
    /// silently returning the base table without any of its extension fields, is a wrong
    /// answer reported as success (GitHub issue #18, the command-line half of #15).
    ///
    /// For the same reason a value-taking option left without a value is refused rather
    /// than quietly becoming the string "true", and a stray positional argument is
    /// refused rather than dropped.
    /// </summary>
    public static Dictionary<string, string> ParseOpts(IEnumerable<string> args, string cmd)
    {
        if (!CliKeys.TryGetValue(cmd, out var accepted))
            throw new ArgumentException($"unknown command '{cmd}' — expected {string.Join(", ", CliKeys.Keys)}");
        var d = new Dictionary<string, string>();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (key != null) throw new ArgumentException($"--{key} needs a value, but '{a}' is the next option");
                key = a[2..];
                if (Array.IndexOf(accepted, key) < 0 && Array.IndexOf(GlobalOpts, key) < 0)
                    throw new ArgumentException($"unknown option '{a}' for '{cmd}' — accepted: "
                        + string.Join(", ", accepted.Concat(GlobalOpts).Select(k => "--" + k)));
                d[key] = "true";
                if (ValuelessOpts.Contains(key)) key = null;
            }
            else if (key != null) { d[key] = a; key = null; }
            else throw new ArgumentException($"unexpected argument '{a}' for '{cmd}' — options are named, e.g. --table \"{a}\"");
        }
        if (key != null) throw new ArgumentException($"--{key} needs a value");
        // --format is a command-line-only option, so it is checked here; --top is checked
        // where it is consumed, because a serve request carries it too.
        if (d.TryGetValue("format", out var fmt) && fmt is not ("tsv" or "json"))
            throw new ArgumentException($"--format expects tsv or json, got '{fmt}'");
        return d;
    }

    // BC replaces characters invalid in SQL identifiers with '_' when building object names.
    static string BcNormalize(string alName)
    {
        var sb = new StringBuilder();
        foreach (var ch in alName) sb.Append(ch is '.' or '"' or '\\' or '/' or '\'' or '%' or '[' or ']' ? '_' : ch);
        return sb.ToString();
    }

    sealed record BcTable(SourceTable Table, string? Company, string TableName, string? AppId)
    {
        public string SqlName => Table.Name;
    }

    static List<BcTable> BcTables(IBcSource src)
    {
        var list = new List<BcTable>();
        foreach (var o in src.Tables)
        {
            var segs = o.Name.Split('$');
            string? company = null, appId = null; string tableName;
            bool isExt = segs[^1] == "ext";
            var core = isExt ? segs[..^1] : segs;
            if (core.Length >= 3 && Guid.TryParse(core[^1], out _)) { company = string.Join("$", core[..^2]); tableName = core[^2]; appId = core[^1]; }
            else if (core.Length == 2 && Guid.TryParse(core[^1], out _)) { tableName = core[0]; appId = core[1]; }
            else if (core.Length == 2) { company = core[0]; tableName = core[1]; }
            else tableName = core[0];
            if (isExt) tableName += "$ext";
            // A name that matches no <company>$<table>$<appid> shape (the platform's
            // $ndo$... tables lead with '$') keeps its raw SQL name — an empty derived
            // name would make the table undiscoverable from the listing (issue #14).
            if (tableName.Length == 0 || tableName == "$ext") { company = null; appId = null; tableName = o.Name; }
            list.Add(new BcTable(o, company, tableName, appId));
        }
        return list;
    }

    static BcTable ResolveTable(IBcSource src, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("table", out var want)) throw new ArgumentException("--table is required");
        var norm = BcNormalize(want);
        var all = BcTables(src);
        var matches = all.Where(t => t.SqlName.Equals(want, StringComparison.OrdinalIgnoreCase)
                                  || t.TableName.Equals(norm, StringComparison.OrdinalIgnoreCase)).ToList();
        if (opts.TryGetValue("company", out var comp))
            matches = matches.Where(t => t.Company != null && t.Company.StartsWith(comp, StringComparison.OrdinalIgnoreCase)).ToList();
        // Two installed apps may define the same table name in the same company (legal
        // via AL namespaces; Microsoft's own demo database ships Dimension Set Entry
        // twice) — the app id is the only distinguishing part, selectable by prefix.
        if (opts.TryGetValue("app", out var app))
            matches = matches.Where(t => t.AppId != null && t.AppId.StartsWith(app, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new ArgumentException($"no table matches '{want}'");
        if (matches.Select(m => m.Company).Distinct().Count() > 1)
        {
            var withRows = matches.Where(m => m.Table.RowCount() > 0).ToList();
            if (withRows.Select(m => m.Company).Distinct().Count() == 1) matches = withRows;
            else throw new ArgumentException(
                $"table '{want}' exists in multiple companies ({string.Join(", ", matches.Select(m => m.Company).Distinct())}) — use --company");
        }
        if (matches.Count > 1)
        {
            string hint = matches.Select(m => m.AppId).Distinct().Count() > 1
                ? " — use --app <app-id-prefix> to select the defining app" : "";
            throw new ArgumentException($"ambiguous table '{want}': {string.Join(" | ", matches.Select(m => m.SqlName))}{hint}");
        }
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

    static int Tables(IBcSource src, Dictionary<string, string> opts)
    {
        var sym = LoadSymbols(opts);
        foreach (var t in BcTables(src).OrderBy(t => t.Company).ThenBy(t => t.TableName))
        {
            var al = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
            string alcol = sym is null ? "" : al is null ? "\t-" : $"\t{al.Id} \"{al.Name}\" ({al.AppName})";
            Console.WriteLine($"{t.Table.RowCount(),8}  {t.Table.Compression,-4}  {t.Company ?? "-"}\t{t.TableName}{alcol}");
        }
        Console.Error.WriteLine(src.Banner);
        return 0;
    }

    /// <summary>Companies = the distinct company segments of per-company table names.</summary>
    static int Companies(IBcSource src)
    {
        foreach (var c in BcTables(src).Where(t => t.Company is { Length: > 0 })
                     .Select(t => t.Company!).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            Console.WriteLine(c);
        return 0;
    }

    static string StripExt(string tableName)
        => tableName.EndsWith("$ext", StringComparison.Ordinal) ? tableName[..^4] : tableName;

    /// <summary>AL schema of one table: field ids, AL names and types, and the SQL columns they map to.</summary>
    static int Describe(IBcSource src, Dictionary<string, string> opts)
    {
        var sym = LoadSymbols(opts) ?? throw new ArgumentException("describe requires --symbols (a .app package or SymbolReference.json)");
        var t = ResolveTable(src, opts);
        var al = sym.FindForSqlTable(StripExt(t.TableName), t.AppId)
            ?? throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        var cols = src.Columns(t.Table);
        Console.WriteLine($"Table {al.Id} \"{al.Name}\" — app \"{al.AppName}\" ({al.AppId})");
        Console.WriteLine($"SQL object: {t.SqlName}");
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
        var (companion, extFields) = ExtensionColumns(src, sym, t);
        foreach (var (c, extApp, ext, field) in extFields)
        {
            if (field != null)
                Console.WriteLine($"{field.Id,6}  {field.Name,-40} {field.TypeName,-28} {c.Name,-40} {SqlTypes.Name(c.XType)}{Len(c)} (tableextension \"{ext!.Name}\", {ext.AppName})");
            else
                Console.WriteLine($"{"-",6}  {"-",-40} {"-",-28} {c.Name,-40} {SqlTypes.Name(c.XType)}{Len(c)} (extension field of app {extApp} — not in the provided symbols)");
        }
        if (companion != null)
            Console.WriteLine($"Companion:  {companion.SqlName} (extension fields; read together with --merge-extensions)");
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

    /// <summary>Companion-table column name "&lt;Field&gt;$&lt;extending app id&gt;" split, or null.</summary>
    static (string BaseName, string ExtAppId)? SplitExtColumn(string name)
    {
        int i = name.LastIndexOf('$');
        return i > 0 && Guid.TryParse(name[(i + 1)..], out _) ? (name[..i], name[(i + 1)..]) : null;
    }

    /// <summary>
    /// The $ext companion of a base table (or null), and its extension-field columns —
    /// each with the AL field its extending app's tableextension symbols define, when
    /// symbols are available and resolve it.
    /// </summary>
    static (BcTable? Companion, List<(SysColumn Col, string ExtAppId, AlTableExtension? Ext, AlField? Field)> ExtCols)
        ExtensionColumns(IBcSource src, SymbolStore? sym, BcTable t)
    {
        var companion = BcTables(src).FirstOrDefault(x =>
            x.Company == t.Company && x.AppId == t.AppId
            && x.TableName.Equals(t.TableName + "$ext", StringComparison.OrdinalIgnoreCase));
        var extCols = new List<(SysColumn, string, AlTableExtension?, AlField?)>();
        if (companion is null) return (null, extCols);
        foreach (var c in src.Columns(companion.Table))
        {
            if (SplitExtColumn(c.Name) is not { } split) continue;   // base-key mirror / timestamp
            var hit = sym?.FindExtensionField(split.ExtAppId, StripExt(t.TableName), t.AppId, split.BaseName);
            extCols.Add((c, split.ExtAppId, hit?.Ext, hit?.Field));
        }
        return (companion, extCols);
    }

    /// <summary>
    /// Resolves one --select / --sha256 name to a column, by SQL column name or AL field
    /// name. The name is matched as written first and only then trimmed: BC turns an AL
    /// field name carrying a leading or trailing space into a SQL column with that space
    /// (observed: "Reten_ Pol_ Filtering "), and trimming first made such a column
    /// unaddressable — while the trim itself is what lets --select "A, B" work (issue #16).
    /// </summary>
    static (SysColumn Col, bool FromExt, string Header) ResolveColumn(
        string name, List<(SysColumn Col, bool FromExt, string Header)> pool, string what)
    {
        var hits = Match(name);
        if (hits.Count == 0 && name != name.Trim()) hits = Match(name.Trim());
        if (hits.Count == 0)
            throw new ArgumentException($"{what}column '{name}' not found; available: {string.Join(", ", pool.Select(a => a.Col.Name))}");
        if (hits.Count > 1)
            throw new ArgumentException($"{what}column '{name}' is ambiguous: {string.Join(" | ", hits.Select(h => h.Col.Name))} — use the full SQL column name");
        return hits[0];

        List<(SysColumn Col, bool FromExt, string Header)> Match(string token)
        {
            var n = BcNormalize(token);
            return pool.Where(a => a.Col.Name.Equals(n, StringComparison.OrdinalIgnoreCase)
                                || SqlNames.Normalize(a.Header).Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    static IEnumerable<(List<SysColumn> cols, List<string> headers, List<object?[]> rows)> ReadCore(IBcSource src, Dictionary<string, string> opts, SymbolStore? preloadedSym = null)
    {
        var t = ResolveTable(src, opts);
        var sym = preloadedSym ?? LoadSymbols(opts);
        var alTable = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
        if (sym is not null && alTable is null)
            throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        var cols = src.Columns(t.Table);

        // --merge-extensions: one AL record = base row + $ext companion row, joined on the
        // companion's own key. Not the base table's clustered key: BC keys a companion on
        // its base table's AL primary key, while the base table is clustered on whichever
        // key carries Clustered = 1 — usually the same key, but not in Posted Gen. Journal
        // Line, where the primary key is Line No. alone and the clustered key is Journal
        // Template Name, Journal Batch Name, Line No. (PROVENANCE.md, GitHub issue #17).
        // A base row without a companion row reads its extension fields as NULL.
        BcTable? companion = null;
        var extCols = new List<(SysColumn Col, string ExtAppId, AlTableExtension? Ext, AlField? Field)>();
        List<SysColumn> keyCols = new();   // the join key as base-table columns
        List<SysColumn> compKey = new();   // the same key as companion columns, same order
        if (opts.ContainsKey("merge-extensions"))
        {
            (companion, extCols) = ExtensionColumns(src, sym, t);
            if (companion != null)
            {
                var compCols = src.Columns(companion.Table);
                var key = src.RowKeyColumns(companion.Table);
                if (key.Count == 0)
                    throw new InvalidDataException($"--merge-extensions: companion {companion.SqlName} has no key to join it to {t.SqlName} on — refusing to guess");
                foreach (var n in key)
                {
                    compKey.Add(compCols.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"key column {n} of {companion.SqlName} is not among its columns"));
                    keyCols.Add(cols.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"companion {companion.SqlName} is keyed on {n}, which base table {t.SqlName} does not have — cannot join, refusing to guess"));
                }
            }
        }

        // A selectable column: from the base table or the companion, with its AL header.
        var all = new List<(SysColumn Col, bool FromExt, string Header)>();
        foreach (var c in cols)
            all.Add((c, false, alTable?.Fields.FirstOrDefault(f => f.FieldClass == "Normal"
                && SqlNames.Normalize(f.Name).Equals(c.Name, StringComparison.OrdinalIgnoreCase))?.Name ?? c.Name));
        foreach (var (c, _, _, field) in extCols)
            all.Add((c, true, field?.Name ?? c.Name));

        List<(SysColumn Col, bool FromExt, string Header)> selected;
        if (opts.TryGetValue("select", out var sel))
            selected = sel.Split(',').Select(name => ResolveColumn(name, all, "")).ToList();
        else selected = all;
        var headers = selected.Select(s => s.Header).ToList();
        // Checked here rather than in ParseOpts because a serve request carries "top" too,
        // and int.Parse's own message names neither the option nor the surface it came from.
        int top = int.MaxValue;
        if (opts.TryGetValue("top", out var ts) && (!int.TryParse(ts, out top) || top < 0))
            throw new ArgumentException($"top expects a whole number of rows, got '{ts}'");
        // --sha256 "A,B": replace those columns' binary values by "sha256:<hex>" — lets
        // fixtures assert large blobs without storing them (export side: HASHBYTES). A name
        // that matches no selected column is refused: silently doing nothing would return
        // the raw value where a hash was asked for (issue #16).
        var shaCols = new HashSet<string>(StringComparer.Ordinal);
        if (opts.TryGetValue("sha256", out var sh))
            foreach (var n in sh.Split(',')) shaCols.Add(ResolveColumn(n, selected, "--sha256 ").Col.Name);

        // Only the columns the answer needs are decoded — the selected base columns plus,
        // when joining a companion, the clustered key. Selecting one column of a table with
        // blobs must not pay for the blobs.
        var baseWanted = selected.Where(s => !s.FromExt).Select(s => s.Col)
            .Concat(companion != null ? keyCols : Enumerable.Empty<SysColumn>())
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // With a companion: read it fully first, keyed by the decoded join-key values.
        Dictionary<string, IReadOnlyDictionary<string, object?>>? extRows = null;
        if (companion != null && selected.Any(s => s.FromExt))
        {
            var compWanted = compKey.Concat(selected.Where(s => s.FromExt).Select(s => s.Col))
                .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            extRows = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            foreach (var row in src.ReadRows(companion.Table, compWanted))
            {
                string key = string.Join("\u0001", compKey.Select(c => Fmt(row[c.Name])));
                extRows[key] = row;
            }
        }

        var outRows = new List<object?[]>();
        foreach (var row in src.ReadRows(t.Table, baseWanted))
        {
            if (outRows.Count >= top) break;
            IReadOnlyDictionary<string, object?>? extRow = null;
            if (extRows != null)
                extRows.TryGetValue(string.Join("\u0001", keyCols.Select(c => Fmt(row[c.Name]))), out extRow);
            outRows.Add(selected.Select(s =>
            {
                object? v;
                if (!s.FromExt) v = row[s.Col.Name];
                else v = extRow != null ? extRow[s.Col.Name] : null;
                if (shaCols.Contains(s.Col.Name))
                {
                    if (v is null) return null;
                    if (v is not string str || !str.StartsWith("0x", StringComparison.Ordinal))
                        throw new ArgumentException($"--sha256 column '{s.Col.Name}' did not decode to binary data");
                    return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(str[2..])));
                }
                return v;
            }).ToArray());
        }
        yield return (selected.Select(s => s.Col).ToList(), headers, outRows);
    }

    static int Read(IBcSource src, Dictionary<string, string> opts)
    {
        foreach (var (selected, headers, rows) in ReadCore(src, opts))
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
    /// The keys each serve command accepts, besides "id" and "cmd". One spelling per
    /// option and no aliases: a caller guessing "mergeExtensions" is told the real key
    /// rather than quietly getting a base-table read (issue #15).
    /// </summary>
    static readonly Dictionary<string, string[]> ServeKeys = new(StringComparer.Ordinal)
    {
        ["read"] = new[] { "table", "company", "app", "top", "select", "sha256", "merge-extensions" },
        ["describe"] = new[] { "table", "company", "app" },
        ["tables"] = Array.Empty<string>(),
        ["companies"] = Array.Empty<string>(),
        ["quit"] = Array.Empty<string>(),
    };

    /// <summary>
    /// Serve mode: the backup is opened once, then requests arrive one JSON object per
    /// stdin line and each is answered with one JSON line — the process-per-read tax
    /// (~1 s of open state plus .NET startup, measured) is paid once. Requests:
    /// {"id": any, "cmd": "read"|"tables"|"companies"|"describe"|"quit", "table": ..,
    /// "company": .., "app": .., "top": .., "select": .., "sha256": ..,
    /// "merge-extensions": ..}. The id is echoed back verbatim. A failed request answers
    /// {"ok": false, "error": ..} and the session stays up; value formatting matches
    /// `read --format json`.
    ///
    /// A key the command does not accept fails the request instead of being dropped.
    /// Serve exists for programmatic callers that build requests in code, where nobody
    /// reads the output: a mistyped "tpo" silently losing a row limit, or a mistyped
    /// "compayn" silently reading every company, is a wrong answer reported as success.
    /// </summary>
    public static int Serve(IBcSource src, Dictionary<string, string> startupOpts, TextReader input, TextWriter output)
    {
        var sym = LoadSymbols(startupOpts);
        src.PreloadMetadata();   // container-specific: a bacpac parses model.xml here, a .bak does nothing
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
                if (!ServeKeys.TryGetValue(cmd, out var accepted))
                    throw new ArgumentException($"unknown cmd '{cmd}' — expected {string.Join(", ", ServeKeys.Keys)}");
                foreach (var prop in root.EnumerateObject())
                    if (prop.Name is not ("id" or "cmd") && Array.IndexOf(accepted, prop.Name) < 0)
                        throw new ArgumentException($"unknown request key '{prop.Name}' for cmd '{cmd}' — accepted: "
                            + string.Join(", ", new[] { "id", "cmd" }.Concat(accepted)));
                if (cmd == "quit") { output.WriteLine($"{{\"id\": {idJson}, \"ok\": true}}"); break; }
                var reqOpts = new Dictionary<string, string>();
                foreach (var name in accepted)
                    if (root.TryGetProperty(name, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null)
                        reqOpts[name] = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : v.GetRawText();
                output.WriteLine(cmd switch
                {
                    "read" => ServeRead(src, sym, reqOpts, idJson),
                    "tables" => ServeTables(src, sym, idJson),
                    "companies" => ServeCompanies(src, idJson),
                    "describe" => ServeDescribe(src, sym, reqOpts, idJson),
                    _ => throw new InvalidOperationException($"cmd '{cmd}' is accepted but not dispatched"),
                });
            }
            catch (Exception ex)
            {
                output.WriteLine($"{{\"id\": {idJson}, \"ok\": false, \"error\": {J(ex.Message)}}}");
            }
        }
        return 0;
    }

    static string ServeRead(IBcSource src, SymbolStore? sym, Dictionary<string, string> opts, string idJson)
    {
        foreach (var (_, headers, rows) in ReadCore(src, opts, sym))
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

    static string ServeTables(IBcSource src, SymbolStore? sym, string idJson)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\": ").Append(idJson).Append(", \"ok\": true, \"tables\": [");
        bool first = true;
        foreach (var t in BcTables(src).OrderBy(t => t.Company).ThenBy(t => t.TableName))
        {
            var al = sym?.FindForSqlTable(StripExt(t.TableName), t.AppId);
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("{\"company\": ").Append(t.Company is null ? "null" : J(t.Company))
              .Append(", \"name\": ").Append(J(t.TableName))
              .Append(", \"rows\": ").Append(t.Table.RowCount())
              .Append(", \"compression\": ").Append(J(t.Table.Compression));
            if (al != null)
                sb.Append(", \"al\": {\"id\": ").Append(al.Id).Append(", \"name\": ").Append(J(al.Name))
                  .Append(", \"app\": ").Append(J(al.AppName)).Append('}');
            sb.Append('}');
        }
        return sb.Append("]}").ToString();
    }

    static string ServeCompanies(IBcSource src, string idJson)
        => "{\"id\": " + idJson + ", \"ok\": true, \"companies\": ["
         + string.Join(", ", BcTables(src).Where(t => t.Company is { Length: > 0 })
               .Select(t => t.Company!).Distinct().OrderBy(x => x, StringComparer.Ordinal).Select(J))
         + "]}";

    static string ServeDescribe(IBcSource src, SymbolStore? sym, Dictionary<string, string> opts, string idJson)
    {
        if (sym is null) throw new ArgumentException("describe requires serve to be started with --symbols (a .app package or SymbolReference.json)");
        var t = ResolveTable(src, opts);
        var al = sym.FindForSqlTable(StripExt(t.TableName), t.AppId)
            ?? throw new ArgumentException($"table '{t.TableName}' (app {t.AppId ?? "-"}) is not defined in the provided symbols — pass the app that defines it");
        var cols = src.Columns(t.Table);
        var sb = new StringBuilder();
        sb.Append("{\"id\": ").Append(idJson).Append(", \"ok\": true, \"table\": {\"id\": ").Append(al.Id)
          .Append(", \"name\": ").Append(J(al.Name)).Append(", \"app\": ").Append(J(al.AppName))
          .Append(", \"appId\": ").Append(J(al.AppId)).Append(", \"sqlObject\": ").Append(J(t.SqlName)).Append("}, \"fields\": [");
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
        var (_, extFields) = ExtensionColumns(src, sym, t);
        foreach (var (c, extApp, ext, field) in extFields)
        {
            sb.Append(", {\"id\": ").Append(field?.Id.ToString() ?? "null")
              .Append(", \"name\": ").Append(J(field?.Name ?? c.Name))
              .Append(", \"type\": ").Append(field is null ? "null" : J(field.TypeName))
              .Append(", \"sqlColumn\": ").Append(J(c.Name))
              .Append(", \"sqlType\": ").Append(J(SqlTypes.Name(c.XType) + Len(c)))
              .Append(", \"extension\": ").Append(ext is null
                  ? $"{{\"app\": {J(extApp)}}}"
                  : $"{{\"name\": {J(ext.Name)}, \"app\": {J(ext.AppId)}, \"appName\": {J(ext.AppName)}}}")
              .Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>Compare decoded rows against a fixture exported from a restored SQL Server (the oracle). Order-insensitive.</summary>
    static int Verify(IBcSource src, Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("fixture", out var fixPath)) throw new ArgumentException("--fixture is required");
        // Fixture lines may end with "|#" (a sentinel the oracle export appends so that
        // trailing spaces inside the last real column survive); strip it before comparing.
        var expected = File.ReadAllLines(fixPath).Where(l => l.Length > 0)
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var (_, _, rows) in ReadCore(src, opts))
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
