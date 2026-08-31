IF DB_ID('typeprobe') IS NOT NULL BEGIN ALTER DATABASE typeprobe SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE typeprobe; END;
CREATE DATABASE typeprobe ON (NAME='typeprobe_data', FILENAME='/var/opt/mssql/data/typeprobe.mdf', SIZE=16MB, FILEGROWTH=8MB)
LOG ON (NAME='typeprobe_log', FILENAME='/var/opt/mssql/data/typeprobe.ldf', SIZE=8MB);
GO
USE typeprobe;
GO
CREATE TABLE probe (
  id int NOT NULL,
  c_tinyint tinyint NULL,
  c_smallint smallint NULL,
  c_int int NULL,
  c_bigint bigint NULL,
  c_bit bit NULL,
  c_dec38_20 decimal(38,20) NULL,
  c_dec18_2 decimal(18,2) NULL,
  c_dec5_0 decimal(5,0) NULL,
  c_datetime datetime NULL,
  c_datetime2_7 datetime2(7) NULL,
  c_datetime2_3 datetime2(3) NULL,
  c_datetime2_0 datetime2(0) NULL,
  c_date date NULL,
  c_time7 time(7) NULL,
  c_time0 time(0) NULL,
  c_guid uniqueidentifier NULL,
  c_nvarchar nvarchar(100) NULL,
  c_varchar varchar(100) NULL,
  c_nchar nchar(10) NULL,
  c_char char(10) NULL,
  c_binary binary(8) NULL,
  c_varbinary varbinary(100) NULL,
  c_real real NULL,
  c_float float NULL,
  CONSTRAINT pk_probe PRIMARY KEY CLUSTERED (id)
);
GO
INSERT INTO probe (id, c_tinyint, c_smallint, c_int, c_bigint, c_bit, c_dec38_20, c_dec18_2, c_dec5_0, c_datetime, c_datetime2_7, c_datetime2_3, c_datetime2_0, c_date, c_time7, c_time0, c_guid, c_nvarchar, c_varchar, c_nchar, c_char, c_binary, c_varbinary, c_real, c_float) VALUES
 (1, 0, 0, 0, 0, 0, 0, 0, 0, '1900-01-01 00:00:00.000', '1900-01-01 00:00:00', '1900-01-01', '1900-01-01', '1900-01-01', '00:00:00', '00:00:00', '00000000-0000-0000-0000-000000000000', N'', '', N'', '', 0x0000000000000000, 0x, 0, 0)
