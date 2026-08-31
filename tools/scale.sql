IF DB_ID('scale') IS NOT NULL BEGIN ALTER DATABASE scale SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE scale; END;
CREATE DATABASE scale ON (NAME='scale_data', FILENAME='/var/opt/mssql/data/scale.mdf', SIZE=5500MB, FILEGROWTH=256MB)
LOG ON (NAME='scale_log', FILENAME='/var/opt/mssql/data/scale.ldf', SIZE=512MB);
GO
ALTER DATABASE scale SET RECOVERY SIMPLE;
ALTER DATABASE scale SET MIXED_PAGE_ALLOCATION ON;
GO
USE scale;
GO
-- small tables while mixed allocation is on: single-page (mixed-extent) allocations
CREATE TABLE mixed1 (id int NOT NULL PRIMARY KEY CLUSTERED, name nvarchar(50) NOT NULL, amount decimal(18,2) NOT NULL);
INSERT INTO mixed1 VALUES (1, N'first Ærøskøbing', 1.50), (2, N'второй', -2.25), (3, N'第三', 0);
CREATE TABLE mixed2 (id int NOT NULL PRIMARY KEY CLUSTERED, note nvarchar(100) NULL);
INSERT INTO mixed2 VALUES (10, N'note ten'), (11, NULL), (12, N'επτά');
GO
-- Turn mixed allocation OFF again before the big table so PFS-page extents get
-- allocated to user tables (uniform extents): a table extent containing a PFS page
-- is the case a production database exposed (the reader must skip allocation pages
-- inside data extents).
ALTER DATABASE scale SET MIXED_PAGE_ALLOCATION OFF;
GO
-- big table pushing the data file past one GAM interval (> 4 GB allocated)
CREATE TABLE big (
  id bigint NOT NULL PRIMARY KEY CLUSTERED,
  filler varchar(7500) NOT NULL,
  d decimal(38,20) NOT NULL,
  ts datetime NOT NULL
);
GO
SET NOCOUNT ON;
DECLARE @i bigint = 0;
WHILE @i < 640000
BEGIN
  ;WITH n AS (SELECT TOP 10000 ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS k FROM sys.all_columns a CROSS JOIN sys.all_columns b)
  INSERT INTO big SELECT @i + k, REPLICATE(CONVERT(char(20), (@i + k) % 997), 350), CAST(@i + k AS decimal(38,20)) / 7, DATEADD(SECOND, (@i+k) % 86400, '2026-01-01')
  FROM n;
  SET @i = @i + 10000;
  IF @i % 100000 = 0 BEGIN CHECKPOINT; RAISERROR('inserted %I64d', 0, 1, @i) WITH NOWAIT; END;
END;
GO
-- write history: deletes + updates so free pages / stale images exist
DELETE FROM big WHERE id % 13 = 0;
UPDATE big SET filler = REPLICATE('Z', 100) WHERE id % 29 = 0;
GO
-- a table created AFTER the churn, landing in reused space
CREATE TABLE late1 (id int NOT NULL PRIMARY KEY CLUSTERED, v nvarchar(80) NOT NULL);
INSERT INTO late1 VALUES (100, N'late row Кириллица'), (101, N'plain');
GO
CHECKPOINT;
GO
BACKUP DATABASE scale TO DISK='/tmp/scale.bak' WITH INIT, NOFORMAT, NOSKIP;
GO
SELECT 'file_pages', size FROM sys.master_files WHERE database_id=DB_ID('scale');
