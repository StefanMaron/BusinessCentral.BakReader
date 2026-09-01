---
name: perf-check
description: How to measure bcdb performance credibly (cold vs warm, page-cache eviction, residency, serve latency) and the 2026-08-31 baseline numbers to compare against. Use before/after any change that could affect open cost, IO volume, or per-read latency, or when someone reports the reader "got slow".
---

# Performance measurement

## Ground rules

- The consumer's requirement: single lazy table reads well under 100 ms, no caching
  mechanisms in the reader. Serve mode (one open handle) is the intended integration;
  do not optimize for repeated reads of the same data.
- Warm, everything here is CPU-bound. Cold, a .bak open is *latency*-bound: the pointer
  chasing walks issue thousands of 8 KB reads one at a time. Measure bytes and request
  count, not just time — on slow media both matter more than they do here.
- "No caching" means no cached decoded data and no optimising for repeated reads of the
  same thing. Warming the OS page cache is not caching: `PageFile.Prefetch` reads pages
  and discards them, and remembers only which allocation units it has already warmed.

## Mechanics

- Evict a file from the page cache (no root needed; works because the bak is read-only):
  `dd if=<file> iflag=nocache count=0 status=none`
- Verify the cold state / measure residency after an operation:
  `fincore -b <file>` (0 B = fully cold; residency after an op = how much the op read,
  including kernel readahead).
- Wall-time a command: `s=$(date +%s%N); <cmd>; e=$(date +%s%N); echo $(( (e-s)/1000000 ))ms`.
- Phase breakdown and per-table timings: scratch harness against BcDb.Core.dll (see
  derive-structural-fact §0) timing PageFile ctor / Catalog ctor / table enumeration /
  LoadColumnMetadata / per-table read separately.
- Serve latency: drive `bcdb serve` from a script (Popen, write one JSON request line,
  time until the response line) — measures the real integration path including process
  spawn for the first answer.
- Repeat cold runs ≥3×; check `fincore` before each to catch another process warming
  the file mid-measurement (verify.sh touches the demo backups).

## Baseline (2026-08-31, 28.1 demo backup 936 MB, NVMe 990 PRO, commit 66e3bdc)

**Always say which build a number came from.** `dotnet build` produces the JIT build the
tests and verify.sh use; `dotnet publish -r linux-x64` produces the native one that ships.
The gap between them is most of a one-shot command's wall clock, so mixing the two
silently invents or hides regressions.

| Metric | JIT build | native (AOT) |
|---|---|---|
| Runtime startup floor (`bcdb` with no args) | 29 ms | **4 ms** |
| One-shot `read` (10 rows of G/L Account), warm | 146 ms | **27 ms** |
| One-shot `read`, cold | 174 ms | **57 ms** |
| `bcdb tables` (3,955 tables), warm | 127 ms | 29 ms |
| `bcdb check` (full-file scan), warm | 289 ms | 180 ms |
| serve: spawn → first answered read | — | **25.5 ms** |
| serve: steady-state read | — | 1.4 ms (0.7–16) |
| serve: spawn → `tables` | — | 29.6 ms |
| Resident after a cold one-shot `read` | — | **50 MB** |
| Sequential read of the whole file, cold / warm | 0.38 s / 0.03 s | |

In process, steady state (a loop of 15, so no JIT and no process start):

| Phase | Time | Allocation |
|---|---|---|
| `PageFile` ctor | 6.0 ms | 13 MB |
| + `Catalog` ctor | 22.6 ms | 37 MB |
| Full open (open + enumerate + preload) | **25.0 ms** | **38 MB** |
| Open + read one 283-row table end to end | 28.2 ms | 38 MB |
| Read every row of all 3,955 tables (406,250 rows) | ~3.0 s | ~9.7 GB |

.bacpac (52 MB production export, 3,914 tables, 178,189 rows), native build:

| Metric | Value |
|---|---|
| model.xml parse, steady state | 611 ms |
| One-shot `read` | ~694 ms |
| serve: spawn → first answer (the parse lands here) | ~680 ms |
| serve: steady-state read | ~1 ms |
| Decode every value of every row after open | 1.11 s |

Regression = open cost or residency growing by more than ~20%, or serve steady-state
reads leaving single-digit milliseconds.

### What the previous baseline got wrong

The 2026-08-31 table recorded before this work claimed a ~500 ms ".NET startup+JIT"
floor and a ~0.96 s warm one-shot read. Both are wrong by 4–14x: the floor is 33 ms on
the JIT build. The tell that this was misattribution rather than a faster machine is
that `check` measured 357 ms against a recorded ~0.4 s — an entry that *did* match, on
the same runs. If a number here looks impossible, re-measure a second entry before
believing a machine difference.

## The .bacpac model.xml pass

The open is dominated by streaming model.xml, and it is pure CPU: cold and warm are
identical, because the 52 MB zip inflates to 107 MB that is then walked in memory.
Measured in isolation on the production export:

| Step (cumulative) | Cost |
|---|---|
| Inflate model.xml alone | 60 ms |
| + bare `XmlReader` walk of all 2.97 M nodes | ~320 ms |
| + `XElement.Load` of the 7,816 kept subtrees | ~223 ms |
| + everything `ParseTable`/`ParseKey` do | ~37 ms |

Walking every element name and all 1.64 M attributes through the reader while building
no DOM at all costs 280 ms and allocates 4.7 MB, against 611 ms and ~337 MB for the real
parse. So the one remaining lever is replacing the per-table DOM with a hand-written
`XmlReader` state machine, worth roughly 2x on a bacpac open. Not taken: the cost is paid
once per serve session and the requirement is about per-read latency.

Two things already tried and not worth repeating: caching the `XName` objects made no
measurable difference, and rewriting `ParseTable`'s LINQ navigation as plain loops was
worth 4% (643.6 → 620.6 ms) — the parsing was never where the time was.

`bcdb tables` on a bacpac counts rows by reading every data stream, so it is the one
command that touches the whole file; `read` and `describe` touch only their table.

## Known cost structure (where time goes)

### A cold .bak open is about access pattern, not volume

The catalog is 5,275 pages (~42 MB) over six base tables — sysallocunits 140, sysrowsets
116, sysschobjs 2,136, syscolpars 1,672, sysiscols 243, sysrscols 968. A single-table read
used to touch all of it. syscolpars, sysiscols and sysrscols are now reached by descending
their clustered index (`ClusteredSeek.cs`), which leaves the ctor's three — sysallocunits,
sysrowsets and sysschobjs, ~2,390 pages — as what a single-table read still scans. That is
what took a cold read from 87 MB resident to 50 MB.

Those pages are reached by pointer chasing (the catalog chain walk, the IAM walk), which
learns the next page id only from the page it just read: never more than one read in
flight, and the order jumps (measured: 11.4% sequential follow-ons, mean run length 1.1,
2,630 backward jumps). Replaying the exact 5,205 page reads a cold one-shot read issues:

| How the same reads are issued | Cold |
|---|---|
| chain order, one in flight (what the walks do unaided) | 310 ms |
| the same reads merely sorted | 102 ms |
| chain order, 16 in flight | 67 ms |
| sorted, 32 in flight | **39 ms** |

Warm, all of them are 8–11 ms. `PageFile.Prefetch` therefore warms an allocation unit's
pages from its IAM chain up front — sorted, coalesced into extent runs, 32 in flight,
once per unit per open. Cold minus warm on a one-shot read is now ~37 ms.

### Warm, the process costs more than the work

A one-shot JIT-build read is 227 ms cold against 189 ms warm, on a 33 ms startup floor
and ~70 ms of JIT. That is why the CLI publishes native: 4 ms floor, 91 ms cold. In
process, steady state, the open is 64 ms. Per-table read cost beyond the open is decode
CPU, roughly proportional to rows × columns, with LOB-heavy tables dominating the tail.

### Allocation is worth attacking in the open, not in the read

Cutting the open from 153 MB to 69 MB — span-based catalog record parsing, and not
materialising the ~95% of sysschobjs rows that are not user tables — halved the
steady-state open (109 → 52 ms at the time). That paid because the allocation was a
proxy for work actually being done: name decodes, dictionary inserts, record copies.

The read path allocates far more and it is *not* worth chasing: 9.7 GB to read all
406,250 rows, but GC pause is only 139 ms of 3.06 s (5%), and 43 ms of 538 ms (8%) on a
LOB-heavy 14,140-row table. Those objects are the decoded values themselves, gen0
allocation is a pointer bump, and they die immediately. Check
`GC.GetTotalPauseDuration()` before believing an allocation figure means time.

### What is still on the table

- **sysschobjs is the last big scan** — 2,136 pages, and the only one of the six that a
  single-table read cannot seek, because it is clustered on the object id while a read
  looks a table up by *name*. It carries three nonclustered indexes (idx2 and idx3 are
  ~2,000 pages each, idx4 is 217); seeking one of those would need the nonclustered index
  record layout derived, which is a different shape from the clustered one — the row ends
  with the clustered key rather than a child pointer at the leaf. `bcdb tables`
  legitimately wants every object and would keep scanning.
- **sysallocunits bootstraps itself** (boot page → chain), so its 140 pages are the one
  walk that cannot even be prefetched: ~13 ms of the remaining cold IO. Its clustered key
  is the allocation unit id, but the reader looks it up by owning rowset, so no seek.
- **The bacpac DOM**, above: ~2x on a bacpac open.
- Not worth doing: a per-object `LoadColumnMetadata` promoting itself to a full load
  after the second object. Tried, and it made `--merge-extensions` *slower* (214 →
  248 ms) — the full load materialises all 85,835 syscolpars rows, which costs more than
  the second heap walk it saves.