,(2, 255, 32767, 2147483647, 9223372036854775807, 1, 99999999999999999.99999999999999999999, 9999999999999999.99, 99999, '9999-12-31 23:59:59.997', '9999-12-31 23:59:59.9999999', '9999-12-31 23:59:59.999', '9999-12-31 23:59:59', '9999-12-31', '23:59:59.9999999', '23:59:59', 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF', N'Hello World', 'Hello', N'abc', 'xyz', 0x0102030405060708, 0xDEADBEEF, 1.5, 2.25)
,(3, 1, -32768, -2147483648, -9223372036854775808, 1, -99999999999999999.99999999999999999999, -9999999999999999.99, -99999, '1753-01-01 00:00:00.000', '0001-01-01 00:00:00', '0001-01-01', '0001-01-01', '0001-01-01', '13:14:15.1234567', '13:14:15', '12345678-9ABC-DEF0-1234-56789ABCDEF0', N'Ærøskøbing über café', 'plain ascii', N'ÆØÅ', 'abc', 0xFFFFFFFFFFFFFFFF, 0x00, -1.5, -2.25)
,(4, 7, 1, 1, 1, 0, 1, 1, 1, '2026-08-31 12:34:56.789', '2026-08-31 12:34:56.7890123', '2026-08-31 12:34:56.789', '2026-08-31 12:34:57', '2026-08-31', '12:34:56.7890123', '12:34:57', NEWID(), N'Кириллица тест', 'row four', N'test', 'test', 0x0000000000000001, 0x0102, 3.14159, 2.718281828459045)
,(5, 42, -1, -1, -1, 1, 0.00000000000000000001, 0.01, 2, '2000-02-29 06:00:00.000', '2000-02-29 06:00:00.0000001', '2000-02-29 06:00:00.001', '2000-02-29 06:00:00', '2000-02-29', '06:00:00.0000001', '06:00:00', NEWID(), N'Ελληνικά και 中文字 and 🎉 emoji', 'x', N'ab', 'cd', 0x8000000000000000, 0xFF, 1e-30, 1e-300)
,(6, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
,(7, 128, 256, 65536, 4294967296, 0, 123456.789012345678901234567890, 12345678.90, 12345, '1999-12-31 23:59:59.997', '2024-01-15 08:30:45.1234567', '2024-01-15 08:30:45.123', '2024-01-15 08:30:45', '2024-01-15', '23:59:59.0000001', '01:02:03', 'A1B2C3D4-E5F6-4789-ABCD-EF0123456789', N'æøå ÆØÅ é è ü ö ä ß', 'high ascii', N'nc', 'ch', 0x7FFFFFFFFFFFFFFF, 0x0000FF, 100.5, -100.5)
,(8, 9, 300, -42, 1234567890123, 1, -0.5, -0.99, -3, '1980-06-15 18:45:30.123', '1980-06-15 18:45:30.1230000', '1980-06-15 18:45:30.123', '1980-06-15 18:45:30', '1980-06-15', '18:45:30.5000000', '18:45:31', '00000000-0000-0000-0000-000000000001', N'ab', 'a', N'x', 'y', 0x0000000000000080, 0xA5, 0.001, 0.001);
GO
-- three compression variants of the same data
SELECT * INTO probe_row FROM probe;
ALTER TABLE probe_row ADD CONSTRAINT pk_probe_row PRIMARY KEY CLUSTERED (id);
ALTER TABLE probe_row REBUILD WITH (DATA_COMPRESSION = ROW);
SELECT * INTO probe_page FROM probe;
ALTER TABLE probe_page ADD CONSTRAINT pk_probe_page PRIMARY KEY CLUSTERED (id);
ALTER TABLE probe_page REBUILD WITH (DATA_COMPRESSION = PAGE);
GO
-- page compression that actually engages: repetitive data filling pages
CREATE TABLE probe_dense (
  id int NOT NULL,
  grp nvarchar(40) NOT NULL,
  amount decimal(38,20) NOT NULL,
  posted datetime NOT NULL,
  note nvarchar(60) NOT NULL,
  CONSTRAINT pk_probe_dense PRIMARY KEY CLUSTERED (id)
) WITH (DATA_COMPRESSION = PAGE);
GO
;WITH n AS (SELECT TOP 4000 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.objects a CROSS JOIN sys.objects b)
INSERT INTO probe_dense
SELECT i,
  N'GROUP-' + CAST(i % 7 AS nvarchar(10)),
  CAST(i AS decimal(38,20)) / 3.0,
  DATEADD(SECOND, i * 17, '2026-01-01'),
  N'Заметка номер ' + CAST(i AS nvarchar(10)) + N' 测试 é'
FROM n;
GO
-- LOB storage: legacy image/text (BC blobs) and varbinary(max)/nvarchar(max)
CREATE TABLE probe_lob (
  id int NOT NULL,
  c_image image NULL,
  c_text text NULL,
  c_ntext ntext NULL,
  c_vbmax varbinary(max) NULL,
  c_nvmax nvarchar(max) NULL,
  CONSTRAINT pk_probe_lob PRIMARY KEY CLUSTERED (id)
);
GO
DECLARE @big varbinary(max) = 0x;
DECLARE @i int = 0;
WHILE @i < 20000 BEGIN SET @big = @big + CONVERT(varbinary(8), @i) ; SET @i = @i + 1; END; -- 160,000 bytes, deterministic
DECLARE @bigtext nvarchar(max) = REPLICATE(CONVERT(nvarchar(max), N'Lorem ipsum dolor sit amet 中文 '), 3000); -- ~90,000 chars
INSERT INTO probe_lob VALUES
 (1, 0x, '', N'', 0x, N'')
,(2, 0x01020304, 'small text', N'small ntext', 0xAABBCCDD, N'small nvmax')
,(3, CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max),'ABCDEFGH'), 700)), REPLICATE(CONVERT(varchar(max),'txt45678'), 700), REPLICATE(CONVERT(nvarchar(max),N'ntx45678'), 350), CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max),'VBMX5678'), 700)), REPLICATE(CONVERT(nvarchar(max),N'nvmx5678'), 350))  -- ~5600 bytes each: single LOB page
,(4, @big, NULL, NULL, @big, @bigtext)  -- multi-page LOB tree
,(5, NULL, NULL, NULL, NULL, NULL);
GO
-- row-overflow: in-row varchars pushed off-row when the row exceeds 8060 bytes
CREATE TABLE probe_overflow (
  id int NOT NULL,
  v1 varchar(8000) NOT NULL,
  v2 varchar(8000) NOT NULL,
  n1 nvarchar(4000) NOT NULL,
  CONSTRAINT pk_probe_overflow PRIMARY KEY CLUSTERED (id)
);
INSERT INTO probe_overflow VALUES
 (1, REPLICATE('a', 20), REPLICATE('b', 30), N'inrow'),
 (2, REPLICATE('c', 7000), REPLICATE('d', 7000), REPLICATE(N'e', 3000));
