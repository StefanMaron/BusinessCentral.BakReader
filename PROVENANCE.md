# Provenance

This project is an independent, clean-room implementation. This file records, for every
non-obvious structural fact implemented, where it came from. No other MDF/backup-reading
project's source code was consulted at any point.

## Sources used

1. **Direct observation of the target files** (primary source):
   `~/.bcartifacts.cache/sandbox/27.5.46862.48827/w1/BusinessCentral-W1.bak` and
   `~/.bcartifacts.cache/sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak`
   (Microsoft-published BC sandbox artifacts, world 1).
2. **A real SQL Server as oracle**: both backups were restored once into
   `mcr.microsoft.com/mssql/server:2022-latest`. Ground truth was taken from
   `SELECT` output, `sys.*` catalog views, `sys.dm_db_database_page_allocations`,
   and `DBCC PAGE` dumps (Microsoft's own page-annotation tool, styles 1 and 3).
   SQL Server is not used at runtime; it produced fixtures and adjudicated every
   structural hypothesis below.
3. **Microsoft documentation**:
   - *Page compression implementation* —
     <https://learn.microsoft.com/en-us/sql/relational-databases/data-compression/page-compression-implementation>
     (conceptual model: CI structure immediately follows the page header; per-column
     prefix anchors; partial prefix matches; page-wide dictionary applied after prefix
     compression; page compression only engages once a page fills — sparser pages of a
     PAGE-compressed partition are row-compressed only).
   - *Row compression implementation* (same doc set) — integer values stored in the
     fewest bytes possible; conceptual only.
   - *Pages and extents architecture guide* — 8 KB pages, 96-byte header, slot array
     at the end of the page, page type meanings, GAM/SGAM/PFS/IAM roles, extents of 8 pages.
   - *Microsoft Tape Format Specification 1.00a* (historically published by Microsoft)
     — the `.bak` container: `TAPE`/`SSET`/`VOLB`/`MSCI`/`MSDA`/`SPAD` descriptor
     blocks and 22-byte stream headers (`id[4]`, attributes, u64 length, ...). Only the
     `MSDA` header layout was ultimately relied on, and only during exploration.
4. **General public knowledge of SQL Server internals in prose form** (e.g. the widely
   published "anatomy of a page/record" material): the *concepts* of the FixedVar record
   layout (status bits, fixed data, column count, null bitmap, variable offset array)
   and of the boot page holding a pointer to the first `sysallocunits` page. Every
   concrete offset was re-derived from the files and validated as listed below.
5. **For `.bacpac` support: sqlpackage as both producer and oracle.** Microsoft's
   `sqlpackage` (`dotnet tool install -g microsoft.sqlpackage`, version 170.5.76 here)
   exports a database to a `.bacpac` and imports one back. Every fact about the
   container and its data streams was derived from files sqlpackage produced from
   probe databases whose every value was chosen (`tools/typeprobe.sql`), and each was
   checked by importing the same file back into the oracle and comparing full
   tables with `SELECT` — the role `RESTORE` plays for a `.bak`. No bacpac-reading
   project's source, and no DacFx source, was consulted.

## Facts and their validation

Method note: "validated against oracle" below means byte- or value-exact comparison
with the restored SQL Server on **both** BC versions unless stated otherwise.

### Backup container (MTF) — structural layout (`Mtf.cs`)
Derived by walking both demo backups descriptor block by descriptor block; block/stream
framing per the historically published Microsoft Tape Format Specification 1.00a.
- Descriptor block (DBLK) sequence observed in both files:
  `TAPE`, `SFMB`, `SSET`, `VOLB`, `MSCI`, `MSDA` (×2), `MSTL` (×2), `MSLS`, …
  Every DBLK: 4-byte type tag, u32 attributes at +4, u16 offset-to-first-event at +8
  pointing at its stream chain. `SFMB` has no stream chain (its offset points at the
  next block).
- Stream header (22 bytes): id[4], u16 fs-attributes, u16 media-format attributes,
  u64 length, u16 encryption algorithm, u16 compression algorithm, u16 checksum.
  Stream data is 4-byte aligned; an `SPAD` stream pads to the next block boundary and
  terminates the chain. `APAD` streams pad inside MSDA/MSTL blocks so the payload
  stream starts near a 4 KB boundary.
- SQL Server payload streams: `MQCI` (configuration, inside MSCI), `MQDA` (data pages,
  inside MSDA), `MQTL` (transaction log, inside MSTL). Every data-bearing MQDA/MQTL
  stream observed carries 2 lead bytes (always 0x0000, meaning unknown — guarded at
  parse time) followed by an exact multiple of 8192 bytes. A zero-length MQDA stream
  (media-format attributes 0x6 vs 0x2) terminates each MSDA section.

### Data-copy layout — how RESTORE places blocks (`PageFile.cs`)
The load-bearing derivation of this project. Page **self-identification is not how
RESTORE places blocks**; placement is positional, driven by the allocation bitmaps:
- **MQDA region 1** holds, per GAM interval in order, every extent that is
  (a) GAM-allocated (bit clear), (b) contains a PFS page, (c) is the interval's
  first extent, or (d) is SGAM-marked (mixed extent with free pages), in ascending
  extent order, 8 blocks per extent, ending where the copy ends (see "File size is a
  lower bound" below — NOT capped at the header's recorded size). GAM page: 2 slots;
  slot 1 = `[2 status bytes][u16 bitmap length = 0x1F38][bitmap]`, LSB-first, bit = 1
  means the extent is FREE. A GAM interval covers 63,904 extents (511,232 pages);
  the 7,992-byte bitmap has a 32-bit overhang whose bits are not extents.
  - Rule (b) was invisible in the demo databases (their PFS extents are all
    GAM-allocated); measured on a purpose-built 5.4 GB backup (`tools/scale.sql`)
    with mixed-page allocation enabled, where all 63 GAM-free interval-0 extents in
    the stream were exactly the PFS-page extents (1011·k).
  - Rule (d) is a logical necessity (such extents contain live pages) but no test
    file exercises it — every observed SGAM is empty. The block-count identity guard
    catches any file where the rule set is wrong.
  - **Multi-interval files**: interval 0 has its GAM/SGAM at pages 2/3 (pages 0/1 are
    the file header and first PFS); interval k>0 has GAM/SGAM at its first two pages
    (observed: pages 511232/511233, types 8/9), with DCM/BCM at +6/+7. Each interval's
    contribution follows the previous one. Validated on the 5.4 GB two-interval file:
    region 1 = 644,144 predicted data blocks + 80 filler = 644,224 actual, and
    644,104 of 644,144 mapped pages byte-identical to a fresh RESTORE (3 header-only;
    37 body-diffs, all allocation bookkeeping / log-redo system pages, none above
    page 24,267). A 590,770-row table spanning both intervals decoded identical to
    SELECT, line for line.
- **File size** comes from the file header page (1:0): its header data is a FixedVar
  record at page offset 96 whose field count varies with the SQL Server version that
  wrote the file (observed: 56 fields on a SQL 2019-era production file, 60 on
  SQL 2022 files) — "Size" (in pages) is variable-length column 4 of that record,
  validated on five backups with known sizes across versions; the field name comes
  from DBCC PAGE. A fixed page offset is NOT stable across versions. The RESTORE target
  size can be larger (the demo backups record 110,464 / 116,112 pages; the restored files
  are 118,208 / 122,328) — and, as the next entry records, the COPIED DATA can be larger
  too, so this value bounds nothing.
- **MQDA region 2** re-dumps the extents that changed while the backup ran, as
  8-block extent frames in ascending extent order, each interval's section led by its
  lead extents (0 and 1 for interval 0, the first extent for later intervals), whose
  slot 6 carries the interval's FINAL DCM image. Which extents appear is **not
  recorded in any single on-disk structure**: the DCM initial/final diff under-lists
  them (an independent production backup carried four re-read extents whose DCM bit
  was set both before and during the backup — no bit changed), and per-block page
  headers over-trust content (on the 28.1 demo backup a frame carries live pages of
  one extent whose header page-ids point at a *different* page — an internal page
  type whose id field is a reference; RESTORE placed the whole frame positionally,
  proven by byte comparison). The reader therefore chooses each frame's extent by
  consensus of frame-aligned page headers constrained by (a) strictly ascending
  order and (b) membership in the interval's final DCM image or being a lead
  extent; exactly one candidate may survive, anything else fails loudly, and whole
  frames are mapped (RESTORE writes whole frames including slots with no readable
  header). Validated byte-for-byte against fresh RESTOREs of five backups.
  RESTORE applies regions in file order, so region 2 supersedes region 1.
- **1 MB padding**: each MQDA region is padded to a 1 MB boundary with filler
  pseudo-pages (header bytes `01 65`, page id 0). Verified: 27.5 region 1 =
  109,984 extent blocks + 96 filler; region 2 = 320 + 64; 28.1 = 114,120 + 56 and
  56 + 72. RESTORE discards filler (its content appears nowhere in the restored MDF).
- **Validation** (the oracle for all of the above): a fresh `RESTORE DATABASE` of each
  backup, taken OFFLINE immediately and copied out. The structural map reproduces the
  restored MDF **byte-for-byte on 109,954 of 109,984 mapped pages (27.5)** and
  **114,091 of 114,120 (28.1)**; 5 pages per file differ only inside the 96-byte header,
  and the remaining body-diffs are exactly: allocation bookkeeping (file header, PFS,
  GAM, DCM, boot), plus `sysobjvalues`/`sysschobjs` rows for objects SQL Server itself
  created *while the backup ran* (redone from the log by RESTORE, see "Log region").
  No BC table data page differs.

### File size is a lower bound, not the end of the copied data (`PageFile.cs`)
The file header's "Size" column was originally used as a hard bound on the region-1
extent walk (`if (firstPage >= FilePages) break;`). **It is not one.** A backup can carry
allocated extents whose page ids lie beyond the size the file-header page records, and BC's
demo database began doing so at 28.2. The walk then stopped mid-region and `VerifyFillerTail`
refused the real pages nobody had mapped — correctly, but about a truncation the reader had
inflicted on itself:

```
block 116504 of MSDA region is neither mapped by the derived extent list
nor padding filler — backup layout differs from the derived model, refusing to guess
```

Measured on the four W1 demo backups (`bcdb check`, and an independent Python walk of the
MTF + GAM written to reproduce the reader without sharing its code):

| BC | header `Size` | data ends at | `Size` − data end | old walk stopped at | region blocks |
|---|---|---|---|---|---|
| 28.1.49838.54308 | 116,240 | 114,180 | **+2,060** (free tail) | 114,176 | 114,176 |
| 28.2.50931.54319 | 116,304 | 116,720 | **−416** | **116,296** | 116,736 |
| 28.3.52162.54309 | 116,296 | 118,760 | **−2,464** | **116,288** | 118,784 |
| 28.4.53241.54318 | 116,512 | ~119,160 | **−2,648** | **116,504** | 119,168 |

The "old walk stopped at" column reproduces, to the block, the three refusal offsets reported
from the field, which is what establishes that the model above measures the real reader rather
than an approximation of it. **Nothing about the format changed**: the MTF container, the
region shape and the file-header record are identical across all four (`ncols = 60`,
`nvar = 59`, Size at variable column 4, `max_size = -1` at 5, `growth = 8192` at 6). 28.2 is
merely the first version where the demo database's content crossed its file's recorded size,
by 52 extents; on 28.1 Size still sat above the data, so the bound never bit. A latent defect
the data grew into, not a regression.

Two independent confirmations that the copy, not the header, is right:
- The block at 28.4's refusal point carries **page 1:116512** — a page id exactly equal to the
  recorded Size, i.e. the stream continues seamlessly with pages the header says cannot exist.
- The **GAM bitmap has zero allocated extents past the true end of data on every version**.
  GAM and copied data agree with each other perfectly; only Size disagrees with both.

So the walk now ends where the copy ends: at the first proposed extent whose lead block is a
filler pseudo-page (type `0x65`, which is not a SQL page type, so no live page can be mistaken
for it), or when the region runs out. `FilePages` is then `max(header Size, derived end)` —
the header may legitimately claim more, as on 28.1, and that larger figure is kept. This also
repairs two latent failures elsewhere that the same understated value fed: the region-2 frame
filter rejects candidate page ids `>= FilePages`, and the GAM interval count is computed from
it.

Each mapped extent is additionally checked to lead with its own first page, so the map
validates itself instead of trusting GAM order alone. That check holds for **all 58,597
extents of the 28.1, 28.2, 28.3 and 28.4 demo backups**, which is independent evidence that
the structural model was always correct and only its termination was wrong.

**Not determined**: why SQL Server wrote a Size below the data it copied. On all four backups
the shortfall is smaller than the file's own `growth` (8,192 pages), which is consistent with
the header image predating the last autogrowth — but both MQDA regions carry the same value,
and separating "the image is stale" from "the field is not refreshed on autogrow" needs DBCC
against a live service tier. The claim made here is only that the field is unreliable as an
end-of-data, which the table establishes on its own.

### Why self-identification ("last image wins") was wrong
The prototype resolved duplicate page images by scanning for plausible page headers and
letting the last image win. Cross-checked against the structural map and the restored
MDFs: **20 pages on 27.5 and 9 pages on 28.1 got the wrong image** under that rule —
deallocated pages keep stale headers, so a stale image with the same page id can appear
later in the stream than the live page. On 27.5 all affected pages are deallocated
(harmless by luck); on 28.1, pages 94,436–94,439 are **live TEXT_MIX LOB pages of the
BC `Published Application` table** — BLOB reads through the old rule would have returned
corrupt data. The structural map matches the restored MDF on every disputed page.
`bcdb check` recomputes this cross-check for any input file.

### PFS pages — per-page allocation (`PageFile.IsPageAllocated`)
- IAM/GAM bits cover whole extents; single pages inside an extent are deallocated
  individually and keep stale images (observed: the BC 28.1 `Customer` extent holds
  1 live page and 7 stale ones whose images still parse as data pages of the table —
  reading them yields garbage rows).
- PFS layout: page 1:1 covers pages 0..8,087, then one PFS page every 8,088 pages
  (page id = interval base). One record at slot 0: `[2 status bytes][u16 = 0x1F9C]
  [one byte per page]`, data at record+4. Bit 0x40 = page allocated. Derived from the
  files; validated for **every page of both databases** against
  `sys.dm_db_database_page_allocations` (only expected deltas: system/allocation pages
  the DMV does not attribute, and pages the oracle DB touched after being brought
  online).
- IAM bitmap overhang: the 7,992-byte IAM bitmap covers 63,936 bits but an interval is
  63,904 extents; bits 63,920/63,925/63,928/63,933 are set on every IAM page observed
  and are not extents. The reader caps IAM (and GAM) reads at 63,904.

### Log region (MSTL/MQTL) — not replayed, consequences measured
Both demo backups carry 65,536 bytes of MQTL log data. RESTORE redoes it after the data
copy; this reader does not. Measured consequence (bak page images vs fresh restore):
the only affected pages are allocation bookkeeping (PFS/GAM/DCM/boot/file header) and
`sysschobjs`/`sysobjvalues` entries for objects SQL Server created during the backup
(backup bookkeeping; pages 768/40752/109792 on 27.5, 768/113264/114136 on 28.1 — all
`sysschobjs`). No BC table data. A backup taken of an active database would have a
larger log region; `bcdb check` prints the unreplayed log size so that risk is visible.

### Page header offsets (`PageHeader` in `PageFile.cs`)
Offsets 1 (type), 2 (typeFlagBits), 3 (level), 6 (indexId), 16 (nextPage), 22 (slotCnt),
24 (objId), 32 (pageId), 58 (ghostRecCnt): observed in the files and confirmed
field-by-field against `DBCC PAGE` header output for the same pages.
- `m_typeFlagBits & 0x80` marks a page carrying a compression-information structure
  (observed on all CI pages; absent on row-compressed-only pages; consistent with the
  MS page-compression doc's statement that not all pages of a PAGE partition are
  page-compressed).
- **Caveat discovered**: after the shrink Microsoft runs while producing the demo DB,
  `m_objId`/`m_indexId` on relocated pages can belong to a *previous* owner (observed:
  the No. Series data page carries m_objId 15766 while its allocation unit is idObj
  53666; `DBCC PAGE` shows the same stale header). Page-to-object mapping therefore
  never uses header fields; only IAM chains.

### Boot page
- Page (1:9), type 13. A 6-byte page pointer to the first `sysallocunits` page sits at
  page offset **612** (the field known as `dbi_firstSysIndexes` in public prose).
  Derived by searching the boot page for a pointer to the oracle-confirmed first
  sysallocunits page (1:20); offset 612 is the unique match on 27.5 and holds on 28.1.

### System catalog base tables (`Catalog.cs`)
Records are classic FixedVar records. Parser rules (status byte bits 4/5 = null bitmap /
variable columns present; u16 at +2 = offset to column count; trailing variable-column
end-offset array): concepts from public prose; every rule validated by the row-for-row
catalog comparisons below. Record type = status bits 1-3 (0 primary, 5/6/7 ghost);
ghost records occur in these files and are skipped.
- `sysallocunits` (first page from boot pointer, chain via nextPage): fixed-column
  layout `auid i64 @0, type u8 @8, ownerid i64 @9, ... pgfirst 6B @23, pgroot 6B @29,
  pgfirstiam 6B @35`: **12,034/12,034 rows matched** `sys.system_internals_allocation_units`
  (27.5).
- `sysrowsets` (its own partition id is the fixed value 5<<16): `rowsetid i64 @0,
  idmajor i32 @9, idminor i32 @13, rcrows i64 @27, cmprlevel u8 @35`:
  11,553/11,571 exact vs `sys.partitions`; the 18 mismatches are ±1 row-counter drift
  on *system* tables only (restore side effects), all BC tables exact.
- `sysschobjs` (object id 34): `id i32 @0, type char2 @13`, name = first variable
  column (UTF-16): all 70,803 objects exposed by `sys.objects` matched exactly; the
  2,662 extra decoded rows are hidden system objects (negative ids, `sp_MS%`,
  INFORMATION_SCHEMA) that the view filters.
- `syscolpars` (41): `id i32 @0, number i16 @4, colid i32 @6, xtype u8 @10,
  length i16 @15, prec u8 @17, scale u8 @18`, name = first var column. Validated
  against `sys.columns` for Customer (107 columns) and No. Series (types, lengths,
  precision/scale exact).
- `sysiscols` (55): `idmajor i32 @0, idminor i32 @4, subid i32 @8, intprop(=colid) i32 @16`.
  Validated against `sys.index_columns` (clustered keys of No. Series and Customer).

### IAM pages (`TableReader.cs`)
- `first_page`/`root_page` in sysallocunits are **stale** in these files (observed: the
  No. Series `first_page` points at a page now owned by `sysobjvalues`; `DBCC PAGE` and
  `sys.dm_db_database_page_allocations` agree the real data page is elsewhere). The IAM
  chain is authoritative.
- IAM page (type 10): slot 1 record = 2 status bytes, u16 bitmap byte length at +2
  (observed 0x1F38 = 7992 bytes ≈ one GAM interval of 63,936 extents), extent bitmap
  from +4, LSB-first (bit *n* of byte *k* = extent 8k+n relative to the IAM's interval
  base, pages (8k+n)*8 ..+7). Derived from the single set bit for single-extent tables
  (No. Series extent 7695 → byte 961 bit 7 = 0x80; Customer extent 7178 → byte 897
  bit 2 = 0x04); page sets for whole tables matched
  `sys.dm_db_database_page_allocations` exactly.
- IAM slot 0 header: the interval base (`start_pg`) is a 6-byte page pointer at
  slot0+40; eight 6-byte single-page allocation slots (mixed-extent pages) follow at
  slot0+46. Derived from a database with mixed-page allocation enabled (three tables
  allocated from mixed extents, one in the second GAM interval), positions confirmed
  against DBCC PAGE's IAM annotations and by decoding those tables identical to
  SELECT. Multi-interval tables chain one IAM per interval via the page header's
  next-page pointer (validated on a 590,770-row table spanning two intervals).
- Data-page enumeration filters on the PFS allocation bit (see "PFS pages" above);
  a PFS-allocated page of a mapped extent must be present in the structural map, so
  absence throws instead of being skipped.

### Compressed (CD) records (`Records.cs`)
Conceptual model from the MS page-compression doc; all byte layouts below derived from
the files with `DBCC PAGE` styles 1+3 annotations and validated by decoding entire
tables and comparing with `SELECT` (No. Series 118 rows / 119 on 28.1: all columns
except timestamps/datetimes; Customer 5 rows; G/L Account 283 rows — all exact, both versions).
- Record: header byte (bit 0 = CD format, bit 1 = versioning info present, bit 5 = long
  data region present), u8 column count, then a 4-bit CD code per column packed
  low-nibble-first.
- CD codes (names from DBCC PAGE output): 0 NULL, 1 EMPTY (zero-length value: empty
  string / numeric zero / "exactly the anchor" when the column has one), 2..9 =
  (code−1) bytes of short data, 0xA long data region, 0xC one-byte page-dictionary symbol.
- **Physical column order is not declaration order**: clustered-index key columns come
  first (in key order), then remaining columns by column id. Derived from the mismatch
  pattern when assuming declaration order; validated by the full-table comparisons.
  (This also explains why DBCC PAGE's own per-column CD annotations appear shifted
  relative to its value annotations.)
- \>30 columns: columns are grouped in clusters of 30; one length byte per non-final
  cluster precedes the short-data region (observed `1c 22 02` on a 107-column Customer
  record = 28/34/2, exactly the per-cluster sums of the CD short lengths), and one
  count byte per non-final cluster sits between the long-region offset array and the
  long data (observed `07 01 05` = per-cluster long-value counts).
- Long data region: `[u8 header = 0x01][u16 count][count × u16 end-offsets][data]`,
  entries assigned to the 0xA-coded columns in order.
- CI structure at page offset 96: `[u8 header (bit1 anchor present, bit2 dictionary
  present)][u16 pageModCount][u16 end-of-anchor offset][u16 end-of-dict offset]` then
  the anchor record (itself a CD record, parsed without anchors) at +7, then the
  dictionary at CI+endOfAnchor: `[u16 entry count][count × u16 end-offsets relative to
  dictionary start][entries]`. Offsets confirmed against the DBCC CompressionInfo dump
  (anchor @103, size 113, dictionary @216, 22 entries, data section offset 46).
- Prefix (anchor) application: a stored value for an anchored column is
  `[u8 prefix-length][suffix]`; full value = anchor[0..prefixLength] + suffix. An
  EMPTY-coded anchored column equals the anchor exactly. Dictionary entries hold the
  post-prefix representation and go through the same reconstruction. Matches the MS
  doc's "partial match" description; byte layout validated by the GUID/text
  reconstructions matching SELECT exactly.
- Unicode compression of `nvarchar`: Latin values are stored one byte per character
  (Latin-1 window); when that single-byte form would have even length, a trailing 0x10
  marker byte is appended (making the stored length odd). Even stored length = plain
  UTF-16LE. Decoder: even → UTF-16LE; odd → strip trailing 0x10 if present, Latin-1.
  Bytes ≥ 0x80 in single-byte mode are rejected (SCSU windows not implemented).
- Integers: big-endian, trimmed to minimal length. `tinyint` (unsigned) is stored
  plainly; signed types are stored order-preserving, biased by 2^(8·len−1) (observed:
  int value 1 → `0x81`; validated via G/L Account "Account Type"/"Direct Posting" on
  both versions). Zero → EMPTY (zero bytes).
- `uniqueidentifier`: 16 raw bytes in the standard SQL GUID byte order (first three
  groups little-endian) — validated against SELECT output.
- `rowversion`/`timestamp`: big-endian trimmed, unbiased.

### Type encodings (`Values.cs`, `Scsu.cs`)
Derivation method for everything below: a purpose-built scratch database (`typeprobe`,
created by `tools/typeprobe.sql`) with known values in three storage variants
(uncompressed / row-compressed / page-compressed), inspected via DBCC PAGE styles 1+3,
decoded independently, and validated by comparing complete decoded tables with SELECT
output — both on the typeprobe database (its backup is committed as
`fixtures/typeprobe.bak`) and on the BC demo databases (G/L Entry decimals/datetimes,
Tenant Media blobs, on 27.5 and 28.1). All rules hold on both.

- **decimal, row/page compression** (the vardecimal form): first byte = sign bit 0x80
  (set = positive) plus a 7-bit exponent biased by 64−1; remaining bytes are the
  mantissa as 10-bit base-1000 digit groups packed MSB-first, trailing zero bytes
  trimmed; value = 0.digits × 10^exponent. Zero is stored EMPTY. Observed anchors:
  1 → `c0 19` (0.100×10¹), 0.01 → `be 19`, −3 → `40 4b`, 99999 → `c4 f9fde0`,
  ±max-38-digit values → 17 bytes, all matching SELECT exactly.
- **decimal, uncompressed**: [u8 sign, 1 = positive][magnitude little-endian in
  4-byte units per precision].
- **datetime, row/page compression**: the 64-bit quantity (days-since-1900 in the high
  32 bits, 1/300-second ticks in the low 32) stored like a compressed bigint —
  big-endian, biased by 2^(8·len−1), trimmed; zero (1900-01-01 00:00) stored EMPTY.
  Negative day counts (pre-1900 dates) validated: 1753-01-01 → `7f 2e 46 00 00 00 00`.
- **datetime, uncompressed**: [i32 ticks-of-day][i32 days since 1900], little-endian.
- **date / time(s) / datetime2(s)**: identical layout compressed and uncompressed —
  date = 3-byte LE days since 0001-01-01; time = LE scaled seconds (3 bytes for scale
  0–2, 4 for 3–4, 5 for 5–7); datetime2 = time bytes then date bytes. Not trimmed
  under compression (all-zero datetime2(7) is stored as 8 zero bytes).
- **real/float, compressed**: little-endian IEEE bytes with trailing (low-order) zero
  bytes trimmed; the stored bytes are the high end of the value.
- **SQL Server "Unicode compression" is SCSU** (Unicode Technical Standard #6, a public
  spec): stored odd length = SCSU (the encoder appends a 0x10 tag — SC0, which emits
  nothing — whenever the SCSU form would be even); even length = plain UTF-16LE.
  Validated with Latin-1, Cyrillic (SC2), Greek (SD7 window define), CJK and emoji
  surrogate pairs (SQU quoting) against SELECT. MAX types are never SCSU-compressed.
- **binary(n), compressed**: trailing zero bytes trimmed; pad right to the declared
  width. char/nchar: trailing blanks trimmed; pad right with spaces.
- **CD code 0xB (BIT_COLUMN)**: a bit with value 1, carried entirely by the CD code
  (no data bytes); bit 0 is EMPTY. Name from DBCC PAGE, values validated.

### Off-row storage (`Lob.cs`)
Derived from typeprobe LOB tables (image/text/ntext, varbinary(max)/nvarchar(max),
row overflow; sizes from 0 bytes to 180 KB producing single records, multi-link roots,
and two-level trees), DBCC PAGE annotations, and byte-level record dumps; validated by
SELECT comparison including SHA-256 of every `Tenant Media` blob in both BC demo
databases.
- In-row 16-byte text pointer (image/text/ntext): [u64 timestamp][6-byte page ptr]
  [u16 slot] → a record on a type-3/4 (TEXT_MIX/TEXT_TREE) page.
- In-row inline root (MAX types and row overflow), 12+12n bytes: [u8 type: 2 =
  row-overflow, 4 = LOB root][u8][u8 level][u8][u32 updateSeq][u32 timestamp] then n
  links of [u32 cumulative size][6-byte page ptr][u16 slot].
- LOB-page records: [u16 statusA][u16 record length][u64 blobId][u16 type]:
  type 0 SMALL = [u16 length][u16 x][u16 0] + data; type 3 DATA = data to record end;
  type 5 LARGE_ROOT_YUKON = [u16 maxLinks][u16 curLinks][u16 level][u32] + 12-byte
  links as above; type 2 INTERNAL = [u16 maxLinks][u16 curLinks][u16 level] + 16-byte
  links ([u64 cumulative size][6-byte ptr][u16 slot]). Assembled length is checked
  against every link's cumulative size — mismatch throws.
- **The SMALL length is a u16, not a u32.** The word at +16 is 0 on freshly written
  values but 1 after the value was rewritten; fusing it into a u32 length produced
  65,536+ byte lengths on updated rows ($ndo$environmentproperty and Tenant Web
  Service OData in the BC 28.1 demo database). DBCC PAGE annotates only "Size", which
  matches the u16. Reproduced by `probe_lob_upd` (UPDATE of small text/image values);
  the size is bounds-checked against the record length, and the +16 word is carried
  as unknown (not needed for decode).
- **Type 8 is a NULL root** (DBCC PAGE: "Type: 8 (NULL)"). Updating a legacy
  text/image value to NULL keeps the in-row text pointer and rewrites the root record
  to type 8 (with stale value bytes still in the record body); the value is SQL NULL.
  Validated against SELECT: probe_lob_upd row 2, $ndo$dbproperty.license, and all
  2,066 NULL "User Code" values of Application Object Metadata (14,140 rows compared
  via SHA-256, `bc281-application-object-metadata.tsv`). A type-8 record below a root
  or carrying a nonzero size throws.
- **Compressed records**: the long-data-region end-offset array uses its high bit
  (0x8000) to mark an entry as an off-row pointer rather than inline data — the same
  convention as the FixedVar variable-offset array. In-row LOB data small enough for
  the short region is always inline data.
- A trailing empty (zero-length, non-NULL) variable-length column can be omitted from
  a FixedVar record's variable section entirely; NULL is signalled only via the null
  bitmap. (Observed: rows whose last varchar columns are empty strings.)

### Ghost records in compressed pages
- CD record header: 0x01/0x21 = live (PRIMARY_RECORD), 0x0D = ghost
  (GHOST_DATA_RECORD) — bits 2+3 set. Derived by deleting rows from a
  page-compressed probe table immediately before BACKUP (so ghost cleanup could not
  purge them) and correlating every record header byte on the page with DBCC PAGE
  record types (267 primary / 67 ghost, no exception). Validated end-to-end: the
  backed-up page holds 500 records, 166 ghosts; decoding returns exactly the 334
  rows SELECT returns. A header with only one of the two bits set has never been
  observed and throws.
- CI structure refinement: the u16 end-offsets after the CI header are conditional —
  one per present part (end-of-anchor if bit 1, end-of-dictionary if bit 2). The
  prototype had only seen pages with both; an anchor-only page (no dictionary)
  carries a single offset field and its anchor record starts 2 bytes earlier.

- **CD column count ≥ 128**: two-byte form — high bit of the first count byte set,
  count = ((first & 0x7F) << 8) | second. Derived from a 203-column probe table
  (bytes `80 cb`); validated against SELECT on the probe and on the 216-column BC
  `Gen. Journal Line` table of the 28.1 demo database.

### Still not determined (implemented as loud failures)
- `money`/`smallmoney`/`smalldatetime`/`sql_variant`/`xml` value encodings (not used
  by BC tables; the reader throws naming the type).
- varchar/char/text bytes ≥ 0x80 are decoded as Latin-1 (ISO-8859-1). The single-byte
  collation of a customer database could map 0x80–0x9F differently (e.g. cp1252);
  BC's own single-byte columns are ASCII in every observed database.

### AL meaning layer (`Symbols.cs`)
- `SymbolReference.json` inside shipped `.app` packages is the schema source, taken as
  an input (the apps a database was built from). Package structure observed on the BC
  27.5/28.1 artifacts: the shipped file is a zip wrapper holding one inner NAVX `.app`;
  NAVX = 4-byte magic + u32 header length (40) + zip. Tables and table extensions can
  be nested in AL namespaces — the loader walks `Namespaces` recursively (the shipped
  Base Application defines 1,523 tables this way, `Customer` = id 18).
- AL name → SQL identifier: characters invalid in SQL identifiers (`."\/'%[]`) become
  `_` (observed across all demo-database object and column names). FlowField/FlowFilter
  fields are computed, not stored — they have no SQL column.
- **Table extensions.** A `tableextension`'s fields live in a companion SQL table named
  `<company>$<table>$<base app id>$ext` whose columns are `<Field>$<extending app id>`
  (plus a mirror of the base table's primary-key columns and `timestamp`); one AL record
  is the base row LEFT-JOINed with its companion row on the companion's own key (see
  "Extension companion join key" below), and a base row
  can exist without a companion row (observed in the demo databases; the merged decode
  matches the oracle's LEFT JOIN — `bc281-source-code-setup-merged.tsv` and the
  `exttest` probe fixture). In `SymbolReference.json`, `TableExtensions` sit beside
  `Tables` in every namespace; `TargetObject` is either a plain table name or the
  qualified form `#<32-hex target app id>#Name` (observed in the shipped Base
  Application, e.g. SourceCodeSetupExt targeting Business Foundation's
  `Source Code Setup`).

### Extension companion join key

The key `--merge-extensions` joins a `$ext` companion to its base table on is the
**companion's own key**, not the base table's clustered key. For almost every table
those are the same columns, which is why the difference stays invisible until it is not.

- In AL the first declared key is the table's primary key, and any one key may carry the
  `Clustered` property. BC builds the SQL clustered index from whichever key carries
  `Clustered = 1` and names the index after that key; it keys the `$ext` companion on the
  base table's *primary* key and names that index `<companion>$Key1`.
- Base Application 28.1 table 181 `Posted Gen. Journal Line` is the one shipped table
  where the two diverge. Read from the `SymbolReference.json` inside
  `Microsoft_Base Application_28.1.49838.50621.app`: `Key1 = [Line No.]` with no
  `Clustered` property, `Key2 = [Journal Template Name, Journal Batch Name, Line No.]`
  with `Clustered = 1`. Field order is no guide either — field 1 is
  `Journal Template Name` and the primary key is field 2 alone.
- The database agrees. On the restored `bc281`, `sys.indexes` / `sys.index_columns` give
  the base table a CLUSTERED index named `$Key2` over those three columns and its
  companion a CLUSTERED index named `<companion>$Key1` over `Line No_` alone.
- Scale, measured on `bc281`: over every base/`$ext` pair, exactly one pair's clustered
  keys differ — `Posted Gen. Journal Line`, in both companies. Over the 114 pairs whose
  base table is defined in the Base Application symbols, each companion's clustered key
  equals its base table's first declared key in 114 of 114 cases, table 181 included.
- The two containers answer this from different metadata — a `.bak` from the clustered
  index (`sysiscols`, `index_id = 1`), a `.bacpac` from `model.xml`'s
  `SqlPrimaryKeyConstraint` — so they disagreed on exactly this table: the `.bacpac` path
  joined it and the `.bak` path refused it. Asking the *companion* asks both containers
  the same question, because for a companion the clustered index is the primary key.
  `IBcSource.RowKeyColumns` is named for what each container can actually answer; it is
  not a primary key and must not be used as one.
- A companion whose key names a column its base table does not have still refuses by
  name (`probe exttest3`), because that is a broken pair rather than a wrong premise.
- Validated: `bc281-posted-gen-journal-line-merged.tsv` (the oracle's LEFT JOIN) matches
  through the `.bak`; the `exttest2` probe reproduces the shape hermetically and matches
  the oracle through both the `.bak` and the `.bacpac`. A merged read of all 387
  non-empty CRONUS base tables of bc281, with the 134 shipped apps as symbols, returns
  387 ok / 40,455 rows; before this it was 386, `Posted Gen. Journal Line` refusing with
  "lacks base key column Journal Template Name". GitHub issue #17.

### Physical rowset layout — sysrscols (`Catalog.RowsetColumns`)
The record layout of a rowset must come from the `sysrscols` system table, never from
declaration (syscolpars) order. On any database with ALTER history — every upgraded BC
database — physical order, fixed offsets, variable-column ordinals and null-bit numbers
all diverge from declaration order: dropped columns keep their physical slots (marked by
rscolid flag 0x04000000 and status bit 0x02), added columns land after them, bit columns
share a byte at recorded bit positions, and rows written before an ALTER keep their old
column count (columns whose null bit exceeds a row's stored column count read as NULL;
compressed records carry the same versioning in their CD column count). sysrscols row
layout (54-byte fixed part): rsid u64@0, rscolid u32@8, hbcolid u32@12, ti u32@24
(low byte = system type id; then per type: decimal precision@+8/scale@+16, time-family
scale@+8, string/binary max length@+8 with 0 = MAX), ordkey i16@32, status u32@36,
leaf offset i16@40 (negative = variable-column ordinal), null bit u16@44, bit position
u16@48. Derived from probe tables with ALTER history and validated field-by-field
against sys.system_internals_partition_columns; end-to-end validated by full-table
comparison on an upgraded production database (848,326-row G/L Entry exact) and by the
committed `probe_altered`/`probe_altered_page` fixtures.

**Internal columns (rscolid flag 0x08000000).** A sysrscols row whose rscolid carries
flag 0x08000000 is a physical column that is no user column: it occupies a fixed-data
offset and a null bit but has no syscolpars entry, and its masked low bits collide with
a real column id (observed value 0x08000002 → masked id 2). Observed cause: enabling
change tracking adds an internal in-row bigint version column at the end of the fixed
data (`sys.system_internals_partition_columns` shows it as partition_column_id
134217730, system type 127, joined to no sys.columns row; on bc281 exactly the three
change-tracked tables Published/Installed/Inplace Installed Application carry it, per
`sys.change_tracking_tables`). Mapping it by masked column id shadowed the colliding
user column's value — "GUID cell of 8 bytes in Runtime Package ID" on Published and
Installed Application. The reader treats it like the uniquifier: a physical slot with
no user value. Reproduced hermetically by `probe_tracked` (change tracking enabled
mid-table, rows before and after enablement plus a rewritten pre-tracking row);
validated value-for-value against SELECT on the restored bc281 for all 134 Published
Application and 95 Installed Application rows (`bc281-published-application.tsv`,
`bc281-installed-application.tsv`).

### Clustered index descent — seeking a catalog base table (`ClusteredSeek.cs`)
The reader looks syscolpars and sysiscols up by object id and sysrscols up by rowset id,
and all three are clustered on exactly that value, so the row can be reached by descending
the index instead of scanning the leaf level. Measured on the BC 28.1 demo backup, a
single-table read touched all 5,196 catalog leaf pages (~42 MB), of which syscolpars
(1,655), sysrscols (948) and sysiscols (237) are reachable by seek.

**Non-leaf index record layout.** `[status byte][key columns, packed, in key order, each at
its storage width][6-byte child page pointer: u32 page id, u16 file id]`. Derived from
`DBCC PAGE` dumps of three bc281 clustered index roots with three different key shapes and
cross-read against DBCC's own decode of every field:
- syscolpars root 1:46387, key (id int, number smallint, colid int) = 10 bytes, record 17.
  Slot 1 bytes `06 | eb 9a f3 14 | 00 00 | 01 00 00 00 | 98 0a 01 00 | 01 00` against DBCC's
  `id=351509227 number=0 colid=1 child=(1:68248)`.
- sysrscols root 1:48121, key (rsid bigint, rscolid int) = 12 bytes, record 19.
  Slot 1 `06 | 00 00 b5 09 00 00 00 01 | 03 00 00 00 | fa bb 00 00 | 01 00` against
  `rsid=72057594200784896 colid=3 child=(1:48122)`.
- sysiscols root 1:148, key (three ints) = 12 bytes, record 19. Slot 1
  `06 | 29 00 00 00 | 18 00 00 00 | 01 00 00 00 | f8 bd 01 00 | 01 00` against
  `41, 24, 1, child=(1:114168)`.

**Record width is the page header's pminlen (u16 at offset 14),** so the key width need not
be known independently and nothing is hardcoded per table: the child pointer sits at
pminlen - 6. Verified against `DBCC PAGE` headers: pminlen 17/19/19/11/15/15 for
syscolpars/sysiscols/sysrscols/sysschobjs/sysallocunits/sysrowsets against key widths
10/12/12/4/8/8. This also matters because sysrscols cannot be asked for its own layout.

**Levels.** A type-2 page at `m_level` 0 is the lowest index level and its children are the
type-1 leaf pages; DBCC's "Level" column is `m_level + 1`. Confirmed by shape: the
syscolpars root at m_level 1 has 5 children which are m_level 0 pages, which in turn point
at its 1,655 leaf pages, while the sysiscols root at m_level 0 has 238 slots for its ~237
leaf pages. The descent therefore stops on page **type**, never on a level count.

**Slot 0 carries no key.** DBCC renders the first index row's key columns NULL on every
root dumped. Treating slot 0 as "below everything" is safe regardless: the parent already
established that the target belongs in this page's range.

**The descent takes the last child strictly below the target, not the last child at most
the target.** Only the leading key column is compared, and rows sharing a leading value
straddle page boundaries — one object's syscolpars rows span several leaf pages. Choosing
the last child whose key is `<=` the target skips the earlier pages of such a run and
silently returns part of an object's columns. Taking the last child strictly below lands on
the page holding the final row before the target, so a seek reads at most one leaf page
that holds nothing wanted, and the leaf chain is then followed forward while the leading
key still matches.

**root_page is not trusted at face value** (see "Never trust catalog metadata"): a root
outside the allocation unit's IAM page set is treated as stale and the caller scans. Index
pages carrying ghost records are likewise declined rather than guessed at, since a ghosted
index record's child pointer is a shape this derivation does not cover. Declining can only
cost time — every caller has a scan that produces the same rows.

**Validation.** Seek and scan were compared for every user table of both demo backups and
the probe database: 3,955 tables on bc281, 3,774 on bc275, 22 on typeprobe — columns, index
columns and full physical rowset layout each compared row for row. 23,265 seeks, zero
declines, zero mismatches. Permanent coverage:
`TypeprobeEndToEndTests.ClusteredKeySeekAgreesWithTheFullScanForEveryObject`,
`RowsetLayoutSeekAgreesWithTheFullScan`,
`SeekOnAMissingKeyReturnsNothingRatherThanTheNeighbouringRows` (hermetic) and
`BcDemoBackupTests.ClusteredSeekAgreesWithTheFullScanOnEveryTable` (skippable, both demo
backups, and asserting zero declines so a silent fall back to scanning fails the test).
Every `verify.sh` fixture also reads through the seeked layout.

### Heaps and empty slots
- BC extension tables can exist as heaps (no clustered index) in real databases; the
  reader falls back to the idminor-0 rowset. Validated on a 764,688-row production
  heap (exact) and the committed `probe_heap` fixture.
- A slot array entry of 0 is an **empty slot** (heap deletes leave them): SQL Server's
  scan skips it and DBCC PAGE renders nothing for it. Treating it as a record offset
  reads the page header bytes as a record (the header's first byte has the CD bit set,
  which produced phantom all-NULL rows on a production heap before the fix). Any other
  offset below 96 throws. Reproduced hermetically: `probe_heap` carries 99 empty slots.
- Allocation pages (PFS/GAM/SGAM/DCM/BCM) recur at fixed intervals through the whole
  file and can sit inside an extent an IAM claims for a table (measured on a 23 GB
  production database: PFS pages every 8,088 pages inside ordinary data extents once
  mixed-page allocation is off, as it is by default since SQL Server 2016). They are
  skipped; genuinely unexpected page types in a data extent still throw.
  `tools/scale.sql` reproduces the shape (mixed allocation off for the big table).

### Validation against an independent production database
The full pipeline was validated once against a ~23 GB single-file production
Business Central database backup (BC 21 lineage upgraded to BC 24, written by
SQL Server 2019, six GAM intervals, ~2,100 tables, no data compression, third-party
extension schema, heaps, ALTER history throughout). Method: one RESTORE into the
oracle, then structural and full-table comparison; no fixture, page image or row
content from that file is committed anywhere.
- Structural map: 3,020,080 pages mapped; 3,019,763 byte-identical to the restore,
  6 header-only, 311 body-different — every one of the 311 accounted for: 297 SQL
  system-catalog pages (RESTORE ran SQL 2019→2022 version-upgrade steps) and 14
  allocation/bookkeeping pages (PFS/DCM/boot/file header). Zero BC table data pages
  differ. Unreplayed transaction-log stream: 65,536 bytes (same as every observed
  quiesced backup).
- Full-table decodes, each compared line-for-line with SELECT on the restore:
  an 848,326-row posting table (decimals, dates, real history), a 3,123,102-row
  change-log table (23 rows re-verified via JSON output after the comparison
  harness's field separator appeared inside logged values), a 138-row
  page-compressed table, 21,175 media blobs by SHA-256, a 764,688-row third-party
  heap, and a 140,824-row table-extension companion table. All identical.
- Two defects found and fixed (both reproduced in committed probe fixtures, see
  above): declaration-order layout assumptions replaced by sysrscols, and empty
  heap slots. Two structural facts corrected: the file-header Size field position
  and the region-2 placement rule (see those sections).

## The `.bacpac` container (`Bacpac.cs`)

A BC cloud export is a `.bacpac`: an Open Packaging (zip) archive. Everything below was
observed in files sqlpackage 170.5.76 produced from SQL Server 2022 — the `typeprobe`
probe database (committed as `fixtures/typeprobe.bacpac`) and one real 52 MB production
export, of which nothing is committed.

- **Entries.** `model.xml` (the schema), `Origin.xml` (package metadata),
  `DacMetadata.xml` (database name and version), `[Content_Types].xml`, `_rels/.rels`,
  and, for every table that has rows,
  `Data/<schema>.<url-encoded table name>/TableData-NNN-MMMMM.BCP`. Folder names are
  URL-encoded (`%20` = space, `%24` = `$`); the reader unescapes entry names and does not
  escape table names, so it never has to guess DacFx's escaping rule. A table with
  no rows has no folder at all — 3,347 of the production export's 3,914 tables.
- **`Origin.xml` carries a SHA-256 over `model.xml`** (`<Checksum Uri="/model.xml">`),
  plain, over the raw entry bytes. Verified byte-exact on both files, and enforced on
  read as a structural identity guard: a mismatch throws.
- **`Origin.xml` declares a Data stream version** (`<Version StreamName="Data">`),
  `2.0.0.0` in both files even though their `ModelSchemaVersion` differs (2.9 vs 3.3).
  The row framing below was derived for 2.0.0.0 only; any other value throws rather
  than being read with an assumed format.
- **`NNN` is an export batch and `MMMMM` its continuation** past roughly 4 MiB
  (observed sizes 4,198,980–4,231,054 bytes before a new `MMMMM`; one 6.5 MB file where
  a single large row would not fit). Both boundaries fall between rows: parsing the 860
  entries of the production export file-by-file and parsing them concatenated give the
  identical 178,189 rows. The reader concatenates in `(NNN, MMMMM)` order, which is
  correct either way.
- **`model.xml` is the whole schema source**, standing in for `sysrscols`/`syscolpars`.
  It is 113 MB in the production export, so it is streamed with `XmlReader` (only
  `SqlTable` and `SqlPrimaryKeyConstraint` subtrees are materialized) and parsed on
  first use, not at open. Column facts used: the order of the `Columns`
  relationship, `IsNullable` (absent means nullable), and the type specifier's type
  reference plus `Length` / `Precision` / `Scale` / `IsMax` (each absent when zero or
  false). `SqlComputedColumn` entries are not stored and carry no data. The primary-key
  constraint's `ColumnSpecifications`, in order, are what `RowKeyColumns` answers for a
  `.bacpac` — the key `--merge-extensions` joins an `$ext` companion on (see "Extension
  companion join key").
- **The order of the `Columns` relationship is the order of values in the data stream.**
  Validated at value level on every probe table and, on the production export, by
  full-table comparison against the sqlpackage import (below) — a wrong order would
  swap same-width columns silently, which is exactly what that comparison rules out.

### `.bacpac`: native BCP row framing (`Bcp.cs`)

A data stream is a bare sequence of rows: no header, no trailer, no row delimiter. Each
row is its columns in model order, each column a length prefix followed by that many
value bytes; a prefix of all one-bits is SQL NULL (a zero-length prefix is an *empty*
value, which is why `0x0000` and `0xFFFF` must not be conflated). Prefix widths were
derived from probe tables with chosen values — `probe` (every type nullable) and
`probe_notnull`, added for this work (every type NOT NULL) — and re-checked against all
567 populated tables of the production export.

| prefix | types |
|---|---|
| 0 bytes | the fixed-length numeric and temporal types, and `char(n)`, **when NOT NULL** |
| 1 byte | those same types when nullable; `bit`, `uniqueidentifier`, `decimal`/`numeric` **always** |
| 2 bytes | `char(n)` when nullable; always `nchar`, `nvarchar`, `varchar`, `binary`, `varbinary`, `rowversion` |
| 4 bytes | `text`, `ntext`, `image` |
| 8 bytes | any `(max)` type |

This is *not* bcp's documented native-format prefix rule (which would give a
non-nullable `bit`, `uniqueidentifier` or `decimal` no prefix at all); it is DacFx's own
serialization, so it was taken from the bytes rather than from the bcp documentation.
Evidence for the two surprising rows:

- `bit` NOT NULL: `probe_notnull` row 1 has `n_bigint` = 0 ending at offset 0x12 and the
  `decimal(38,20)` prefix `0x13` at 0x15; the two bytes between are `01 00` — a length
  of 1 and a value of 0, not a bare value byte. Row 2 (`n_bit` = 1) reads `01 01`.
- `char(n)` NOT NULL is unprefixed while `nchar(n)` NOT NULL is prefixed: in
  `probe_notnull` row 2 the 20 bytes of `N'ÆØÅ'` padded to `nchar(10)` are preceded by
  `14 00`, and the 20 bytes of `'xyz'` padded to `char(10)` follow them immediately,
  with the next `08 00` being the `binary(8)` prefix.

Types whose framing no observed file exercises — `money`, `smallmoney`, `smalldatetime`,
`datetimeoffset`, `xml`, and anything with no SQL Server system type id known here —
throw naming the table, the column and the type. They are an open gap, not a guess: a
column whose width is unknown makes the rest of the row unreadable, so the whole table
is refused.

Value encodings, relative to the storage forms the `.bak` reader already decodes:

- **`char`, `varchar` and `text` are written as UTF-16**, not in the column's collation
  code page (`probe` row 2: `c_varchar` = `'Hello'` occupies 10 bytes). The shared
  decoder is told so with `textIsUtf16`; nothing guesses a code page, and SCSU never
  applies. `nchar`/`nvarchar`/`ntext` are UTF-16 in both containers.
- **`datetime` has its halves the other way round**: `[i32 days since 1900][u32 ticks of
  1/300 s]`, where storage is `[ticks][days]`. `probe_notnull` row 2
  (`9999-12-31 23:59:59.997`) reads `7f 24 2d 00 | ff 81 8b 01` = 2,958,463 days then
  25,919,999 ticks; the other reading gives year 72,881.
- **`time` and the time half of `datetime2` are always five bytes of 100 ns units**
  whatever the declared scale, where storage uses 3/4/5 bytes of 10^−scale units
  (`c_time0` = `23:59:59` reads 863,990,000,000). The reader rescales and throws if a
  value does not divide exactly into the declared scale. The three-byte date half is
  the same in both.
- **`decimal` carries its own precision and scale**: `[19][precision][scale][sign: 1 =
  positive][16-byte magnitude, little-endian]`, where storage is `[sign][magnitude]` in
  the 4/8/12/16 bytes the precision needs. The reader narrows it and throws both when
  the dropped magnitude bytes are non-zero and when the precision/scale the value
  carries differ from model.xml's — either means this stream was paired with the wrong
  schema.
- **`rowversion` is eight bytes in storage order**, read big-endian as the `.bak` path
  does (`probe_notnull` rows read `0x…07E9`, `07EA`, `07EB` — consecutive, as a
  rowversion must be).
- Everything else — the integer family, `real`, `float`, `date`, `bit`,
  `uniqueidentifier`, `binary`, `varbinary`, `image`, and all `(max)` values — is
  byte-identical to the storage form, so `Values.cs` decodes both containers.
- **LOBs are inline.** A `(max)` value's 8-byte prefix is its whole length (a
  160,000-byte `varbinary(max)` in `probe_lob` row 4 is written as one run); none of the
  `.bak` reader's text pointers, LOB trees or row-overflow handling applies. SQL
  Server's chunked "unknown length" form `0xFFFFFFFFFFFFFFFE` appears in no observed
  export and is refused rather than guessed.

### `.bacpac`: validation

- **Hermetic, value-for-value.** `fixtures/typeprobe.bacpac` is a sqlpackage export of
  the same `typeprobe` database state as `fixtures/typeprobe.bak`, so both containers
  are checked against the *same* oracle fixtures: `dotnet test` and `verify.sh` compare
  17 probe tables through each path, including the merged base + `$ext` read. A
  round-trip check (`sqlpackage /Action:Import` of that file into the oracle, then
  re-exporting every fixture TSV from the imported copy) reproduced all 19 committed
  typeprobe fixtures byte-for-byte, which is why they can serve as the bacpac's
  expected values.
- **Real data, decode inventory.** The 52 MB production export (860 data files,
  3,914 tables in model.xml, 567 with rows) decodes with **zero failures**: every value
  of every row of every table, 178,189 rows.
- **Real data, against the oracle.** The same file was imported with
  `sqlpackage /Action:Import` and every one of the 567 populated tables compared
  column-for-column and row-for-row against `SELECT`, order-insensitively:
  **all 567 identical**, 178,189 rows. Three columns are excluded and why:
  - `rowversion`: the server assigns new values on import, so it cannot survive a
    round trip. It is validated instead against the oracle-exported
    `typeprobe-probe-notnull.tsv`, whose values come from `SELECT` on the source
    database itself.
  - `real`/`float`: SQL Server's float-to-string form is not .NET's round-trip form.
    Validated instead against chosen literals in `BacpacEndToEndTests`.
  - One `nvarchar` column of `NAV App Installed App` holds a binary content hash whose
    control characters break sqlcmd's line-based output. That table was compared in two
    passes instead — all other columns directly, that column by SHA-256 of its UTF-16
    bytes on both sides; 52 of 52 rows identical either way.

  Nothing from that file is committed — no fixture, no bytes, no values.

  Two things this comparison surfaced that are *not* bacpac facts, recorded so the next
  run does not rediscover them: the oracle container needs `mssql-server-fts` installed
  (BC tables carry full-text indexes, and without it the import fails after deploying
  the schema but before loading any data), and `--select` trims the names it is given,
  so a column whose SQL name has a trailing space — BC has them — cannot be addressed by
  `--select` in either container, though a `read` without `--select` still returns it.

## BC version differences observed (27.5 vs 28.1)
- 28.1 demo databases contain a second company, `My Company`, with populated tables
  (27.5 W1 has only `CRONUS International Ltd_`). Table resolution needs `--company`.
- 28.1 has ~4,300 more pages and 5× as many superseded (duplicate) page images.
- All structural facts above hold identically on both versions.

## BC version differences observed (28.1 vs 28.2+)
- The demo database's content outgrew the size recorded in its data file's header page.
  Every structural fact above still holds; only the reader's use of that size as a bound
  did not. See "File size is a lower bound, not the end of the copied data".
- 28.2, 28.3 and 28.4 read correctly once the walk ends at the copy's end; 28.1's map is
  byte-identical before and after that change (SHA-256 over G/L Entry, Customer,
  G/L Account and Item, `CRONUS International Ltd_`).
