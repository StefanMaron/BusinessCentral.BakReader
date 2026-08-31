# BusinessCentral.BakReader

`bcbak` reads Business Central data straight out of a SQL Server native backup
(`.bak`) — no SQL Server, no restore, no service tier. It parses the backup
container, maps every database page, walks the system catalog, and decodes table
rows (including page-compressed data and off-page BLOBs) directly from the file.

Any Business Central database backup is the target: the format work — pages, the
system catalog, row/page compression, LOB storage — is the same in a customer's
production backup as in Microsoft's demo databases. What has actually been
*verified* is narrower; see "What has been verified" below and read it before
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

```
bcbak tables   <file.bak>                          list tables with row counts, compression, company
bcbak companies <file.bak>                         list the companies in the database
bcbak read     <file.bak> --table <name> [options] decode rows to pipe-separated text or JSON
bcbak describe <file.bak> --table <name> --symbols <apps>   AL schema: field ids, AL types, SQL columns
bcbak serve    <file.bak> [--symbols <apps>]       open once, answer many requests over stdin/stdout
bcbak check    <file.bak>                          cross-check the page map; prints map statistics
bcbak verify   <file.bak> --fixture <f.tsv> ...    compare decoded rows against a fixture file
```

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
- `--symbols` takes a comma-separated list of `.app` packages or
  `SymbolReference.json` files. The schema is an **input**: pass the apps the
  database was actually built from (the shipped Base Application for demo
  databases; a customer's own extensions for a customer database).
- `--select` takes AL or SQL column names; `--top N` limits rows;
  `--sha256 "Col"` replaces a binary column by the SHA-256 of its bytes.

### Serve mode — many reads over one open backup

A one-shot `bcbak read` pays the full open cost (page map, catalog, column
metadata, .NET startup) per invocation — about a second per call. A program
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

Commands: `read` (options `table`, `company`, `top`, `select`, `sha256`),
`tables`, `companies`, `describe` (needs `--symbols` at startup), `quit`.
The `id` is echoed back verbatim; a failed request answers `"ok": false`
with the error message and the session stays up. Value formatting matches
`read --format json`. Measured on the demo backup (NVMe): a few ms per
small-table read once the session is warm, and the open cost is paid once.

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
  row-compressed and page-compressed storage, LOB trees up to 180 KB, row
  overflow, and SCSU text (Cyrillic, Greek, CJK, emoji).

No production customer backup has been tested. The code is written for the
general case and everything outside the verified envelope **fails loudly**
rather than returning partial or guessed data — but treat the first run against
any new class of backup as an experiment, and run `bcbak check` on it (it
cross-checks the structural page map against page self-identification and
reports any disagreement).

## Supported today

- SQL Server native full backups, uncompressed and unencrypted, single data
  file, any number of GAM intervals (databases beyond 4 GB), mixed-extent
  allocations.
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
  column and type.
- varchar/char/text bytes ≥ 0x80 decode as Latin-1; a customer database with a
  different single-byte collation could map 0x80–0x9F differently.

## Verification

`./verify.sh` is the full gate: it re-decodes 20 fixture tables from the real
backups (the two demo backups from the BC artifact cache plus the committed
type-probe backup) and compares byte-for-byte with fixtures exported from a real
SQL Server `RESTORE` of the same files. It **fails** when the demo backups are
absent; it never skips silently.

`dotnet test` runs the hermetic suite (unit tests plus the full pipeline against
`fixtures/typeprobe.bak`) everywhere, including CI. Tests that need the ~900 MB
demo backups report as **skipped** when the files are absent — and CI asserts
they were skipped, so a green run cannot mean "tested nothing".

## License

MIT — see `LICENSE`. Independent implementation; no GPL-licensed prior work was
read or used (see `PROVENANCE.md`).