GO
-- compressed variants of LOB-bearing tables (BC tables are page-compressed and have image columns)
SELECT id, c_image, c_vbmax, c_nvmax INTO probe_lob_page FROM probe_lob;
ALTER TABLE probe_lob_page ADD CONSTRAINT pk_probe_lob_page PRIMARY KEY CLUSTERED (id);
ALTER TABLE probe_lob_page REBUILD WITH (DATA_COMPRESSION = PAGE);
GO
IF OBJECT_ID('probe_lob2') IS NOT NULL DROP TABLE probe_lob2;
CREATE TABLE probe_lob2 (id int NOT NULL, c_image image NULL, c_vbmax varbinary(max) NULL, CONSTRAINT pk_probe_lob2 PRIMARY KEY CLUSTERED (id));
DECLARE @k varbinary(max) = CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max),'0123456789ABCDEF'), 256)); -- 4096 bytes
INSERT INTO probe_lob2 VALUES
 (1, CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'X'), 12000)), CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'Y'), 12000)))
,(2, CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'M'), 20000)), CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'N'), 20000)))
,(3, CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'P'), 32000)), CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'Q'), 32000)))
,(4, CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'R'), 60000)), CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'S'), 60000)));
CHECKPOINT;
SELECT allocated_page_page_id, page_type FROM sys.dm_db_database_page_allocations(DB_ID('typeprobe'),OBJECT_ID('probe_lob2'),NULL,NULL,'DETAILED') WHERE page_type=1;
GO
CHECKPOINT;
GO
-- >127 columns: exercises the two-byte CD column-count form
IF OBJECT_ID('probe_wide') IS NOT NULL DROP TABLE probe_wide;
CREATE TABLE probe_wide (id int NOT NULL, c1 int NULL, c2 int NULL, c3 int NULL, c4 int NULL, c5 int NULL, c6 int NULL, c7 int NULL, c8 int NULL, c9 int NULL, c10 int NULL, c11 int NULL, c12 int NULL, c13 int NULL, c14 int NULL, c15 int NULL, c16 int NULL, c17 int NULL, c18 int NULL, c19 int NULL, c20 int NULL, c21 int NULL, c22 int NULL, c23 int NULL, c24 int NULL, c25 int NULL, c26 int NULL, c27 int NULL, c28 int NULL, c29 int NULL, c30 int NULL, c31 int NULL, c32 int NULL, c33 int NULL, c34 int NULL, c35 int NULL, c36 int NULL, c37 int NULL, c38 int NULL, c39 int NULL, c40 int NULL, c41 int NULL, c42 int NULL, c43 int NULL, c44 int NULL, c45 int NULL, c46 int NULL, c47 int NULL, c48 int NULL, c49 int NULL, c50 int NULL, c51 int NULL, c52 int NULL, c53 int NULL, c54 int NULL, c55 int NULL, c56 int NULL, c57 int NULL, c58 int NULL, c59 int NULL, c60 int NULL, c61 int NULL, c62 int NULL, c63 int NULL, c64 int NULL, c65 int NULL, c66 int NULL, c67 int NULL, c68 int NULL, c69 int NULL, c70 int NULL, c71 int NULL, c72 int NULL, c73 int NULL, c74 int NULL, c75 int NULL, c76 int NULL, c77 int NULL, c78 int NULL, c79 int NULL, c80 int NULL, c81 int NULL, c82 int NULL, c83 int NULL, c84 int NULL, c85 int NULL, c86 int NULL, c87 int NULL, c88 int NULL, c89 int NULL, c90 int NULL, c91 int NULL, c92 int NULL, c93 int NULL, c94 int NULL, c95 int NULL, c96 int NULL, c97 int NULL, c98 int NULL, c99 int NULL, c100 int NULL, c101 int NULL, c102 int NULL, c103 int NULL, c104 int NULL, c105 int NULL, c106 int NULL, c107 int NULL, c108 int NULL, c109 int NULL, c110 int NULL, c111 int NULL, c112 int NULL, c113 int NULL, c114 int NULL, c115 int NULL, c116 int NULL, c117 int NULL, c118 int NULL, c119 int NULL, c120 int NULL, c121 int NULL, c122 int NULL, c123 int NULL, c124 int NULL, c125 int NULL, c126 int NULL, c127 int NULL, c128 int NULL, c129 int NULL, c130 int NULL, c131 int NULL, c132 int NULL, c133 int NULL, c134 int NULL, c135 int NULL, c136 int NULL, c137 int NULL, c138 int NULL, c139 int NULL, c140 int NULL, c141 int NULL, c142 int NULL, c143 int NULL, c144 int NULL, c145 int NULL, c146 int NULL, c147 int NULL, c148 int NULL, c149 int NULL, c150 int NULL, c151 int NULL, c152 int NULL, c153 int NULL, c154 int NULL, c155 int NULL, c156 int NULL, c157 int NULL, c158 int NULL, c159 int NULL, c160 int NULL, c161 int NULL, c162 int NULL, c163 int NULL, c164 int NULL, c165 int NULL, c166 int NULL, c167 int NULL, c168 int NULL, c169 int NULL, c170 int NULL, c171 int NULL, c172 int NULL, c173 int NULL, c174 int NULL, c175 int NULL, c176 int NULL, c177 int NULL, c178 int NULL, c179 int NULL, c180 int NULL, c181 int NULL, c182 int NULL, c183 int NULL, c184 int NULL, c185 int NULL, c186 int NULL, c187 int NULL, c188 int NULL, c189 int NULL, c190 int NULL, c191 int NULL, c192 int NULL, c193 int NULL, c194 int NULL, c195 int NULL, c196 int NULL, c197 int NULL, c198 int NULL, c199 int NULL, c200 int NULL, wtext nvarchar(50) NULL, wdec decimal(18,2) NULL,
  CONSTRAINT pk_probe_wide PRIMARY KEY CLUSTERED (id)) WITH (DATA_COMPRESSION = PAGE);
