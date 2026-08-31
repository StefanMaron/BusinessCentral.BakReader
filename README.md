# BusinessCentral.BakReader

A purpose-built reader for the Business Central **CRONUS demo database backup files**
(`BusinessCentral-W1.bak`, shipped inside every BC artifact). It reads SQL Server pages,
walks the system catalog, and decodes table rows — including PAGE-compressed data —
directly from the `.bak`, with no SQL Server, no restore, and no service tier.

**This is an independent implementation.** It was written from scratch against
Microsoft's published documentation, Microsoft's own diagnostic tooling (`DBCC PAGE`
output from a restored copy of the same files), and direct observation of the backup
files, with every structural fact validated against a real SQL Server restore of the
same backups. `PROVENANCE.md` records where each non-obvious structural fact came from.
No other MDF-reading project's source code was consulted.

## Status: scouting prototype

Built as a timeboxed feasibility scout. What works today (each claim backed by
byte-exact comparison against SQL Server `SELECT` output on BC 27.5 and 28.1 — see
`verify.sh`):

- Locating all page images inside the `.bak` (MTF) container, including resolving
  duplicate page images the way `RESTORE` does.
- Walking the system catalog (`sysallocunits`, `sysrowsets`, `sysschobjs`,
  `syscolpars`, `sysiscols`) — validated row-for-row against `sys.*` views.
- Listing all BC tables with row counts, company, and compression mode.
- Enumerating a table's data pages via its IAM chain (the only reliable way in these
  files — page metadata is stale after the shrink Microsoft runs when building them).
- Decoding uncompressed (FixedVar) records.
- Decoding row-compressed and **page-compressed (CD) records**: column-prefix anchors,
  page dictionaries, cluster arrays for >30-column tables, unicode compression,
  biased variable-length integers.

Not implemented yet (the tool throws or emits explicit `raw[...]:0x...` values, never a
guess): non-zero `decimal` values, `datetime`/`datetime2`/`date`/`time`, off-page LOBs
(BLOB columns), non-Latin unicode-compressed text, multi-GAM-interval databases,
mixed-extent allocations. Also missing: the AL "meaning layer" mapping SQL columns to
AL field numbers/types via the Base Application's `SymbolReference.json`.

## Usage

```
bcbak tables <file.bak>
bcbak read   <file.bak> --table "No. Series" --top 10 --select "Code,Description"
bcbak read   <file.bak> --table "Customer" --company CRONUS --format json
bcbak verify <file.bak> --fixture fixtures/bc275-customer.tsv --table Customer --select "No.,Name,City,Post Code"
```

`--table` accepts the AL table name (`No. Series`) or the raw SQL object name. BC 28.1
demo databases contain two companies (`CRONUS International Ltd_` and `My Company`);
use `--company` to disambiguate.

### Planned filter surface (not implemented)

The end goal is a small OData-flavored query surface:

```
bcbak read <file.bak> --table "Customer" --filter "City eq 'London' and Balance gt 1000" --format json
```

## Verification

`./verify.sh` re-decodes three tables per BC version straight from the 900 MB backups
and compares byte-for-byte with fixtures under `fixtures/`, which were exported once
from a real `RESTORE DATABASE` of the same files. The suite **fails** when the backup
files are absent; it never skips silently. SQL Server is not needed to run it — only
to regenerate fixtures.

## License

MIT — see `LICENSE`.
