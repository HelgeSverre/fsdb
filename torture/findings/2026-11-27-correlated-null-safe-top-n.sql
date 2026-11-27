CREATE DATABASE fsdb_nullsafe_top CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE fsdb_nullsafe_top;
SET NAMES utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE TABLE outer_row(id INT PRIMARY KEY,tenant INT,tag VARCHAR(12),cutoff INT);
INSERT INTO outer_row VALUES (1,1,'café',98),(2,NULL,'CAFE',100),(3,1,NULL,100),(4,NULL,NULL,100),(5,9,'missing',100);
CREATE TABLE evt(id INT PRIMARY KEY,tenant INT,tag VARCHAR(12),state INT,stamp INT,visible INT,
    KEY recent(tenant,tag,state,stamp DESC,id));
CREATE TABLE digit(d INT PRIMARY KEY);
INSERT INTO digit VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
INSERT INTO evt
SELECT t.d*100+n.d*10+x.d,IF(t.d<4,NULL,1),ELT(MOD(t.d,4)+1,'Cafe','CAFÉ','café',NULL),
    NULL,n.d*10+x.d,MOD(x.d,3)
FROM digit t CROSS JOIN digit n CROSS JOIN digit x WHERE t.d<8;
INSERT INTO evt VALUES (1000,1,'Cafe',1,100,1),(1001,NULL,NULL,1,100,1),(1002,NULL,NULL,NULL,NULL,1);

SELECT o.id,q.id,q.stamp FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e
    WHERE e.tenant <=> o.tenant AND e.tag <=> o.tag AND e.state IS NULL
      AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY o.id,q.stamp DESC,q.id;
SELECT o.id,(SELECT e.id FROM evt e
    WHERE o.tenant <=> e.tenant AND o.tag <=> e.tag AND e.state <=> NULL
      AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp,e.id DESC LIMIT 1 OFFSET 1) AS chosen
FROM outer_row o ORDER BY o.id;
SELECT o.id,(SELECT e.id FROM evt e
    WHERE e.tenant <=> o.tenant AND e.tag = o.tag AND e.state IS NULL
      AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 1) AS chosen
FROM outer_row o ORDER BY o.id;
SELECT o.id,(SELECT e.id FROM evt e IGNORE INDEX (recent)
    WHERE e.tenant <=> o.tenant AND e.tag <=> o.tag AND e.state IS NULL
      AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 1) AS chosen
FROM outer_row o ORDER BY o.id;
DROP DATABASE fsdb_nullsafe_top;
