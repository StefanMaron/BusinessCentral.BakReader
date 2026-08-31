---
name: derive-structural-fact
description: The end-to-end loop for diagnosing a decode failure or deriving a new on-disk format fact — narrow reproduction, byte-level diagnosis with a scratch harness, oracle interrogation, known-value probe design, RED test, fix with loud-failure guards, oracle validation, PROVENANCE entry. Use whenever a table fails to decode, a new format surface needs support, or output disagrees with the oracle.
---

# Deriving a structural fact / fixing a decode failure

The clean-room and oracle rules (`.claude/rules/`) say what is allowed; this is the
working loop that has fixed every failure so far. Load `oracle-ops` alongside — every
step here leans on it.

## 0. Inventory first, one failure at a time second

A full-database scan is the cheapest way to see the blast pattern (how many tables, which
error classes). Build a scratch harness OUTSIDE the repo referencing the built core —
never add diagnostic code to the repo itself:

```
scratch/bench.csproj:  <Reference Include="BcBak.Core"><HintPath>…/src/BcBak.Core/bin/Release/net8.0/BcBak.Core.dll</HintPath></Reference>
```

Loop `cat.Objects` (Type "U") → `cat.RowsetFor(id, 1, 0)` → `TableReader.ReadRows` +
`SqlTypes.Decode` per cell, try/catch per table, print `table|error`. Group errors by
message shape. Copy the DLL after every rebuild — a stale copy shows fixed bugs as live.

## 1. Reproduce narrowly

`bcbak read <bak> --table '<exact SQL object name>' --select <one column> --top 1`.
Note whether the failure names its context (table/column/page). A bare framework
exception (ArgumentOutOfRange etc.) is itself a second bug per the loud-failures rule:
the fix must add the named guard as well as the decode.

## 2. Look at the actual bytes

Extend the scratch harness to dump what the reader computed vs. what is on disk:
`Catalog.RowsetColumns(rowsetId)` for physical layout questions, raw record hex from
`PageFile.GetPage` + `PageHeader.SlotOffsets` for record questions, chain walks for LOB
questions. Two diagnostics that found real bugs:
- print every PhysColumn (colid, nullbit, xtype, maxlen, leafoff) and look for
  duplicates/collisions or widths that disagree with `sys.system_internals_partition_columns`;
- for LOB failures, walk the pointer chain printing per-record `statusA, recLen, blobId,
  type` + the next 32 bytes of payload.

### For a .bacpac
The equivalent of DBCC PAGE is a **re-export with a changed value, diffed byte by byte**:
there is no annotator, so a hypothesis about a field boundary is settled by changing one
value in the probe database, exporting again, and seeing which bytes move. The
equivalent of RESTORE is `sqlpackage /Action:Import` followed by full-table `SELECT`
(oracle-ops has the commands). A parse that merely *succeeds* proves little — the row
framing is self-checking enough that a wrong column order still parses; only the
value-level comparison rules that out.

## 3. Ask the oracle what SQL Server thinks

`DBCC PAGE(...,3)` annotations name record types and sizes; `sys.system_internals_*`
views expose the engine's own interpretation; a plain `SELECT` states the expected
values. If the oracle's annotation contradicts the reader's parse, the annotation wins
and tells you the field boundaries (see oracle-ops for the byte-order of DBCC hex dumps).

## 4. Find the trigger, then reproduce it with KNOWN values

The failing shape always has a cause a probe can recreate. Triggers proven so far, all
now in `tools/typeprobe.sql`: ALTER history (drop/add columns with existing rows),
heaps with delete churn, ghost records under page compression, wide tables (>128
columns), change tracking (internal in-row column), UPDATE of legacy text/image values
(rewritten SMALL_ROOT; UPDATE-to-NULL leaves a type-8 NULL root), `$`-prefixed
platform-style names, two apps sharing a table name, base + `$ext` companion tables.
Design the probe so every stored value is chosen, including edge values the hypothesis
predicts behave differently — unknown-value reverse engineering invites confirmation bias.

## 5. RED before GREEN

Regenerate the probe + fixtures (oracle-ops), then add the hermetic test asserting the
oracle-known values and watch it fail with the same error as production. If it fails
differently, the probe does not reproduce the trigger — fix the probe first.

## 6. Fix, guarding everything the derivation does not explain

Decode exactly what was derived; everything else throws naming table/column/page and
what was refused. A guard firing later is a derivation opportunity, not something to
relax. Never map two things onto one dictionary key silently (the change-tracking bug
was an internal column *shadowing* a user column — wrong values, no error, on any table
where the collision type happened to fit).

## 7. Validate on real data, permanently

Re-run the full-database scan (failure count must only shrink), then add a value-level
fixture for a real demo-backup table that exercised the fact and wire it into verify.sh.
Row counts are a smoke check; the fixture comparison is the proof. Run `./verify.sh`
before every commit.

## 8. Record and commit

- PROVENANCE.md entry in the existing style: the fact, where it was observed, what
  reproduces it hermetically, what validates it (file names of fixtures count as
  evidence).
- Commit message: what the fact is, why the old behavior was wrong, how it was
  reproduced and validated, `Closes #N`. Keep the bak+fixtures regeneration in the same
  commit as the probe change that caused it.
