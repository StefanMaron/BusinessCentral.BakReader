#!/bin/bash
# Regenerates fixtures/typeprobe.bacpac: a sqlpackage export of the same `typeprobe`
# database fixtures/typeprobe.bak was taken from, so both fixtures and every
# fixtures/typeprobe-probe*.tsv describe one database state.
#
# Run it AFTER tools/make-typeprobe.sh (which rebuilds the database) and before
# tools/export-fixtures.sh, or in any order — none of them change the data.
#
# Needs sqlpackage on the host (`dotnet tool install -g microsoft.sqlpackage`) and a
# TCP-reachable oracle; the container publishes 1433 on host port 14330. Usage:
#   tools/make-typeprobe-bacpac.sh <sa-password> [server]
set -eu
# sqlpackage is a .NET tool: under a locale .NET has no culture for (en_DK on the
# machine this was first hit on) it dies with "4096 (0x1000) is an invalid culture
# identifier" *after* writing a truncated bacpac, so the failure looks like a smaller
# but valid file. Pin a culture .NET always has.
export LANG=C.UTF-8 LC_ALL=C.UTF-8
PASS="${1:?sa password}"; SERVER="${2:-localhost,14330}"
HERE="$(cd "$(dirname "$0")" && pwd)"; OUT="$HERE/../fixtures/typeprobe.bacpac"
command -v sqlpackage >/dev/null || { echo "sqlpackage not on PATH: dotnet tool install -g microsoft.sqlpackage" >&2; exit 3; }
rm -f "$OUT"
sqlpackage /Action:Export /SourceServerName:"$SERVER" /SourceDatabaseName:typeprobe \
  /SourceUser:sa /SourcePassword:"$PASS" /SourceTrustServerCertificate:True \
  /TargetFile:"$OUT" /OverwriteFiles:True
echo "fixtures/typeprobe.bacpac regenerated"
