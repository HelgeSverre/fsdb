CREATE DATABASE fsdb_checked_join_upsert;
USE fsdb_checked_join_upsert;
CREATE TABLE view_target(id INT PRIMARY KEY,k INT NOT NULL,n VARCHAR(4) NOT NULL);
CREATE TABLE view_gate(k INT PRIMARY KEY,ceiling INT NOT NULL);
INSERT INTO view_target VALUES(1,1,'5'),(2,7,'8'),(3,1,'15');
INSERT INTO view_gate VALUES(1,10);
CREATE VIEW checked_gate AS SELECT t.id,t.k,t.n,g.ceiling
FROM view_target t JOIN view_gate g ON g.k=t.k WHERE t.n<g.ceiling WITH CHECK OPTION;
CREATE VIEW outer_gate AS SELECT id AS i,k AS lane,n AS amount
FROM checked_gate WHERE n>0 WITH LOCAL CHECK OPTION;

INSERT INTO outer_gate(i,lane,amount) VALUES(1,1,'5') ON DUPLICATE KEY UPDATE amount=5;
SELECT ROW_COUNT();
INSERT INTO outer_gate(i,lane,amount) VALUES(3,1,'2') ON DUPLICATE KEY UPDATE amount=amount;
SELECT ROW_COUNT();
INSERT INTO outer_gate(i,lane,amount) VALUES(2,1,'2') ON DUPLICATE KEY UPDATE lane=1,amount=9;
SELECT ROW_COUNT();
INSERT INTO outer_gate(i,lane,amount) VALUES(1,1,'5') ON DUPLICATE KEY UPDATE lane=7;
SHOW WARNINGS;
INSERT INTO outer_gate(i,lane,amount) VALUES(1,1,'0') ON DUPLICATE KEY UPDATE amount=6;
SHOW WARNINGS;
INSERT IGNORE INTO outer_gate(i,lane,amount) VALUES(1,1,'3'),(4,1,'4') ON DUPLICATE KEY UPDATE amount=20;
SHOW WARNINGS;
SELECT * FROM view_target ORDER BY id;

INSERT INTO checked_gate SET id=1,k=1,n='6' ON DUPLICATE KEY UPDATE n='6';
SELECT ROW_COUNT();
CREATE TABLE incoming(id INT PRIMARY KEY,k INT,n VARCHAR(4));
INSERT INTO incoming VALUES(1,1,'8'),(5,1,'7');
START TRANSACTION;
INSERT INTO outer_gate(i,lane,amount) SELECT id,k,n FROM incoming ORDER BY id ON DUPLICATE KEY UPDATE amount=8;
SELECT ROW_COUNT();
SELECT * FROM view_target ORDER BY id;
ROLLBACK;
PREPARE checked_upsert FROM 'INSERT INTO outer_gate(i,lane,amount) VALUES(1,1,?) ON DUPLICATE KEY UPDATE amount=?';
SET @proposed='5', @changed='7';
EXECUTE checked_upsert USING @proposed,@changed;
SELECT ROW_COUNT();
DEALLOCATE PREPARE checked_upsert;
SELECT * FROM view_target ORDER BY id;

CREATE VIEW checked_cross AS SELECT t.id,t.k,t.n,g.ceiling
FROM view_target t CROSS JOIN view_gate g WHERE t.n>0 WITH CHECK OPTION;
INSERT INTO checked_cross(id,k,n) VALUES(1,1,'5') ON DUPLICATE KEY UPDATE k=7;
SELECT ROW_COUNT();
INSERT INTO checked_cross(id,k,n) VALUES(1,1,'5') ON DUPLICATE KEY UPDATE n='-1';
SHOW WARNINGS;
SELECT * FROM view_target WHERE id=1;

INSERT INTO view_gate VALUES(2,20);
UPDATE view_target SET k=2 WHERE id=1;
INSERT INTO outer_gate(i,lane,amount) VALUES(1,2,'8') ON DUPLICATE KEY UPDATE amount=8;
SELECT ROW_COUNT();
INSERT INTO outer_gate(i,lane,amount) VALUES(1,2,'8') ON DUPLICATE KEY UPDATE lane=1;
SHOW WARNINGS;
SELECT * FROM view_target WHERE id=1;
DROP DATABASE fsdb_checked_join_upsert;
