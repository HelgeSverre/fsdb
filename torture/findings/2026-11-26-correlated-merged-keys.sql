CREATE DATABASE joined_delete_keys;
USE joined_delete_keys;
CREATE TABLE base(id INT PRIMARY KEY,n INT);
CREATE TABLE physical(k INT PRIMARY KEY,n2 INT);
INSERT INTO base VALUES(1,10),(2,20);
INSERT INTO physical VALUES(1,7);
CREATE VIEW writable AS SELECT id AS k,n,id+10 AS shifted FROM base;
CREATE VIEW side AS SELECT k,n2 FROM physical;

SELECT (SELECT k) FROM side JOIN writable USING(k);
SELECT p.k,v.k,(SELECT k) AS merged FROM physical p RIGHT JOIN writable v USING(k) ORDER BY v.k;
SELECT (SELECT k FROM physical WHERE physical.k=1) AS local_key,(SELECT k) AS merged
FROM physical p RIGHT JOIN writable v USING(k) ORDER BY v.k;
SELECT (SELECT k) AS merged FROM physical p RIGHT JOIN writable v USING(k) WHERE (SELECT k)=2;
SELECT (SELECT k) AS merged FROM physical p RIGHT JOIN writable v USING(k) GROUP BY k ORDER BY k;
SELECT (SELECT k) AS merged,ROW_NUMBER() OVER(ORDER BY v.k) AS rn
FROM physical p RIGHT JOIN writable v USING(k) ORDER BY v.k;
SELECT v.k FROM physical p RIGHT JOIN writable v USING(k) ORDER BY (SELECT k) DESC;
SELECT (SELECT k) FROM (physical p RIGHT JOIN writable v USING(k))
JOIN (SELECT 1 AS probe) q ON (SELECT k)=q.probe;
SELECT (SELECT k) FROM (physical p RIGHT JOIN writable v USING(k)) JOIN physical q ON q.k=v.k;

PREPARE merged_read FROM 'SELECT (SELECT k) AS merged FROM physical p RIGHT JOIN writable v USING(k) WHERE (SELECT k)=?';
SET @key=2;
EXECUTE merged_read USING @key;
DEALLOCATE PREPARE merged_read;

CREATE TABLE other_physical(k INT PRIMARY KEY,n INT);
INSERT INTO other_physical VALUES(1,10),(2,20);
START TRANSACTION;
UPDATE physical p RIGHT JOIN other_physical q USING(k) SET q.n=(SELECT k)+30 WHERE (SELECT k)=2;
SELECT ROW_COUNT();
SELECT k,n FROM other_physical ORDER BY k;
ROLLBACK;

START TRANSACTION;
DELETE v FROM side p JOIN writable v USING(k) WHERE (SELECT k)=1;
SELECT ROW_COUNT();
ROLLBACK;

START TRANSACTION;
DELETE v FROM physical p NATURAL JOIN writable v WHERE (SELECT k)=1;
SELECT ROW_COUNT();
ROLLBACK;

START TRANSACTION;
DELETE v FROM physical p RIGHT JOIN writable v USING(k) WHERE (SELECT k)=2;
SELECT ROW_COUNT();
ROLLBACK;

START TRANSACTION;
DELETE v FROM physical p JOIN writable v ON p.k=v.k WHERE (SELECT k)=1;
ROLLBACK;

START TRANSACTION;
DELETE v FROM physical p JOIN writable v USING(k)
JOIN (SELECT 1 AS probe) q ON (SELECT k)=q.probe
WHERE v.k=1;
SELECT ROW_COUNT();
ROLLBACK;
