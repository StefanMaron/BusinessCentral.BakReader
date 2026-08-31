# Loud failures: never a default for something you could not decode

A consumer of this tool acts on the rows it prints. A silently wrong value —
a zero for an undecodable decimal, an empty string for an unsupported encoding,
a truncated blob — looks exactly like a right value and will be trusted. That is
strictly worse than an error.

**The rule:** when the reader meets anything it cannot handle faithfully —
a type, an encoding variant, a record shape, a backup layout — it throws,
naming what it met (column name, type name, page id) and why it refused.
Never return a default, never skip a row silently, never truncate.

Existing patterns to keep:
- `NotSupportedException` with the type and column for undecodable types
  (`money`, `sql_variant`, …).
- `InvalidDataException` "refusing to guess" when a structure differs from every
  observed file (unknown MTF layout, wrong block counts, failed link-size checks
  in LOB trees).
- Structural identity guards: the page map's block counts must add up exactly,
  padding must verify as filler, LOB assembly must match every cumulative size.
  A guard that fails is a derivation opportunity, not something to relax.

The one acceptable "keep going" is skipping a page or record the format itself
declares dead (ghost records, PFS-deallocated pages) — because SQL Server skips
them too, and PROVENANCE.md documents the evidence.
