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
  extent order, 8 blocks per extent, capped at the file size. GAM page: 2 slots;
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
- **File size** comes from the file header page (1:0) "Size" field at page offset 254
  (offset derived by value search across four backups with known sizes; field name
  confirmed via DBCC PAGE). It caps the PFS-extent rule and determines the interval
  count. The RESTORE target size can be larger (the demo backups record 110,464 /
  116,112 pages; the restored files are 118,208 / 122,328).
- **MQDA region 2** re-dumps the extents that changed while the backup ran, per
  interval in order: the interval's lead extents (0 and 1 for interval 0 — the boot
  page lives in extent 1 — and the single first extent for later intervals), then
  every extent whose DCM (differential changed map, page +6 of the interval, same
  bitmap record shape) bit is set in the region-2 image but not the region-1 image,
  ascending. RESTORE applies regions in file order, so region 2 supersedes region 1.
  Validated on the two-interval file: 2,297 predicted extents match the observed
  sequence exactly, including the second interval's section.
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

### Why self-identification ("last image wins") was wrong
The prototype resolved duplicate page images by scanning for plausible page headers and
letting the last image win. Cross-checked against the structural map and the restored
MDFs: **20 pages on 27.5 and 9 pages on 28.1 got the wrong image** under that rule —
deallocated pages keep stale headers, so a stale image with the same page id can appear
later in the stream than the live page. On 27.5 all affected pages are deallocated
(harmless by luck); on 28.1, pages 94,436–94,439 are **live TEXT_MIX LOB pages of the
BC `Published Application` table** — BLOB reads through the old rule would have returned
corrupt data. The structural map matches the restored MDF on every disputed page.
`bcbak check` recomputes this cross-check for any input file.

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
larger log region; `bcbak check` prints the unreplayed log size so that risk is visible.

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
  type 0 SMALL = [u32 length][u16] + data; type 3 DATA = data to record end;
  type 5 LARGE_ROOT_YUKON = [u16 maxLinks][u16 curLinks][u16 level][u32] + 12-byte
  links as above; type 2 INTERNAL = [u16 maxLinks][u16 curLinks][u16 level] + 16-byte
  links ([u64 cumulative size][6-byte ptr][u16 slot]). Assembled length is checked
  against every link's cumulative size — mismatch throws.
- **Compressed records**: the long-data-region end-offset array uses its high bit
  (0x8000) to mark an entry as an off-row pointer rather than inline data — the same
  convention as the FixedVar variable-offset array. In-row LOB data small enough for
  the short region is always inline data.
- A trailing empty (zero-length, non-NULL) variable-length column can be omitted from
  a FixedVar record's variable section entirely; NULL is signalled only via the null
  bitmap. (Observed: rows whose last varchar columns are empty strings.)

### Still not determined (implemented as loud failures)
- CD records with ≥128 columns (2-byte column-count form).
- Ghost-record detection inside CD records (compressed pages with ghost records are
  rejected loudly; none occur in the demo databases).
- `money`/`smallmoney`/`smalldatetime`/`sql_variant`/`xml` value encodings (not used
  by BC tables; the reader throws naming the type).
- varchar/char/text bytes ≥ 0x80 are decoded as Latin-1 (ISO-8859-1). The single-byte
  collation of a customer database could map 0x80–0x9F differently (e.g. cp1252);
  BC's own single-byte columns are ASCII in every observed database.

## BC version differences observed (27.5 vs 28.1)
- 28.1 demo databases contain a second company, `My Company`, with populated tables
  (27.5 W1 has only `CRONUS International Ltd_`). Table resolution needs `--company`.
- 28.1 has ~4,300 more pages and 5× as many superseded (duplicate) page images.
- All structural facts above hold identically on both versions.