INSERT INTO probe_wide VALUES (1, 3, 6, 9, 12, 15, 18, 21, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60, 63, 66, 69, 72, 75, 78, 81, 84, 87, 90, 93, 96, 99, 102, 105, 108, 111, 114, 117, 120, 123, 126, 129, 132, 135, 138, 141, 144, 147, 150, 153, 156, 159, 162, 165, 168, 171, 174, 177, 180, 183, 186, 189, 192, 195, 198, 201, 204, 207, 210, 213, 216, 219, 222, 225, 228, 231, 234, 237, 240, 243, 246, 249, 252, 255, 258, 261, 264, 267, 270, 273, 276, 279, 282, 285, 288, 291, 294, 297, 300, 303, 306, 309, 312, 315, 318, 321, 324, 327, 330, 333, 336, 339, 342, 345, 348, 351, 354, 357, 360, 363, 366, 369, 372, 375, 378, 381, 384, 387, 390, 393, 396, 399, 402, 405, 408, 411, 414, 417, 420, 423, 426, 429, 432, 435, 438, 441, 444, 447, 450, 453, 456, 459, 462, 465, 468, 471, 474, 477, 480, 483, 486, 489, 492, 495, 498, 501, 504, 507, 510, 513, 516, 519, 522, 525, 528, 531, 534, 537, 540, 543, 546, 549, 552, 555, 558, 561, 564, 567, 570, 573, 576, 579, 582, 585, 588, 591, 594, 597, 600, N'wide-one', 12.34);
INSERT INTO probe_wide VALUES (2, NULL, -2, NULL, -4, NULL, -6, NULL, -8, NULL, -10, NULL, -12, NULL, -14, NULL, -16, NULL, -18, NULL, -20, NULL, -22, NULL, -24, NULL, -26, NULL, -28, NULL, -30, NULL, -32, NULL, -34, NULL, -36, NULL, -38, NULL, -40, NULL, -42, NULL, -44, NULL, -46, NULL, -48, NULL, -50, NULL, -52, NULL, -54, NULL, -56, NULL, -58, NULL, -60, NULL, -62, NULL, -64, NULL, -66, NULL, -68, NULL, -70, NULL, -72, NULL, -74, NULL, -76, NULL, -78, NULL, -80, NULL, -82, NULL, -84, NULL, -86, NULL, -88, NULL, -90, NULL, -92, NULL, -94, NULL, -96, NULL, -98, NULL, -100, NULL, -102, NULL, -104, NULL, -106, NULL, -108, NULL, -110, NULL, -112, NULL, -114, NULL, -116, NULL, -118, NULL, -120, NULL, -122, NULL, -124, NULL, -126, NULL, -128, NULL, -130, NULL, -132, NULL, -134, NULL, -136, NULL, -138, NULL, -140, NULL, -142, NULL, -144, NULL, -146, NULL, -148, NULL, -150, NULL, -152, NULL, -154, NULL, -156, NULL, -158, NULL, -160, NULL, -162, NULL, -164, NULL, -166, NULL, -168, NULL, -170, NULL, -172, NULL, -174, NULL, -176, NULL, -178, NULL, -180, NULL, -182, NULL, -184, NULL, -186, NULL, -188, NULL, -190, NULL, -192, NULL, -194, NULL, -196, NULL, -198, NULL, -200, NULL, -0.05);
INSERT INTO probe_wide VALUES (3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, N'ÆØÅ wide', 0);
GO
-- ALTER history: physical layout diverges from declaration order (the sysrscols path).
-- Every upgraded BC database has this shape: dropped columns keep their slots, added
-- columns land after them, bit columns share bytes, old rows keep their old column count.
IF OBJECT_ID('probe_altered') IS NOT NULL DROP TABLE probe_altered;
CREATE TABLE probe_altered (
  id int NOT NULL, a int NULL, b nvarchar(30) NULL, c decimal(18,2) NULL,
  d datetime NULL, b1 bit NULL, b2 bit NULL,
  CONSTRAINT pk_probe_altered PRIMARY KEY CLUSTERED (id));
