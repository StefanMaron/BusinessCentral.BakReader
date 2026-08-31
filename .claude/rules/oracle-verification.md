# Verify against the oracle, never against yourself

Self-consistent decoding proves nothing: a wrong offset produces plausible
garbage that decodes cleanly and round-trips through this codebase's own logic.
Every load-bearing claim in this project is validated against a real SQL Server
("the oracle") reading the same bytes: `RESTORE` of the same backup, `SELECT`
output, `sys.*` catalog views, `DBCC PAGE` annotations.

**Rules:**
- A new decoder or structural rule is done when its output matches the oracle on
  real data — full-table comparison, not spot checks. Byte-identical restores
  adjudicate page-map questions; `SELECT` adjudicates value questions.
- Fixtures under `fixtures/` are oracle exports. Never edit one by hand and
  never regenerate one from this tool's own output — that converts the fixture
  from evidence into an echo.
- When deriving a new encoding, build probe data with **known values** on the
  oracle (extend `tools/typeprobe.sql`), back it up, and derive from the bytes +
  `DBCC PAGE`. Unknown-value reverse engineering invites confirmation bias.
- Every non-obvious fact goes into `PROVENANCE.md` with its origin: a doc URL,
  a DBCC PAGE dump, or "observed in file X, validated against restore/SELECT".
  That file is a deliverable — it is what makes independence demonstrable.
