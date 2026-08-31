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
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',ISNULL(CONVERT(varchar(36),g),'NULL'),'|',ISNULL(txt,N'NULL'),'|',ISNULL(CONVERT(varchar(30),amt),'NULL'),'|#') FROM probe_tracked ORDER BY id" > "$FIX/typeprobe-probe-tracked.tsv"
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',$IMG,'|',ISNULL(CONVERT(varchar(max),c_text),'NULL'),'|#') FROM probe_lob_upd ORDER BY id" > "$FIX/typeprobe-probe-lob-upd.tsv"
# probe_notnull: every type as NOT NULL. n_real/n_float are exercised by the framing but
# left out here — SQL Server's float-to-string form is not .NET's round-trip form; the
# tests assert those two columns directly against exact literals instead.
Q "SELECT CONCAT(CAST(id AS varchar(max)),'|',CAST(n_tinyint AS varchar(5)),'|',CAST(n_smallint AS varchar(8)),'|',CAST(n_int AS varchar(12)),'|',CAST(n_bigint AS varchar(22)),'|',CAST(CAST(n_bit AS int) AS varchar(4)),'|',CONVERT(varchar(60),n_dec38_20),'|',CONVERT(varchar(30),n_dec18_2),'|',CONVERT(varchar(10),n_dec5_0),'|',CONVERT(varchar(30),n_datetime,121),'|',CONVERT(varchar(40),n_datetime2_7,121),'|',CONVERT(varchar(40),n_datetime2_0,121),'|',CONVERT(varchar(10),n_date,121),'|',CONVERT(varchar(20),n_time7,121),'|',CONVERT(varchar(10),n_time0,121),'|',CONVERT(varchar(36),n_guid),'|',n_nvarchar,'|',n_varchar,'|',CAST(n_nchar AS nvarchar(10)),'|',CAST(n_char AS varchar(10)),'|','0x'+CONVERT(varchar(20),n_binary,2),'|','0x'+CONVERT(varchar(220),n_varbinary,2),'|','0x'+CONVERT(varchar(max),n_vbmax,2),'|',n_nvmax,'|','0x'+CONVERT(varchar(20),CONVERT(varbinary(8),n_ver),2),'|#') FROM probe_notnull ORDER BY id" > "$FIX/typeprobe-probe-notnull.tsv"
Q "SELECT CONCAT(CAST(b.id AS varchar(max)),'|',ISNULL(b.own,N'NULL'),'|',ISNULL(e.[extra\$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb],N'NULL'),'|',ISNULL(CAST(e.[num\$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb] AS varchar(12)),'NULL'),'|#') FROM [TP\$exttest\$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa] b LEFT JOIN [TP\$exttest\$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\$ext] e ON e.id = b.id ORDER BY b.id" > "$FIX/typeprobe-probe-exttest-merged.tsv"
echo "typeprobe fixtures exported"
