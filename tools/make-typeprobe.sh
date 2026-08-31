#!/bin/bash
# Regenerates fixtures/typeprobe.bak: a small scratch database exercising every SQL type
# and storage form the reader supports (uncompressed / row / page compression, LOBs,
# row overflow, SCSU text), backed up by a real SQL Server.
#
# Requires a running SQL Server container (see PROVENANCE.md "Sources"). Usage:
#   tools/make-typeprobe.sh <container-name> <sa-password>
# After regenerating the .bak, re-export the fixture TSVs with tools/export-fixtures.sh —
# the fixtures and the backup must come from the same database state.
set -eu
CONTAINER="${1:?container name}"; PASS="${2:?sa password}"
HERE="$(cd "$(dirname "$0")" && pwd)"
docker cp "$HERE/typeprobe.sql" "$CONTAINER:/tmp/typeprobe.sql"
docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PASS" -C -i /tmp/typeprobe.sql
docker cp "$CONTAINER:/tmp/typeprobe.bak" "$HERE/../fixtures/typeprobe.bak"
echo "fixtures/typeprobe.bak regenerated — now run tools/export-fixtures.sh"
