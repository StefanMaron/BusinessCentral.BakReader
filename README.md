# BusinessCentral.BakReader

`bcbak` reads Business Central data straight out of the two files a BC database
normally arrives in — a SQL Server native backup (`.bak`) or a cloud export
(`.bacpac`) — with no SQL Server, no restore or import, and no service tier. For a
`.bak` it parses the backup container, maps every database page, walks the system
catalog, and decodes table rows (including page-compressed data and off-page BLOBs)
directly from the file. For a `.bacpac` it reads the zip container, the `model.xml`
schema, and the native bulk-copy data streams. Both paths answer the same commands
and produce the same output, so which file you have only changes the path you pass.

Any Business Central database file is the target: the format work — pages, the
system catalog, row/page compression, LOB storage, BCP row framing — is the same in
a customer's production file as in Microsoft's demo databases. What has actually
been *verified* is narrower; see "What has been verified" below and read it before
relying on the tool.

**This is an independent implementation.** It was written from scratch against
Microsoft's published documentation, Microsoft's own diagnostic tooling
(`DBCC PAGE`), and direct observation of backup files, with every structural fact
validated against a real SQL Server restore of the same bytes. `PROVENANCE.md`
records where each non-obvious structural fact came from. No other MDF- or
backup-reading project's source code was consulted; in particular, no GPL-licensed
code was read or used. The project is MIT.

## Install

```
git clone https://github.com/StefanMaron/BusinessCentral.BakReader
cd BusinessCentral.BakReader
dotnet build BcBak.sln -c Release
alias bcbak=$PWD/src/BcBak.Cli/bin/Release/net8.0/bcbak
```

## Usage

`<file>` is a `.bak` or a `.bacpac`; the file type is detected from its contents.

```
bcbak tables   <file>                          list tables with row counts, compression, company
bcbak companies <file>                         list the companies in the database
bcbak read     <file> --table <name> [options] decode rows to pipe-separated text or JSON
bcbak describe <file> --table <name> --symbols <apps>   AL schema: field ids, AL types, SQL columns
bcbak serve    <file> [--symbols <apps>]       open once, answer many requests over stdin/stdout
bcbak check    <file.bak>                      cross-check the page map; prints map statistics
bcbak verify   <file> --fixture <f.tsv> ...    compare decoded rows against a fixture file
```

`check` and `validate` inspect the page map, which only a `.bak` has; every other
command works on both file types.

Worked examples against the demo backup shipped in every BC sandbox artifact
(`~/.bcartifacts.cache/sandbox/<version>/w1/BusinessCentral-W1.bak`):

```
# five customers, three columns
bcbak read BusinessCentral-W1.bak --table Customer --company CRONUS \
    --select "No.,Name,City" --top 5

# JSON, with AL field names resolved from the shipped Base Application package
bcbak read BusinessCentral-W1.bak --table "G/L Entry" --company CRONUS \
    --select "Entry No.,Posting Date,Amount" --format json \
    --symbols "Extensions/Microsoft_Base Application_28.1.49838.50621.app"

# the AL view of a table: field numbers, AL types, SQL columns
bcbak describe BusinessCentral-W1.bak --table "No. Series" --company CRONUS \
    --symbols "Extensions/Microsoft_Base Application_28.1.49838.50621.app"
```

- `--table` accepts the AL table name (`No. Series`) or the raw SQL object name.
- `--company` selects the company when a table exists in several (BC 28.1 demo
  databases contain `CRONUS International Ltd_` and `My Company`); a prefix is
  enough (`--company CRONUS`).
- `--app` selects the defining app when two installed apps declare the same
  table name in the same company (legal through AL namespaces — the demo
  database ships `Dimension Set Entry` twice); an app-id prefix is enough
  (`--app 437dbf0e`).
- `--symbols` takes a comma-separated list of `.app` packages or
  `SymbolReference.json` files. The schema is an **input**: pass the apps the
  database was actually built from (the shipped Base Application for demo
  databases; a customer's own extensions for a customer database).
- `--select` takes AL or SQL column names; `--top N` limits rows;
  `--sha256 "Col"` replaces a binary column by the SHA-256 of its bytes.
- `--merge-extensions` joins the base table with its `$ext` companion table on
  the clustered key and returns one row per AL record. Extension fields resolve
  to AL names and field ids through the extending app's symbols (pass the
  extending apps in `--symbols`); a base row without a companion row reads its
  extension fields as NULL. `describe` lists extension fields either way.

