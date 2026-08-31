#!/bin/bash
# Re-exports the typeprobe fixture TSVs from the oracle SQL Server (the same database
# state fixtures/typeprobe.bak was taken from). Usage:
#   tools/export-fixtures.sh <container-name> <sa-password>
set -eu
CONTAINER="${1:?container name}"; PASS="${2:?sa password}"
HERE="$(cd "$(dirname "$0")" && pwd)"; FIX="$HERE/../fixtures"
Q() { docker exec "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PASS" -C -f 65001 -y0 -d typeprobe -Q "SET NOCOUNT ON; $1" | grep -E '^[0-9]+\|' | sed 's/[[:space:]]*$//'; }
SEL="SELECT CONCAT(CAST(id AS varchar(max)),'|',ISNULL(CAST(c_tinyint AS varchar(5)),'NULL'),'|',ISNULL(CAST(c_smallint AS varchar(8)),'NULL'),'|',ISNULL(CAST(c_int AS varchar(12)),'NULL'),'|',ISNULL(CAST(c_bigint AS varchar(22)),'NULL'),'|',ISNULL(CAST(CAST(c_bit AS int) AS varchar(4)),'NULL'),'|',ISNULL(CONVERT(varchar(60),c_dec38_20),'NULL'),'|',ISNULL(CONVERT(varchar(30),c_dec18_2),'NULL'),'|',ISNULL(CONVERT(varchar(10),c_dec5_0),'NULL'),'|',ISNULL(CONVERT(varchar(30),c_datetime,121),'NULL'),'|',ISNULL(CONVERT(varchar(40),c_datetime2_7,121),'NULL'),'|',ISNULL(CONVERT(varchar(40),c_datetime2_3,121),'NULL'),'|',ISNULL(CONVERT(varchar(40),c_datetime2_0,121),'NULL'),'|',ISNULL(CONVERT(varchar(10),c_date,121),'NULL'),'|',ISNULL(CONVERT(varchar(20),c_time7,121),'NULL'),'|',ISNULL(CONVERT(varchar(10),c_time0,121),'NULL'),'|',ISNULL(CONVERT(varchar(36),c_guid),'NULL'),'|',ISNULL(c_nvarchar,N'NULL'),'|',ISNULL(c_varchar,'NULL'),'|',ISNULL(CAST(c_nchar AS nvarchar(10)),N'NULL'),'|',ISNULL(CAST(c_char AS varchar(10)),'NULL'),'|',CASE WHEN c_binary IS NULL THEN 'NULL' ELSE '0x'+CONVERT(varchar(20),c_binary,2) END,'|',CASE WHEN c_varbinary IS NULL THEN 'NULL' ELSE '0x'+CONVERT(varchar(220),c_varbinary,2) END,'|#') FROM"
Q "$SEL probe ORDER BY id" > "$FIX/typeprobe-probe.tsv"
Q "$SEL probe_row ORDER BY id" > "$FIX/typeprobe-probe-row.tsv"
Q "$SEL probe_page ORDER BY id" > "$FIX/typeprobe-probe-page.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',grp,'|',CONVERT(varchar(60),amount),'|',CONVERT(varchar(30),posted,121),'|',note,'|#') FROM probe_dense ORDER BY id" > "$FIX/typeprobe-probe-dense.tsv"
IMG="CASE WHEN c_image IS NULL THEN 'NULL' ELSE '0x'+CONVERT(varchar(max),CONVERT(varbinary(max),c_image),2) END"
VB="CASE WHEN c_vbmax IS NULL THEN 'NULL' ELSE '0x'+CONVERT(varchar(max),c_vbmax,2) END"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',$IMG,'|',ISNULL(CONVERT(varchar(max),c_text),'NULL'),'|',ISNULL(CONVERT(nvarchar(max),c_ntext),N'NULL'),'|',$VB,'|',ISNULL(c_nvmax,N'NULL'),'|#') FROM probe_lob ORDER BY id" > "$FIX/typeprobe-probe-lob.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',$IMG,'|',$VB,'|',ISNULL(c_nvmax,N'NULL'),'|#') FROM probe_lob_page ORDER BY id" > "$FIX/typeprobe-probe-lob-page.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',$IMG,'|',$VB,'|#') FROM probe_lob2 ORDER BY id" > "$FIX/typeprobe-probe-lob2.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',v1,'|',v2,'|',n1,'|#') FROM probe_overflow ORDER BY id" > "$FIX/typeprobe-probe-overflow.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',val,'|',CONVERT(varchar(30),amt),'|#') FROM probe_ghost ORDER BY id" > "$FIX/typeprobe-probe-ghost.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',ISNULL(CAST(c1 AS varchar(12)),'NULL'),'|',ISNULL(CAST(c100 AS varchar(12)),'NULL'),'|',ISNULL(CAST(c199 AS varchar(12)),'NULL'),'|',ISNULL(CAST(c200 AS varchar(12)),'NULL'),'|',ISNULL(wtext,N'NULL'),'|',ISNULL(CONVERT(varchar(30),wdec),'NULL'),'|#') FROM probe_wide ORDER BY id" > "$FIX/typeprobe-probe-wide.tsv"
ASEL="SELECT CONCAT(CAST(id AS varchar(max)),'|',ISNULL(b,N'NULL'),'|',ISNULL(CONVERT(varchar(30),d,121),'NULL'),'|',ISNULL(CAST(CAST(b1 AS int) AS varchar(4)),'NULL'),'|',ISNULL(CAST(CAST(b2 AS int) AS varchar(4)),'NULL'),'|',ISNULL(e,N'NULL'),'|',ISNULL(CAST(f AS varchar(12)),'NULL'),'|',ISNULL(CAST(CAST(b3 AS int) AS varchar(4)),'NULL')"
Q "$ASEL,'|',ISNULL(CONVERT(varchar(60),g),'NULL'),'|#') FROM probe_altered ORDER BY id" > "$FIX/typeprobe-probe-altered.tsv"
Q "$ASEL,'|#') FROM probe_altered_page ORDER BY id" > "$FIX/typeprobe-probe-altered-page.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',ISNULL(txt,N'NULL'),'|',ISNULL(CONVERT(varchar(30),amt),'NULL'),'|#') FROM probe_heap ORDER BY id" > "$FIX/typeprobe-probe-heap.tsv"
echo "typeprobe fixtures exported"
