SET NAMES utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE DATABASE fsdb_defaults_probe CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
SHOW CREATE DATABASE fsdb_defaults_probe;
USE fsdb_defaults_probe;
SELECT @@character_set_database,@@collation_database,@@collation_connection;
SHOW VARIABLES LIKE 'collation_database';
CREATE TABLE old_table(id INT PRIMARY KEY,v VARCHAR(12),explicit_v VARCHAR(12) COLLATE utf8mb4_0900_bin);
INSERT INTO old_table VALUES (1,'CAFÉ','CAFÉ'),(2,'cafe ','cafe ');
SELECT a.v <=> b.v,a.explicit_v <=> b.explicit_v,COLLATION(a.v),COLLATION(a.explicit_v)
FROM old_table a JOIN old_table b ON a.id=1 AND b.id=2;
SELECT a.id,(SELECT b.id FROM old_table b WHERE b.id<>a.id AND b.v <=> a.v ORDER BY b.id LIMIT 1)
FROM old_table a ORDER BY a.id;
CREATE TABLE temp_example(v VARCHAR(12)) DEFAULT CHARACTER SET utf8mb4;

ALTER DATABASE CHARACTER SET latin1;
SHOW CREATE DATABASE fsdb_defaults_probe;
SELECT @@character_set_database,@@collation_database,@@collation_connection;
CREATE TABLE new_table(v VARCHAR(12));
CREATE TABLE explicit_charset(v VARCHAR(12)) DEFAULT CHARSET=utf8mb4;
CREATE TABLE explicit_collate(v VARCHAR(12)) COLLATE=utf8mb4_unicode_ci;
CREATE TABLE cloned LIKE temp_example;
INSERT INTO new_table VALUES ('ångström');
SELECT HEX(v),COLLATION(v) FROM new_table;
SELECT TABLE_NAME,COLUMN_NAME,CHARACTER_SET_NAME,COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA=DATABASE() AND COLUMN_NAME IN ('v','explicit_v') ORDER BY 1,2;
SELECT DEFAULT_CHARACTER_SET_NAME,DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME=DATABASE();
CREATE DATABASE IF NOT EXISTS fsdb_defaults_probe COLLATE utf8mb4_0900_bin;
SHOW WARNINGS;
SELECT @@collation_database;
ALTER DATABASE fsdb_defaults_missing CHARSET latin1;
CREATE DATABASE fsdb_defaults_invalid CHARSET bogus;
CREATE DATABASE fsdb_defaults_invalid COLLATE bogus;
CREATE DATABASE fsdb_defaults_invalid CHARSET latin1 COLLATE utf8mb4_0900_ai_ci;
SELECT * FROM mysql.schemata LIMIT 0;
SHOW CREATE TABLE mysql.schemata;
SHOW TABLES FROM mysql LIKE 'schemata';
DROP DATABASE fsdb_defaults_probe;
SELECT DATABASE(),@@character_set_database,@@collation_database;
ALTER DATABASE CHARACTER SET latin1;
CREATE DATABASE fsdb_defaults_probe;
SELECT DEFAULT_COLLATION_NAME FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME='fsdb_defaults_probe';
DROP DATABASE fsdb_defaults_probe;