INSERT INTO probe_altered VALUES (1, 11, N'one', 1.10, '2020-01-01', 1, 0), (2, 22, N'two', 2.20, '2020-02-02', 0, 1);
ALTER TABLE probe_altered DROP COLUMN a;
ALTER TABLE probe_altered DROP COLUMN c;
ALTER TABLE probe_altered ADD e nvarchar(20) NULL;
ALTER TABLE probe_altered ADD f int NULL;
ALTER TABLE probe_altered ADD b3 bit NULL;
ALTER TABLE probe_altered ADD g decimal(38,20) NULL;
INSERT INTO probe_altered (id,b,d,b1,b2,e,f,b3,g) VALUES (3, N'three', '2020-03-03', 1, 1, N'ee3', 33, 1, 3.00000000000000000003);
UPDATE probe_altered SET b = N'one-upd', e = N'ee1', b3 = 0 WHERE id = 1;
GO
IF OBJECT_ID('probe_altered_page') IS NOT NULL DROP TABLE probe_altered_page;
CREATE TABLE probe_altered_page (
  id int NOT NULL, a int NULL, b nvarchar(30) NULL, c decimal(18,2) NULL,
  d datetime NULL, b1 bit NULL, b2 bit NULL,
  CONSTRAINT pk_probe_altered_page PRIMARY KEY CLUSTERED (id)) WITH (DATA_COMPRESSION = PAGE);
