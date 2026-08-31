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

### Backup container / page map
- Page images are 8192-byte blocks, 8192-aligned within the `.bak`, self-identifying
  via `m_pageId` at header offset 32 (u32 page id + u16 file id). Observed: first image
  at file offset 16384; page types at expected well-known positions (1:1=PFS, 1:2=GAM,
  1:3=SGAM, 1:6=DCM, 1:7=BCM, 1:9=boot) match the documented page architecture.
- **Duplicate page images**: a page id can occur multiple times (27.5: 327, 28.1: 1622).
  Validated against oracle-restored MDF files: the **last** occurrence always matches
  what `RESTORE` produces (120/120 sampled on 27.5, 40/40 on 28.1); the earlier
  occurrence never does. Implemented as last-one-wins (`PageFile.cs`).
- Page images extracted from the bak were confirmed byte-identical to the corresponding
  pages of the restored MDF (spot checks including pages 61560, 70688 on 27.5).

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
  from +4, LSB-first (bit *n* of byte *k* = extent 8k+n, pages (8k+n)*8 ..+7). Derived
  from the single set bit for single-extent tables (No. Series extent 7695 → byte 961
  bit 7 = 0x80; Customer extent 7178 → byte 897 bit 2 = 0x04); page sets for whole
  tables matched `sys.dm_db_database_page_allocations` exactly.
- Single-page (mixed-extent) slots in IAM slot 0: **both demo databases contain zero
  mixed-extent data pages** (oracle: `is_mixed_page_allocation=1 AND page_type=1`
  returns nothing). The reader requires the slot-0 tail to be all zero and fails loudly
  otherwise; interval base is assumed 0 and multi-page IAM chains are rejected.

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

### Not yet determined (implemented as loud failures / explicit raw output)
- `decimal` non-zero encoding under row compression (zero = EMPTY is validated).
- `datetime`, `datetime2`, `date`, `time` encodings (7-byte datetime2(3) values with a
  0x80... lead byte were observed; likely the same biased-integer scheme per component,
  but unvalidated — the reader emits explicit raw bytes instead).
- SCSU beyond the Latin-1 single-byte window.
- Off-page LOB storage (text/image/varbinary(max) pointer chase into type-3/4 pages).
- CD records with ≥128 columns (2-byte column-count form).
- Ghost-record detection inside CD records (pages with ghosts are rejected).
- The exact semantics of the IAM slot-0 header fields (interval base is assumed 0 and
  guarded).

## BC version differences observed (27.5 vs 28.1)
- 28.1 demo databases contain a second company, `My Company`, with populated tables
  (27.5 W1 has only `CRONUS International Ltd_`). Table resolution needs `--company`.
- 28.1 has ~4,300 more pages and 5× as many superseded (duplicate) page images.
- All structural facts above hold identically on both versions.
