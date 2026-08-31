# Clean room: no other reader's code, ever

This project's value depends on being demonstrably independent: it is MIT-licensed,
and there exists at least one GPL-3.0 project (OrcaMDF) that reads SQL Server MDF
files. Reading GPL code and then writing similar code creates a derivative-work
question that cannot be un-asked. Independence is only demonstrable because every
structural fact in this codebase has a documented, non-code origin.

**The rule:** never read, fetch, search for, decompile, or otherwise consult any
other MDF-, BAK-, or database-file-reading project's source code — GPL or not.
That includes code "just for a hint", ports of such code found elsewhere, and blog
posts that reproduce such code verbatim.

**What to use instead** — the sources that built everything here so far:
- Microsoft's published documentation (pages-and-extents guide, compression
  implementation docs, the MTF specification).
- Microsoft's own tooling: `DBCC PAGE` annotated dumps from the oracle.
- The files themselves, plus purpose-built probe databases with known values
  (`tools/typeprobe.sql`, `tools/scale.sql`).
- The oracle SQL Server for validation.

If something cannot be determined from those sources, **stop and say so** in the
report/issue — an open gap is fine; a contaminated derivation is not. Every
non-obvious fact you do derive gets a `PROVENANCE.md` entry naming its origin.