INSERT INTO probe_altered_page VALUES (1, 11, N'one', 1.10, '2020-01-01', 1, 0), (2, 22, N'two', 2.20, '2020-02-02', 0, 1);
ALTER TABLE probe_altered_page DROP COLUMN a;
ALTER TABLE probe_altered_page DROP COLUMN c;
ALTER TABLE probe_altered_page ADD e nvarchar(20) NULL;
ALTER TABLE probe_altered_page ADD f int NULL;
ALTER TABLE probe_altered_page ADD b3 bit NULL;
INSERT INTO probe_altered_page (id,b,d,b1,b2,e,f,b3) VALUES (3, N'three', '2020-03-03', 1, 1, N'ee3', 33, 1);
UPDATE probe_altered_page SET b = N'one-upd', e = N'ee1', b3 = 0 WHERE id = 1;
GO
-- a heap (no clustered index) with churn: heap record paths, and delete/update history
-- that can leave empty (offset-0) slot-array entries
IF OBJECT_ID('probe_heap') IS NOT NULL DROP TABLE probe_heap;
CREATE TABLE probe_heap (id int NOT NULL, txt nvarchar(100) NULL, amt decimal(18,2) NULL);
;WITH n AS (SELECT TOP 400 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) i FROM sys.all_columns)
INSERT INTO probe_heap SELECT i, REPLICATE(N'h', 20 + i % 60), i / 4.0 FROM n;
DELETE FROM probe_heap WHERE id % 4 = 1;
UPDATE probe_heap SET txt = REPLICATE(N'W', 90) WHERE id % 7 = 0; -- grow rows: forces movement in a heap
CHECKPOINT;
GO
-- Base table + $ext companion: the BC table-extension storage shape (GitHub #12).
-- Extension fields live in a companion table named <company>$<table>$<appid>$ext,
-- each column suffixed with the EXTENDING app's id. Base row 3 has no companion
-- row: a merged read must yield NULL extension fields for it.
IF OBJECT_ID('[TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa]') IS NOT NULL DROP TABLE [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa];
CREATE TABLE [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa] (
  id int NOT NULL, own nvarchar(20) NULL,
  CONSTRAINT [pk_tp_exttest] PRIMARY KEY CLUSTERED (id));
INSERT INTO [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa] VALUES
  (1, N'base-one'), (2, N'base-two'), (3, N'base-three');
IF OBJECT_ID('[TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext]') IS NOT NULL DROP TABLE [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext];
CREATE TABLE [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext] (
  id int NOT NULL,
  [extra$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb] nvarchar(20) NULL,
  [num$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb] int NULL,
  CONSTRAINT [pk_tp_exttest_ext] PRIMARY KEY CLUSTERED (id));
INSERT INTO [TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext] VALUES
  (1, N'ext-one', 11), (2, NULL, 22);
GO
-- Two apps defining the same table name in the same company (legal via AL
-- namespaces; Microsoft's own demo database ships Dimension Set Entry twice) —
-- the app id suffix is the only distinguishing part, selectable with --app
-- (GitHub issue #13).
IF OBJECT_ID('[ProbeCo$ambig$11111111-1111-1111-1111-111111111111]') IS NOT NULL DROP TABLE [ProbeCo$ambig$11111111-1111-1111-1111-111111111111];
CREATE TABLE [ProbeCo$ambig$11111111-1111-1111-1111-111111111111] (id int NOT NULL, v nvarchar(20) NULL,
  CONSTRAINT [pk_ambig_1] PRIMARY KEY CLUSTERED (id));
INSERT INTO [ProbeCo$ambig$11111111-1111-1111-1111-111111111111] VALUES (1, N'from-app-one');
IF OBJECT_ID('[ProbeCo$ambig$22222222-2222-2222-2222-222222222222]') IS NOT NULL DROP TABLE [ProbeCo$ambig$22222222-2222-2222-2222-222222222222];
CREATE TABLE [ProbeCo$ambig$22222222-2222-2222-2222-222222222222] (id int NOT NULL, v nvarchar(20) NULL,
  CONSTRAINT [pk_ambig_2] PRIMARY KEY CLUSTERED (id));
INSERT INTO [ProbeCo$ambig$22222222-2222-2222-2222-222222222222] VALUES (1, N'from-app-two');
GO
-- A platform-style table whose SQL name starts with '$' (like the BC platform's
-- $ndo$... tables): the <company>$<table>$<appid> name parsing must fall back to
-- the raw object name, not an empty string (GitHub issue #14).
IF OBJECT_ID('[$probe$platform]') IS NOT NULL DROP TABLE [$probe$platform];
CREATE TABLE [$probe$platform] (id int NOT NULL, v nvarchar(20) NULL,
  CONSTRAINT [pk_$probe$platform] PRIMARY KEY CLUSTERED (id));
INSERT INTO [$probe$platform] VALUES (1, N'platform-one'), (2, NULL);
GO
-- LOB update history: rewriting a legacy text/image value bumps a word in the
-- SMALL_ROOT record header (observed 1 on an updated production row where every
-- freshly-written probe record has 0 — the reader must not fuse it into the size),
-- and updating a value to NULL can leave a text pointer to a type-8 (NULL) root
-- record instead of clearing the in-row cell. Reproduces GitHub issues #7 and #8:
-- $ndo$environmentproperty / $ndo$dbproperty / Application Object Metadata.
IF OBJECT_ID('probe_lob_upd') IS NOT NULL DROP TABLE probe_lob_upd;
CREATE TABLE probe_lob_upd (id int NOT NULL, c_image image NULL, c_text text NULL,
  CONSTRAINT pk_probe_lob_upd PRIMARY KEY CLUSTERED (id));
INSERT INTO probe_lob_upd VALUES (1, 0x0102030405, 'first small'), (2, 0xAA, 'to-null'),
  (3, NULL, NULL), (4, 0xBB, 'stays');
UPDATE probe_lob_upd SET c_text = 'updated small text' WHERE id = 1;
UPDATE probe_lob_upd SET c_image = 0x99887766, c_text = NULL WHERE id = 2;
GO
-- Every supported type as NOT NULL, plus a rowversion. Nullability is invisible in the
-- .bak record (the null bitmap has a bit either way) but decides the prefix width of a
-- native BCP field in a .bacpac: a non-nullable fixed-length column is written raw with
-- no length prefix, while uniqueidentifier, decimal and rowversion carry one regardless.
-- Chosen values only, so the framing rule is derived and not inferred (GitHub issue #3).
IF OBJECT_ID('probe_notnull') IS NOT NULL DROP TABLE probe_notnull;
CREATE TABLE probe_notnull (
  id int NOT NULL,
  n_tinyint tinyint NOT NULL,
  n_smallint smallint NOT NULL,
  n_int int NOT NULL,
  n_bigint bigint NOT NULL,
  n_bit bit NOT NULL,
  n_dec38_20 decimal(38,20) NOT NULL,
  n_dec18_2 decimal(18,2) NOT NULL,
  n_dec5_0 decimal(5,0) NOT NULL,
  n_datetime datetime NOT NULL,
  n_datetime2_7 datetime2(7) NOT NULL,
  n_datetime2_0 datetime2(0) NOT NULL,
  n_date date NOT NULL,
  n_time7 time(7) NOT NULL,
  n_time0 time(0) NOT NULL,
  n_guid uniqueidentifier NOT NULL,
  n_nvarchar nvarchar(100) NOT NULL,
  n_varchar varchar(100) NOT NULL,
  n_nchar nchar(10) NOT NULL,
  n_char char(10) NOT NULL,
  n_binary binary(8) NOT NULL,
  n_varbinary varbinary(100) NOT NULL,
  n_real real NOT NULL,
  n_float float NOT NULL,
  n_vbmax varbinary(max) NOT NULL,
  n_nvmax nvarchar(max) NOT NULL,
  n_ver rowversion NOT NULL,
  CONSTRAINT pk_probe_notnull PRIMARY KEY CLUSTERED (id));
INSERT INTO probe_notnull (id, n_tinyint, n_smallint, n_int, n_bigint, n_bit, n_dec38_20, n_dec18_2, n_dec5_0, n_datetime, n_datetime2_7, n_datetime2_0, n_date, n_time7, n_time0, n_guid, n_nvarchar, n_varchar, n_nchar, n_char, n_binary, n_varbinary, n_real, n_float, n_vbmax, n_nvmax) VALUES
 (1, 0, 0, 0, 0, 0, 0, 0, 0, '1900-01-01 00:00:00.000', '1900-01-01 00:00:00', '1900-01-01', '1900-01-01', '00:00:00', '00:00:00', '00000000-0000-0000-0000-000000000000', N'', '', N'', '', 0x0000000000000000, 0x, 0, 0, 0x, N'')
,(2, 255, 32767, 2147483647, 9223372036854775807, 1, 99999999999999999.99999999999999999999, 9999999999999999.99, 99999, '9999-12-31 23:59:59.997', '9999-12-31 23:59:59.9999999', '9999-12-31 23:59:59', '9999-12-31', '23:59:59.9999999', '23:59:59', 'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF', N'Ærøskøbing über café', 'Hello', N'ÆØÅ', 'xyz', 0x0102030405060708, 0xDEADBEEF, 1.5, 2.25, 0xCAFEBABE, N'Ελληνικά 中文字 🎉')
,(3, 1, -32768, -2147483648, -9223372036854775808, 1, -99999999999999999.99999999999999999999, -9999999999999999.99, -99999, '1753-01-01 00:00:00.000', '0001-01-01 00:00:00', '0001-01-01', '0001-01-01', '13:14:15.1234567', '13:14:15', '12345678-9ABC-DEF0-1234-56789ABCDEF0', N'Кириллица тест', 'plain ascii', N'ab', 'cd', 0xFFFFFFFFFFFFFFFF, 0x00, -1.5, -2.25, CONVERT(varbinary(max), REPLICATE(CONVERT(varchar(max),'NN'), 5000)), REPLICATE(CONVERT(nvarchar(max), N'notnull 测试 '), 400));
GO
-- Change tracking adds an internal in-row version column: sysrscols carries a row
-- whose rscolid has flag 0x08000000 (partition_column_id 134217730 = 0x08000002 in
-- sys.system_internals_partition_columns), type bigint, occupying fixed-data space
-- and a null bit but absent from syscolpars. Its masked low bits collide with a real
-- column id, so treating it as a user column shadows that column's value (observed
-- on Published/Installed Application in the BC 28.1 demo database; GitHub issue #6).
ALTER DATABASE typeprobe SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
GO
IF OBJECT_ID('probe_tracked') IS NOT NULL DROP TABLE probe_tracked;
CREATE TABLE probe_tracked (
  id int NOT NULL, g uniqueidentifier NOT NULL, txt nvarchar(40) NULL, amt decimal(18,2) NULL,
  CONSTRAINT pk_probe_tracked PRIMARY KEY CLUSTERED (id));
INSERT INTO probe_tracked VALUES
  (1, '11111111-2222-3333-4444-555555555555', N'before-tracking', 1.25),
  (2, '00000000-0000-0000-0000-000000000000', NULL, -2.50);
ALTER TABLE probe_tracked ENABLE CHANGE_TRACKING;
INSERT INTO probe_tracked VALUES
  (3, 'AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0001', N'after-tracking', 3.75),
  (4, 'FEDCBA98-7654-3210-FEDC-BA9876543210', N'ÆØÅ tracked', NULL);
UPDATE probe_tracked SET txt = N'updated-tracked' WHERE id = 1; -- rewrites a pre-tracking row with the version column
GO
-- ghost records inside compressed pages: delete right before BACKUP so the ghost
-- cleanup task has no time to purge them (regeneration must keep DELETE and BACKUP
-- in the same batch for the ghosts to be captured)
IF OBJECT_ID('probe_ghost') IS NOT NULL DROP TABLE probe_ghost;
CREATE TABLE probe_ghost (id int NOT NULL, val nvarchar(60) NOT NULL, amt decimal(18,2) NOT NULL,
  CONSTRAINT pk_probe_ghost PRIMARY KEY CLUSTERED (id)) WITH (DATA_COMPRESSION = PAGE);
;WITH n AS (SELECT TOP 500 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) i FROM sys.all_columns)
INSERT INTO probe_ghost SELECT i, N'value-' + CAST(i AS nvarchar(10)), i * 1.25 FROM n;
CHECKPOINT;
DELETE FROM probe_ghost WHERE id % 3 = 0;
CHECKPOINT;
BACKUP DATABASE typeprobe TO DISK='/tmp/typeprobe.bak' WITH INIT, NOFORMAT, NOSKIP;
GO
SELECT name, OBJECTPROPERTY(object_id,'TableHasClustIndex') FROM sys.tables ORDER BY name;
