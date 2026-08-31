# No silent skips: a test that cannot run reports as skipped, never as passed

The full-file tests need ~900 MB demo backups that cannot live in CI. The failure
mode this rule exists to prevent: those tests silently pass (or silently vanish)
when the files are absent, and a green CI claims coverage it does not have.

**Rules:**
- A test whose input is unavailable reports **skipped** (`SkippableFact` /
  `Skip.If`) with a message naming the missing file. Never `return` early from a
  passing test body.
- CI asserts the skip actually happened: the workflow greps the test output for a
  non-zero skip count. If someone makes the suite "all green" in CI, CI fails.
- `verify.sh` is the stricter local gate: with the demo backups absent it FAILS
  (exit 3) rather than skipping, because on a dev machine absence is a setup
  error, not an environment fact.
- When adding a new fixture-dependent test, decide which side it is on: hermetic
  (committed fixture, runs everywhere) or full-file (skippable, verify.sh).
