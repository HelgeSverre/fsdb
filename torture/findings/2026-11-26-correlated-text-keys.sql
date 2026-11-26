-- Run on the digest-pinned MySQL 8.4.11 image and a disposable Fsdb server.
-- The fixture owns and drops the decorrelation_text_probe schema.
CREATE DATABASE decorrelation_text_probe;
USE decorrelation_text_probe;
CREATE TABLE owners(id INT PRIMARY KEY, tenant INT, tag VARCHAR(24) COLLATE utf8mb4_unicode_ci);
CREATE TABLE items(id INT PRIMARY KEY, tenant INT, tag VARCHAR(24) COLLATE utf8mb4_unicode_ci, payload INT, number DOUBLE);
CREATE TABLE marker(id INT PRIMARY KEY);
INSERT INTO owners VALUES (1,10,'École'),(2,10,'上海'),(3,10,NULL),(4,20,'ecole'),(5,10,'absent'),(6,NULL,'École');
INSERT INTO items VALUES (1,10,'ecole',3,4),(2,10,'ÉCOLE ',NULL,2),(3,10,'上海',5,4),(4,10,NULL,4,5),(5,20,'École',5,6),(6,NULL,'École',6,6),(7,10,'unrelated',7,6);
INSERT INTO marker VALUES(1);
SELECT o.id,
 (SELECT COUNT(i.payload) FROM items i JOIN marker m ON m.id=1 AND i.tenant=o.tenant WHERE i.tag <=> o.tag) AS c,
 EXISTS (SELECT 1 FROM items i JOIN marker m ON m.id=1 AND i.tenant=o.tenant WHERE i.tag=o.tag) AS e
 FROM owners o ORDER BY o.id;
SELECT o.id,(SELECT COUNT(*) FROM items i JOIN marker m ON m.id=1 WHERE i.tag <=> o.tag AND i.tenant <=> o.tenant) AS c FROM owners o ORDER BY o.id;
CREATE TABLE binary_owners(id INT PRIMARY KEY,k VARCHAR(8) COLLATE utf8mb4_0900_bin);
CREATE TABLE binary_items(k VARCHAR(8) COLLATE utf8mb4_0900_bin);
INSERT INTO binary_owners VALUES(1,'x'),(2,'x '),(3,'X');
INSERT INTO binary_items VALUES('x'),('x'),('x '),('X');
SELECT o.id, (SELECT COUNT(*) FROM binary_items i JOIN marker m ON m.id=1 WHERE i.k=o.k) c FROM binary_owners o ORDER BY o.id;
SELECT o.id, (SELECT COUNT(*) FROM binary_items i JOIN marker m ON m.id=1 WHERE i.k=o.k COLLATE utf8mb4_unicode_ci) c FROM binary_owners o ORDER BY o.id;
CREATE TABLE folded_items(k VARCHAR(8) COLLATE utf8mb4_unicode_ci);
INSERT INTO folded_items SELECT k FROM binary_items;
SELECT o.id, (SELECT COUNT(*) FROM folded_items i JOIN marker m ON m.id=1 WHERE i.k=o.k) c FROM binary_owners o ORDER BY o.id;
SELECT o.id, (SELECT COUNT(*) FROM items i JOIN marker m ON m.id=1 WHERE i.tag=o.tag AND i.number < '3x') c FROM owners o WHERE o.id IN (1,4) ORDER BY o.id;
SHOW COUNT(*) WARNINGS;
SHOW WARNINGS;
DROP DATABASE decorrelation_text_probe;
