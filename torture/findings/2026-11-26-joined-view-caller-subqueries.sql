CREATE DATABASE joined_caller_probe;
USE joined_caller_probe;
CREATE TABLE base(id INT PRIMARY KEY,n INT);
CREATE TABLE side(id INT PRIMARY KEY,amount INT);
CREATE TABLE look(id INT PRIMARY KEY, extra INT);
INSERT INTO base VALUES(1,10),(2,20);
INSERT INTO side VALUES(1,50),(2,60);
INSERT INTO look VALUES(1,3),(2,4);
CREATE VIEW writable AS SELECT id AS k,n,id+10 AS shifted FROM base;
CREATE VIEW writable_side AS SELECT id AS partner,amount,id+100 AS bump FROM side;
CREATE TEMPORARY TABLE base(id INT PRIMARY KEY,private_value INT);
INSERT INTO base VALUES(1,100),(2,200);
UPDATE writable v JOIN side p ON p.id=v.k
SET v.n=(SELECT private_value FROM base t WHERE t.id=k)+ (SELECT p.amount) WHERE v.k=1;
SELECT ROW_COUNT();
UPDATE writable v JOIN writable_side s ON s.partner=v.k
SET v.n=(SELECT v.shifted+s.amount),s.amount=(SELECT v.k+s.bump) WHERE v.k=2;
SELECT ROW_COUNT();
UPDATE writable v JOIN side p ON p.id=v.k AND EXISTS(SELECT 1 FROM look q WHERE q.id=v.k AND q.extra=3)
SET v.n=v.n+1 WHERE v.k=1;
SELECT ROW_COUNT();
UPDATE writable v JOIN look p ON p.id=v.k SET v.n=(SELECT id) WHERE v.k=1;
SELECT ROW_COUNT();
UPDATE writable v JOIN writable_side s ON s.partner=v.k SET v.n=(SELECT id) WHERE v.k=1;
SELECT k,n FROM writable ORDER BY k;
SELECT partner,amount FROM writable_side ORDER BY partner;
SELECT id,private_value FROM base ORDER BY id;
CREATE TABLE collision(id INT PRIMARY KEY,n INT);
INSERT INTO collision VALUES(1,12);
UPDATE writable v JOIN collision p ON p.id=v.k SET v.n=(SELECT n) WHERE v.k=1;
START TRANSACTION;
PREPARE joined_update FROM 'UPDATE writable v JOIN look p ON p.id=v.k
SET v.n=(SELECT v.k+p.extra)+?
WHERE v.k=? AND EXISTS(SELECT 1 FROM base t WHERE t.id=k)';
SET @increment=7,@target=1;
EXECUTE joined_update USING @increment,
@target;
SELECT ROW_COUNT();
SELECT k,n FROM writable WHERE k=1;
DEALLOCATE PREPARE joined_update;
ROLLBACK;
SELECT k,n FROM writable WHERE k=1;
START TRANSACTION;
UPDATE writable v JOIN writable_side s ON s.partner=v.k
SET v.n=(SELECT private_value FROM base t WHERE t.id=v.k)+s.amount,
    s.amount=(SELECT v.k+s.bump) WHERE v.k=2;
SELECT ROW_COUNT();
SELECT k,n FROM writable WHERE k=2;
SELECT partner,amount FROM writable_side WHERE partner=2;
ROLLBACK;
SELECT k,n FROM writable WHERE k=2;
CREATE TABLE merged_partner(k INT PRIMARY KEY);
INSERT INTO merged_partner VALUES(1),(2);
UPDATE writable v JOIN merged_partner p USING(k) SET v.n=(SELECT k)+8 WHERE v.k=1;
SELECT ROW_COUNT();
UPDATE writable v NATURAL JOIN merged_partner p SET v.n=(SELECT k)+18 WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM writable ORDER BY k;
START TRANSACTION;
UPDATE merged_partner p JOIN writable v USING(k) SET v.n=(SELECT k)+20 WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM writable WHERE k=1;
UPDATE merged_partner p RIGHT JOIN writable v USING(k) SET v.n=(SELECT k)+30 WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM writable WHERE k=1;
ROLLBACK;
