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
