# CLAUDE.md

`bcbak` reads Business Central data directly out of SQL Server native backups
(`.bak`) and BC cloud exports (`.bacpac`) — no SQL Server at runtime, no restore
or import, no service tier. For a `.bak` it parses the MTF container, derives the
page map from the allocation bitmaps, walks the system catalog, and decodes rows
including page compression and off-page LOBs. For a `.bacpac` it reads the zip
container, streams `model.xml` for the schema, and decodes native-BCP data
streams. Both land in one query surface (`IBcSource`).
`README.md` has the user-facing scope; `PROVENANCE.md` records where every
non-obvious structural fact came from and how it was validated.

## Map

| Path | What |
|---|---|
| `src/BcBak.Core/Mtf.cs` | MTF container walk: descriptor blocks, streams, MQDA/MQTL extraction |
| `src/BcBak.Core/PageFile.cs` | the structural page map (GAM/SGAM/DCM/PFS driven), cross-check |
| `src/BcBak.Core/Catalog.cs` | system catalog base tables (sysallocunits, sysschobjs, …) |
| `src/BcBak.Core/TableReader.cs` | IAM chains, per-page PFS filtering, row enumeration |
| `src/BcBak.Core/Records.cs` | FixedVar and compressed (CD) record parsing, CI structure |
| `src/BcBak.Core/Values.cs` | type decoding (vardecimal, datetime family, SCSU dispatch, …) |
| `src/BcBak.Core/Scsu.cs` | full SCSU decoder (UTS #6) |
| `src/BcBak.Core/Lob.cs` | off-row value resolution: text pointers, inline roots, LOB trees |
| `src/BcBak.Core/Symbols.cs` | AL meaning layer: SymbolReference.json from `.app` packages |
| `src/BcBak.Core/Source.cs` | `IBcSource`: the tables/columns/rows contract both containers implement |
| `src/BcBak.Core/Bacpac.cs` | the `.bacpac` zip container, Origin.xml guards, streaming model.xml |
| `src/BcBak.Core/Bcp.cs` | native-BCP row framing: prefix widths, per-type storage-form conversion |
| `src/BcBak.Cli/Program.cs` | the `bcbak` CLI (tables / read / describe / check / verify) |
| `tests/BcBak.Tests/` | hermetic unit + end-to-end tests (see rules on skips) |
| `fixtures/` | oracle-exported expected values + the committed `typeprobe.bak` |
| `tools/` | scripts that regenerate the probe databases and fixtures on the oracle |
| `fixtures/typeprobe.bacpac` | sqlpackage export of the same probe database state as `typeprobe.bak` |
| `verify.sh` | the full-file gate against the real demo backups (fails when absent) |

## The oracle

All verification compares against a real SQL Server — container
`bakreader-oracle` (`mcr.microsoft.com/mssql/server:2022-latest`, sa password in
the container's environment), holding restored copies of the BC 27.5/28.1 demo
backups (`bc275`, `bc281`) and the probe databases. If it is gone, recreate it by
restoring the demo backups from `~/.bcartifacts.cache/sandbox/<v>/w1/` and
running `tools/typeprobe.sql` / `tools/scale.sql`. Fixtures under `fixtures/`
were exported from it with `tools/export-fixtures.sh`.

For `.bacpac` work the oracle plays the same role through `sqlpackage`
(`dotnet tool install -g microsoft.sqlpackage`; the container publishes SQL on host
port **14330**): `/Action:Export` produces a bacpac from a probe database,
`/Action:Import` loads any bacpac back for a `SELECT` comparison.

## Operating rules

Auto-loaded from `.claude/rules/`. The short version:

- **Clean room**: never read, fetch, or consult any other MDF/backup-reading
  project's code — especially not GPL-licensed code. Derive from Microsoft's
  docs, DBCC PAGE, the files themselves, and the oracle. (`clean-room.md`)
- **Derivations go in `PROVENANCE.md`** with their evidence, in the existing
  style — that file is a deliverable. (`oracle-verification.md`)
- **Loud failures**: anything the reader cannot decode faithfully throws,
  naming the column/type/surface. Never a default value. (`loud-failures.md`)
- **Verify against the oracle, never against yourself.** (`oracle-verification.md`)
- **Never trust catalog metadata at face value** — first_page/root_page/m_objId
  are measurably stale in real files. (`stale-metadata.md`)
- **TDD**, tests that assert real values. (`tdd.md`)
- **No silent skips in CI.** (`no-silent-skips.md`)

## Skills

Project skills under `.claude/skills/` carry the working mechanics distilled from real
sessions — load them instead of rediscovering:

- **derive-structural-fact** — the loop for any decode failure or new format surface
  (diagnose → oracle → known-value probe → RED → fix with guards → validate → PROVENANCE).
- **oracle-ops** — sqlcmd pitfalls, DBCC PAGE, typeprobe/fixture regeneration, verify.sh wiring.
- **perf-check** — credible cold/warm measurement and the current baseline numbers.

## Everyday commands

```
dotnet build BcBak.sln -c Release        # warnings are errors
dotnet test  BcBak.sln -c Release        # hermetic suite (typeprobe.bak + typeprobe.bacpac)
./verify.sh                              # full gate; needs the ~900 MB demo backups
src/BcBak.Cli/bin/Release/net8.0/bcbak check <file.bak>   # page-map self-check on any backup
src/BcBak.Cli/bin/Release/net8.0/bcbak serve <file>       # open once, JSON requests over stdin (the consumer path)
```
