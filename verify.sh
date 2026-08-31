#!/bin/bash
# Verification harness: decodes tables straight from the BC demo backups and compares
# byte-for-byte against fixtures exported from a real SQL Server RESTORE of the same file.
#
# Requires the ~900 MB backup files from the BC artifact cache. If they are absent the
# suite FAILS (exit 3) — it never silently passes.
set -u
BAK275="$HOME/.bcartifacts.cache/sandbox/27.5.46862.48827/w1/BusinessCentral-W1.bak"
BAK281="$HOME/.bcartifacts.cache/sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak"
HERE="$(cd "$(dirname "$0")" && pwd)"
BCBAK="$HERE/src/BcBak.Cli/bin/Release/net8.0/bcbak"

for f in "$BAK275" "$BAK281"; do
  if [ ! -f "$f" ]; then
    echo "MISSING BACKUP FILE: $f" >&2
    echo "The verification suite cannot run without the real backup files. FAILING (not skipping silently)." >&2
    exit 3
  fi
done

dotnet build "$HERE/BcBak.sln" -c Release -v q || exit 1
fail=0
run() { echo "--- $*"; "$BCBAK" "$@" || fail=1; }

run verify "$BAK275" --fixture "$HERE/fixtures/bc275-no-series.tsv"  --table "No. Series"  --select "Code,Description,Default Nos_,Manual Nos_,Date Order,\$systemId"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-customer.tsv"   --table "Customer"    --select "No.,Name,City,Post Code"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-gl-account.tsv" --table "G/L Account" --select "No.,Name,Account Type,Direct Posting"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-no-series.tsv"  --table "No. Series"  --company CRONUS --select "Code,Description,Default Nos_,Manual Nos_,Date Order,\$systemId"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-customer.tsv"   --table "Customer"    --company CRONUS --select "No.,Name,City,Post Code"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-gl-account.tsv" --table "G/L Account" --company CRONUS --select "No.,Name,Account Type,Direct Posting"

[ $fail -eq 0 ] && echo "ALL VERIFICATIONS PASSED" || echo "VERIFICATION FAILURES" >&2
exit $fail
