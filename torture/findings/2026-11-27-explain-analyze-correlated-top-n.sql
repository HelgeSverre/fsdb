DROP DATABASE IF EXISTS fsdb_explain_correlated_top;
CREATE DATABASE fsdb_explain_correlated_top CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE fsdb_explain_correlated_top;
CREATE TABLE outer_row(id INT PRIMARY KEY,tenant VARCHAR(12),cutoff INT);
INSERT INTO outer_row VALUES (1,'café',98),(2,'CAFE',100),(3,'別',100),(4,NULL,100),(5,'missing',100);
CREATE TABLE evt(id INT PRIMARY KEY,tenant VARCHAR(12),state INT,stamp INT,visible INT,KEY recent(tenant,state,stamp DESC,id));
CREATE TABLE digit(d INT PRIMARY KEY);
INSERT INTO digit VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
INSERT INTO evt SELECT t.d*100+x.d*10+y.d,ELT(t.d+1,'Cafe','CAFÉ','café','別','noise','other'),1,x.d*10+y.d,MOD(y.d,3) FROM digit t CROSS JOIN digit x CROSS JOIN digit y WHERE t.d<6;
INSERT INTO evt VALUES (1000,'Cafe',2,99,1),(1001,NULL,1,97,1);
EXPLAIN ANALYZE SELECT o.id,q.id FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e
    WHERE e.tenant=o.tenant AND e.state=1 AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY o.id,q.id;
EXPLAIN ANALYZE SELECT o.id,(SELECT e.id FROM evt e
    WHERE o.tenant=e.tenant AND e.state=1 AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp,e.id DESC LIMIT 1 OFFSET 1) AS chosen
FROM outer_row o ORDER BY o.id;
EXPLAIN ANALYZE SELECT o.id,
    (SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1 ORDER BY e.stamp DESC,e.id LIMIT 1) AS indexed,
    (SELECT e.id FROM evt e IGNORE INDEX (recent)
     WHERE e.tenant=o.tenant AND e.state=1 ORDER BY e.stamp DESC,e.id LIMIT 1) AS ignored
FROM outer_row o WHERE o.tenant IS NOT NULL ORDER BY o.id;
EXPLAIN ANALYZE SELECT o.id,q.id FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1 ORDER BY e.stamp DESC,e.id LIMIT 1
) q ON TRUE;
PREPARE p FROM 'EXPLAIN ANALYZE SELECT o.id,
    (SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1
     ORDER BY e.stamp DESC,e.id LIMIT ?) AS chosen
    FROM outer_row o ORDER BY o.id';
SET @limit=1;
EXECUTE p USING @limit;
SET @limit=0;
EXECUTE p USING @limit;
DEALLOCATE PREPARE p;
DELETE FROM outer_row;
EXPLAIN ANALYZE SELECT o.id,q.id FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1 ORDER BY e.stamp DESC,e.id LIMIT 1
) q ON TRUE;
DROP DATABASE fsdb_explain_correlated_top;
