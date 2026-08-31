# TDD, with tests that assert real values

Every decoder, structural rule, or fix gets a test first: RED (fails against the
current code) then GREEN. No exceptions.

**Tests must prove, not just pass.** Ask: would this test still pass if the
implementation returned a default (0, "", false)? If yes it is noise — assert
specific concrete values. The strongest tests here compare full decoded tables
against oracle fixtures; unit tests use real cell bytes observed in probe
databases with their oracle-confirmed values (see `ValueTests`).

Negative direction is part of every surface: unsupported input must throw with
the promised message (`Assert.Throws` + message content), per the loud-failures
rule.
