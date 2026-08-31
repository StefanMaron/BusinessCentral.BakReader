---
name: oracle-ops
description: Mechanics of working with the oracle SQL Server container — sqlcmd invocation and its pitfalls, DBCC PAGE dumps, catalog introspection queries, regenerating typeprobe.bak, exporting fixtures, and wiring new verify.sh checks. Use whenever a task needs to query the oracle, add/regenerate probe data, or create fixtures.
---

# Oracle operations

## The container

- Name `bakreader-oracle`; sa password: `PASS=$(docker exec bakreader-oracle printenv MSSQL_SA_PASSWORD)` — never write it into files.
- Databases: `bc275`, `bc281` (restored demo backups), `typeprobe` (probe database), plus any scratch DBs. If the container is gone, CLAUDE.md has the recreation steps.
- sqlcmd lives at `/opt/mssql-tools18/bin/sqlcmd`; always pass `-C` (trust server cert) and `-f 65001` when text with non-ASCII matters.

## sqlcmd pitfalls (each of these cost real time once)

- `-y0` (unlimited variable-width columns) and `-h -1` (no header) are **mutually exclusive**. With `-y0` alone the output has **no header row for CONCAT selects** — do not `tail -n +2` or you eat the first data row. Verify fixture line counts against `SELECT COUNT(*)`.
- Wide fixed-width output truncates names; use `-y 30`..`-y 60` for readable catalog listings.
- Strip trailing whitespace: `| sed 's/[[:space:]]*$//'`, and drop empty/noise lines with `grep -vE '^$'`.
- `$` in table/column names must be escaped as `\$` inside the double-quoted `-Q` string when going through bash.

Standard one-row-per-line export helper:

```bash
E() { docker exec bakreader-oracle /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PASS" -C -f 65001 -y0 -d bc281 \
      -Q "SET NOCOUNT ON; $1" | sed 's/[[:space:]]*$//' | grep -vE '^$'; }
E "SELECT CONCAT(colA,'|',ISNULL(colB,N'NULL')) FROM [Some\$Table]" > fixtures/bc281-some-table.tsv
```

Formatting must match `bcbak`'s `Fmt`: `NULL` literal for nulls, GUIDs via `CONVERT(varchar(36), g)` (uppercase, dashed), binary as `'0x'+CONVERT(varchar(max), CONVERT(varbinary(max), col), 2)`, big blobs as `'sha256:'+CONVERT(varchar(64), HASHBYTES('SHA2_256', CAST(col AS varbinary(max))), 2)` paired with `--sha256` on the bcbak side.

## DBCC PAGE — the format authority

```bash
docker exec bakreader-oracle /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PASS" -C \
  -Q "DBCC TRACEON(3604); DBCC PAGE('bc281',1,<pageid>,3);"
```

Dump style 3 annotates records — for LOB pages it prints lines like `Blob row at: Page (1:265) Slot 1 Length: 84 Type: 8 (NULL)`, which names record types and sizes authoritatively. The hex groups display as little-endian 4-byte words (bytes `02 45 7D 5B` render as `5b7d4502`).

## Catalog introspection queries that answered real questions

- Physical leaf layout (what sysrscols means): `sys.system_internals_partition_columns` joined to `sys.partitions` (`index_id = 1`) and LEFT JOIN `sys.columns` — columns with no `sys.columns` match are internal; `partition_column_id` carries flag bits (0x08000002 = change tracking's in-row version column). Column is `max_length`, not `max_inline_length`.
- Compression per rowset: `sys.partitions.data_compression_desc`.
- Change tracking: `sys.change_tracking_tables` joined to `sys.tables`.

## Regenerating typeprobe.bak and fixtures

1. Edit `tools/typeprobe.sql`. Constraints: the `BACKUP DATABASE` must stay the **last** statement, and the `probe_ghost` DELETE must stay in the same batch as the BACKUP (ghost records must not be cleaned up before the backup runs). Add new probe sections before the ghost section.
2. `tools/make-typeprobe.sh bakreader-oracle "$PASS"` — rebuilds the DB and copies `fixtures/typeprobe.bak`.
3. Add an export line to `tools/export-fixtures.sh` for any new probe table, then run `tools/export-fixtures.sh bakreader-oracle "$PASS"`.
4. Expect drift in `typeprobe-probe*.tsv` GUID columns (`NEWID()` values regenerate): the bak and fixtures move together, commit both. Never hand-edit a fixture and never regenerate one from bcbak's own output.
5. Add the hermetic test (TypeprobeEndToEndTests / ServeTests) and, when the fact also manifests in the demo backups, a permanent `verify.sh` line with a bc281 fixture.

## verify.sh tiers

- `"$TP"` lines: hermetic, run everywhere the repo is checked out.
- `"$BAK281"` / `"$BAK275"` lines: need the demo backups from `~/.bcartifacts.cache`; verify.sh FAILS (exit 3) without them — that's intended on dev machines.
- CI runs only `dotnet test` and **asserts a non-zero skip count** (the demo-backup SkippableFacts must skip in CI). Never remove or convert those tests.
