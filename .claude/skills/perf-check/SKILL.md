---
name: perf-check
description: How to measure bcbak performance credibly (cold vs warm, page-cache eviction, residency, serve latency) and the 2026-08-31 baseline numbers to compare against. Use before/after any change that could affect open cost, IO volume, or per-read latency, or when someone reports the reader "got slow".
---

# Performance measurement

## Ground rules

- The consumer's requirement: single lazy table reads well under 100 ms, no caching
  mechanisms in the reader. Serve mode (one open handle) is the intended integration;
  do not optimize for repeated reads of the same data.
- Everything here is CPU-bound on NVMe; cold-vs-warm deltas are small locally but the
  IO *volume* matters for slow media — measure bytes, not just time.

## Mechanics

- Evict a file from the page cache (no root needed; works because the bak is read-only):
  `dd if=<file> iflag=nocache count=0 status=none`
- Verify the cold state / measure residency after an operation:
  `fincore -b <file>` (0 B = fully cold; residency after an op = how much the op read,
  including kernel readahead).
- Wall-time a command: `s=$(date +%s%N); <cmd>; e=$(date +%s%N); echo $(( (e-s)/1000000 ))ms`.
- Phase breakdown and per-table timings: scratch harness against BcBak.Core.dll (see
  derive-structural-fact §0) timing PageFile ctor / Catalog ctor / table enumeration /
  LoadColumnMetadata / per-table read separately.
- Serve latency: drive `bcbak serve` from a script (Popen, write one JSON request line,
  time until the response line) — measures the real integration path including process
  spawn for the first answer.
- Repeat cold runs ≥3×; check `fincore` before each to catch another process warming
  the file mid-measurement (verify.sh touches the demo backups).

## Baseline (2026-08-31, 28.1 demo backup 893 MB, NVMe 990 PRO, commit 61f2f34)

| Metric | Value |
|---|---|
| Sequential read of whole file, cold / warm | 0.38 s / 0.03 s |
| Warm fixed open (PageFile+Catalog+enumerate+full colmeta) | ~480 ms |
| Single-table read in process, end to end (89-row table) | ~400 ms |
| One-shot `bcbak read`, warm / cold (includes ~500 ms .NET startup+JIT) | ~0.96 s / ~0.6 s* |
| serve: spawn → first answer, warm / cold | ~0.6 s / ~0.7 s |
| serve: small-table read, steady state | 2–5 ms |
| serve: first touch of a mid-size table (500–1400 rows) | 10–90 ms |
| Full read of all 3,955 tables, warm / cold | ~2.6–5 s / +0.35 s |
| `bcbak check` (full-file scan), warm | ~0.4 s |
| Resident bytes after a cold open | ~64 MB |

*cold one-shot is faster than warm here only because the runs differed in phase mix;
treat ~0.6–1.0 s as the one-shot band. Regression = open cost or residency growing by
more than ~20%, or serve steady-state reads leaving single-digit milliseconds.

## Baseline for .bacpac (2026-08-31, 52 MB production export, 3,914 tables, 567 with rows)

| Metric | Value |
|---|---|
| Open + full model.xml parse (107 MB uncompressed, in process, incl. JIT) | ~1.3-1.5 s |
| Decode every value of every row (178,189 rows) after open | ~1.5 s |
| serve: spawn → first answer (the model parse lands here) | ~1.4 s |
| serve: small-table read, steady state | 1.5-5 ms |
| serve: 1,011-row table, first touch / repeat | ~21 ms / ~12 ms |

The open is dominated by the model.xml pass, broken down as: inflate 44 ms,
inflate + SHA-256 110 ms, a bare `XmlReader` walk of all 2.97 M nodes ~290-420 ms, the
same walk with `XElement` subtrees ~490-620 ms; the rest is element processing. Caching
the `XName` objects made no measurable difference. A hand-written `XmlReader` state
machine in place of the per-table DOM is the remaining option, not taken — the cost is
paid once per serve session and the requirement is about per-read latency.

`bcbak tables` on a bacpac counts rows by reading every data stream, so it is the one
command that touches the whole file; `read` and `describe` touch only their table.

## Known cost structure (where time goes)

Catalog ctor ~200 ms (sysallocunits/sysrowsets/sysschobjs walk), column-metadata page
walk ~130–190 ms (heaps — the walk is unavoidable, only materialization is filtered),
sysrscols layout ~55 ms, PageFile open ~35 ms. Per-table read cost is decode CPU,
roughly proportional to rows × columns; LOB-heavy tables dominate the tail. The .NET
startup+JIT floor (~500 ms) can only be attacked with ReadyToRun/AOT publishing —
tracked as a possibility, not done.
