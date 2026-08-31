# Never trust catalog metadata at face value

Measured on Microsoft's own shipped demo databases (which are shrunk after data
load, relocating pages without rewriting metadata):

- `sysallocunits.first_page` and `root_page` can point at pages now owned by a
  different object.
- A page header's `m_objId`/`m_indexId` can belong to a previous owner.
- A page's self-identification (`m_pageId`) is worthless for building the page
  map: deallocated pages keep stale headers, and a stale image elsewhere in the
  backup can carry the same page id as a live page. "Last image wins" picked the
  wrong image for 29 pages across the two demo backups, including live LOB pages.
- IAM/GAM bitmaps have a 32-bit overhang past the 63,904-extent interval whose
  bits are set but are not extents.

**The trustworthy paths, validated byte-for-byte against restores:**
- Page placement: the structural map — GAM/SGAM extent walk plus PFS-extent and
  interval-lead rules, re-read region by DCM diff (`PageFile.cs`).
- Table pages: the IAM chain (bitmap + single-page slots), filtered per page
  through the PFS allocation bit.
- Object identity: `sysschobjs`/`sysrowsets`/`sysallocunits` rows themselves —
  their *page pointers* are the stale part, except `first_iam_page`, which is
  reliable and oracle-validated.

Any new code that wants to trust a metadata shortcut must first prove it against
a restore and record the proof in PROVENANCE.md.
