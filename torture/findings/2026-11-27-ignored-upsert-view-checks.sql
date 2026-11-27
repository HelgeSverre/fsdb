CREATE DATABASE fsdb_ignore_view;
USE fsdb_ignore_view;
CREATE TABLE item(id INT PRIMARY KEY,qty INT);
INSERT INTO item VALUES (1,-1),(2,10),(3,10);
CREATE VIEW visible AS SELECT id,qty FROM item WHERE qty>0;
CREATE VIEW guarded AS SELECT id,qty FROM visible WITH CASCADED CHECK OPTION;

INSERT IGNORE INTO guarded VALUES (1,4),(2,0),(3,6),(4,8),(5,0),(6,9)
ON DUPLICATE KEY UPDATE qty=IF(id=1,-2,6);
SHOW WARNINGS;
SELECT * FROM item ORDER BY id;

CREATE TABLE incoming(source_id INT PRIMARY KEY,source_qty INT);
INSERT INTO incoming VALUES (2,4),(3,0),(8,9);
INSERT IGNORE INTO guarded(id,qty) SELECT source_id,source_qty FROM incoming ORDER BY source_id
ON DUPLICATE KEY UPDATE qty=IF(guarded.id=2,-3,guarded.qty);
SHOW WARNINGS;
SELECT * FROM item ORDER BY id;

INSERT INTO item VALUES (7,-1);
PREPARE upsert FROM 'INSERT IGNORE INTO guarded VALUES(?,?),(?,?),(?,?) ON DUPLICATE KEY UPDATE qty=-2';
SET @a=1,@b=15,@c=7,@d=0,@e=9,@f=8;
START TRANSACTION;
EXECUTE upsert USING @a,@b,@c,@d,@e,@f;
SHOW WARNINGS;
SELECT * FROM item WHERE id IN (1,7,9) ORDER BY id;
ROLLBACK;
DEALLOCATE PREPARE upsert;
SELECT * FROM item WHERE id IN (1,7,9) ORDER BY id;
DROP DATABASE fsdb_ignore_view;
