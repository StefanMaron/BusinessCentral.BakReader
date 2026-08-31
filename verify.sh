#!/bin/bash
# Full-file verification: decodes tables straight from real backups and compares with
# fixtures exported from a real SQL Server (the oracle) — see PROVENANCE.md.
#
# Two tiers:
#  * typeprobe tests run against fixtures/typeprobe.bak (committed) — always run.
#  * BC demo tests need the ~900 MB backups from the BC artifact cache. If those files
#    are absent the suite FAILS (exit 3) — it never skips silently. CI covers the
#    typeprobe tier via `dotnet test`; this script is the full local gate.
set -u
BAK275="$HOME/.bcartifacts.cache/sandbox/27.5.46862.48827/w1/BusinessCentral-W1.bak"
BAK281="$HOME/.bcartifacts.cache/sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak"
HERE="$(cd "$(dirname "$0")" && pwd)"
BCBAK="$HERE/src/BcBak.Cli/bin/Release/net8.0/bcbak"
TP="$HERE/fixtures/typeprobe.bak"

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

# --- structural cross-check: the page map must be internally consistent on every file
run check "$BAK275" > /dev/null
run check "$BAK281" > /dev/null
run check "$TP" > /dev/null

# --- typeprobe: every supported type and storage form, against oracle SELECT output
PSEL="id,c_tinyint,c_smallint,c_int,c_bigint,c_bit,c_dec38_20,c_dec18_2,c_dec5_0,c_datetime,c_datetime2_7,c_datetime2_3,c_datetime2_0,c_date,c_time7,c_time0,c_guid,c_nvarchar,c_varchar,c_nchar,c_char,c_binary,c_varbinary"
run verify "$TP" --table probe          --fixture "$HERE/fixtures/typeprobe-probe.tsv"          --select "$PSEL"
run verify "$TP" --table probe_row      --fixture "$HERE/fixtures/typeprobe-probe-row.tsv"      --select "$PSEL"
run verify "$TP" --table probe_page     --fixture "$HERE/fixtures/typeprobe-probe-page.tsv"     --select "$PSEL"
run verify "$TP" --table probe_dense    --fixture "$HERE/fixtures/typeprobe-probe-dense.tsv"    --select "id,grp,amount,posted,note"
run verify "$TP" --table probe_lob      --fixture "$HERE/fixtures/typeprobe-probe-lob.tsv"      --select "id,c_image,c_text,c_ntext,c_vbmax,c_nvmax"
run verify "$TP" --table probe_lob_page --fixture "$HERE/fixtures/typeprobe-probe-lob-page.tsv" --select "id,c_image,c_vbmax,c_nvmax"
run verify "$TP" --table probe_lob2     --fixture "$HERE/fixtures/typeprobe-probe-lob2.tsv"     --select "id,c_image,c_vbmax"
run verify "$TP" --table probe_overflow --fixture "$HERE/fixtures/typeprobe-probe-overflow.tsv" --select "id,v1,v2,n1"
run verify "$TP" --table probe_ghost    --fixture "$HERE/fixtures/typeprobe-probe-ghost.tsv"    --select "id,val,amt"
run verify "$TP" --table probe_wide     --fixture "$HERE/fixtures/typeprobe-probe-wide.tsv"     --select "id,c1,c100,c199,c200,wtext,wdec"
run verify "$TP" --table probe_altered  --fixture "$HERE/fixtures/typeprobe-probe-altered.tsv"  --select "id,b,d,b1,b2,e,f,b3,g"
run verify "$TP" --table probe_altered_page --fixture "$HERE/fixtures/typeprobe-probe-altered-page.tsv" --select "id,b,d,b1,b2,e,f,b3"
run verify "$TP" --table probe_heap     --fixture "$HERE/fixtures/typeprobe-probe-heap.tsv"     --select "id,txt,amt"
run verify "$TP" --table probe_tracked  --fixture "$HERE/fixtures/typeprobe-probe-tracked.tsv"  --select "id,g,txt,amt"

# --- BC demo databases, both shipped versions
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-no-series.tsv"  --table "No. Series"  --select "Code,Description,Default Nos_,Manual Nos_,Date Order,\$systemId"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-customer.tsv"   --table "Customer"    --select "No.,Name,City,Post Code"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-gl-account.tsv" --table "G/L Account" --select "No.,Name,Account Type,Direct Posting"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-gl-entry.tsv"   --table "G/L Entry"   --select "Entry No.,G/L Account No.,Posting Date,Amount,Description,\$systemCreatedAt"
run verify "$BAK275" --fixture "$HERE/fixtures/bc275-tenant-media.tsv" --table "Tenant Media" --select "ID,Content" --sha256 "Content"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-no-series.tsv"  --table "No. Series"  --company CRONUS --select "Code,Description,Default Nos_,Manual Nos_,Date Order,\$systemId"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-customer.tsv"   --table "Customer"    --company CRONUS --select "No.,Name,City,Post Code"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-gl-account.tsv" --table "G/L Account" --company CRONUS --select "No.,Name,Account Type,Direct Posting"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-gl-entry.tsv"   --table "G/L Entry"   --company CRONUS --select "Entry No.,G/L Account No.,Posting Date,Amount,Description,\$systemCreatedAt"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-tenant-media.tsv" --table "Tenant Media" --select "ID,Content" --sha256 "Content"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-gen-journal-line.tsv" --table "Gen. Journal Line" --company CRONUS --select "Journal Template Name,Journal Batch Name,Line No.,Account No.,Amount,Description,\$systemId"
# legacy LOB update history: SMALL_ROOT records with a rewritten value, and text
# pointers to type-8 NULL roots (GitHub issues #7 and #8)
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-application-object-metadata.tsv" --table "Application Object Metadata" --select "Runtime Package ID,Object Type,Object ID,User Code" --sha256 "User Code"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-tenant-web-service-odata.tsv" --table "Tenant Web Service OData" --select "\$systemId,ODataSelectClause,ODataFilterClause,ODataV4FilterClause" --sha256 "ODataSelectClause,ODataFilterClause,ODataV4FilterClause"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-ndo-dbproperty.tsv" --table "\$ndo\$dbproperty" --select "databaseversionno,chartable,license" --sha256 "chartable,license"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-ndo-environmentproperty.tsv" --table "\$ndo\$environmentproperty" --select "propertykey,propertyvalue"
# change-tracked platform tables: change tracking adds an internal in-row version
# column that previously shadowed "Runtime Package ID" (GitHub issue #6)
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-published-application.tsv" --table "Published Application" --select "Runtime Package ID,Package ID,Name,Publisher,Version Major"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-installed-application.tsv" --table "Installed Application" --select "Runtime Package ID,Package ID,\$systemId"
# multi-company: the second (My Company) company of the 28.1 demo database
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-mycompany-no-series.tsv" --table "No. Series" --company "My Company" --select "Code,Description,\$systemCreatedAt,\$systemId"
run verify "$BAK281" --fixture "$HERE/fixtures/bc281-mycompany-data-exch-column-def.tsv" --table "Data Exch. Column Def" --company "My Company" --select "Data Exch. Def Code,Data Exch. Line Def Code,Column No.,Name,Length"

[ $fail -eq 0 ] && echo "ALL VERIFICATIONS PASSED" || echo "VERIFICATION FAILURES" >&2
exit $fail