### Reading a cloud export

A BC online environment exports as a `.bacpac`. Everything above works the same way
against one:

```
bcbak tables   MyEnvironment.bacpac
bcbak read     MyEnvironment.bacpac --table "G/L Entry" \
    --company "My Company" --select "Entry No.,Posting Date,Amount" --top 5
```

Two differences are worth knowing:

- The compression column reads `-`. A bacpac stores logical rows, so there is no
  storage compression to report.
- Row counts are counted, not read from a catalog, so `bcbak tables` on a large
  export reads the whole file. `read` and `describe` only touch the table asked for.

### Serve mode — many reads over one open file

A one-shot `bcbak read` pays the full open cost (page map or `model.xml` parse,
catalog, column metadata, .NET startup) per invocation — about a second per call. A program
that reads tables one at a time as it needs them should use `serve` instead:
the backup is opened once, then each stdin line is one JSON request and each
stdout line one JSON response, in order:

```
bcbak serve BusinessCentral-W1.bak
> {"id": 1, "cmd": "read", "table": "CRONUS International Ltd_$Currency Exchange Rate$437dbf0e-84ff-417a-965d-ed2bb9650972", "select": "Currency Code,Starting Date"}
< {"id": 1, "ok": true, "headers": ["Currency Code", "Starting Date"], "rows": [["AED", "2025-03-01"], ...]}
> {"id": 2, "cmd": "read", "table": "no such table"}
< {"id": 2, "ok": false, "error": "no table matches 'no such table'"}
> {"cmd": "quit"}
```

Commands: `read` (options `table`, `company`, `app`, `top`, `select`, `sha256`, `merge-extensions`),
`tables`, `companies`, `describe` (needs `--symbols` at startup), `quit`.
The `id` is echoed back verbatim; a failed request answers `"ok": false`
with the error message and the session stays up. Value formatting matches
`read --format json`. Measured on the demo backup (NVMe): a few ms per
small-table read once the session is warm, and the open cost is paid once. The
same holds for a bacpac: on a 52 MB production export, the first request pays the
one-time `model.xml` parse (~1.4 s including process start) and later reads take
1–25 ms.

## What has been verified

Every claim below is backed by byte- or value-exact comparison against a real
SQL Server (`RESTORE` of the same file, `SELECT`, `sys.*` catalog views,
`DBCC PAGE`) — the project's oracle. The verified inputs are:

- **Microsoft's shipped demo backups** for BC 27.5 and 28.1 (W1): the structural
  page map reproduces a fresh `RESTORE` byte-for-byte on 109,954 of 109,984 /
  114,091 of 114,120 pages; every remaining difference is allocation bookkeeping
  or SQL Server's own during-backup bookkeeping, none of it BC table data.
  Fixture tables (No. Series, Customer, G/L Account, G/L Entry, Tenant Media
  blobs by SHA-256, both companies) decode identical to `SELECT`.
- **A 5.4 GB two-GAM-interval database** with mixed-extent allocations and
  delete/update history, built for the purpose (`tools/scale.sql`): the map is
  byte-identical to its restore on 644,104 of 644,144 pages, and a 590,770-row
  table spanning both intervals decodes line-for-line equal to `SELECT`.
- **A type-probe database** (`tools/typeprobe.sql`, committed as
  `fixtures/typeprobe.bak`) covering every supported type in uncompressed,
  row-compressed and page-compressed storage, both nullable and NOT NULL, LOB
  trees up to 180 KB, row overflow, and SCSU text (Cyrillic, Greek, CJK, emoji).
- **The same probe database as a `.bacpac`** (`fixtures/typeprobe.bacpac`, exported
  by `sqlpackage`). Both containers are checked against the *same* oracle fixtures,
  so a bacpac read and a backup read of one database must produce identical output;
  17 probe tables are compared through each path on every `dotnet test` and
  `verify.sh` run.

One independent ~23 GB production BC database backup (BC 21 lineage upgraded
to BC 24, SQL Server 2019, heaps, ALTER history, third-party extensions) has
been validated the same way: 3,019,763 of 3,020,080 pages byte-identical to a
fresh restore with every difference accounted for, and six full tables (up to
3.1 M rows) decoding line-for-line equal to `SELECT`; nothing from that file is
committed. One real 52 MB BC cloud export (`.bacpac`, 3,914 tables, 567 with rows)
has been validated the same way: a scan that decodes every value of every row
(178,189 rows, zero failures), plus `sqlpackage /Action:Import` into a SQL Server
and a column-for-column comparison of all 567 populated tables against `SELECT` —
all identical. Nothing from that file is committed either.
Other production files remain untested. The code is written for the
general case and everything outside the verified envelope **fails loudly**
rather than returning partial or guessed data — but treat the first run against
any new class of backup as an experiment, and run `bcbak check` on it (it
cross-checks the structural page map against page self-identification and
reports any disagreement).

## Supported today

- SQL Server native full backups, uncompressed and unencrypted, single data
  file, any number of GAM intervals (databases beyond 4 GB), mixed-extent
  allocations.
- `.bacpac` exports written by `sqlpackage` / DacFx, Data stream version 2.0.0.0.
  The SHA-256 `Origin.xml` records over `model.xml` is verified on read.
- Uncompressed, row-compressed and page-compressed tables (column-prefix
  anchors, page dictionaries, >30-column clusters).
- Types: integers, bit, decimal/numeric (any precision — output is exact to the
  declared scale), datetime, datetime2/date/time (all scales), real/float,
  uniqueidentifier, rowversion, (n)char/(n)varchar (including SCSU-compressed
  Unicode), binary/varbinary, and off-page LOBs: image/text/ntext and
  varbinary(max)/nvarchar(max)/varchar(max), including multi-page LOB trees and
  row-overflow columns.
- Multi-company databases; AL names and types via `SymbolReference.json`.

## Not supported (fails loudly, by design)

- Backups taken `WITH COMPRESSION` or encrypted backups.
- Multi-data-file databases (Business Central uses one data file).
- Differential and log backups; only the full-backup data copy is read.
- The transaction-log region of the backup is **not replayed**. Measured
  consequence on every verified backup: the only pages this can affect are
  allocation bookkeeping and objects SQL Server itself created *while* the
  backup ran — never settled BC table data. A backup taken of a busy database
  would carry more unreplayed log; `bcbak check` prints the log size.
- `money`, `sql_variant`, `xml`, sparse columns: the reader throws, naming the
  column and type. In a `.bacpac` the same applies to `smalldatetime` and
  `datetimeoffset`, whose row framing no observed export exercises; because an
  unknown column width makes the rest of the row unreadable, the whole table is
  refused, not just that column.
- A `.bacpac` whose `Origin.xml` declares a Data stream version other than 2.0.0.0:
  the row framing was derived for 2.0.0.0 only, so the reader throws.
- In a `.bak`, varchar/char/text bytes ≥ 0x80 decode as Latin-1; a customer
  database with a different single-byte collation could map 0x80–0x9F differently.
  This does not apply to a `.bacpac`, where those columns are written as UTF-16.

## Verification

`./verify.sh` is the full gate: it re-decodes the fixture tables from the real
files (the two demo backups from the BC artifact cache, the committed type-probe
backup, and the committed type-probe bacpac) and compares byte-for-byte with
fixtures exported from a real SQL Server holding the same data. It **fails** when the demo backups are
absent; it never skips silently.

`dotnet test` runs the hermetic suite (unit tests plus the full pipeline against
`fixtures/typeprobe.bak`) everywhere, including CI. Tests that need the ~900 MB
demo backups report as **skipped** when the files are absent — and CI asserts
they were skipped, so a green run cannot mean "tested nothing".

## License

MIT — see `LICENSE`. Independent implementation; no GPL-licensed prior work was
read or used (see `PROVENANCE.md`).
