DROP DATABASE IF EXISTS fsdb_decimal_top;
CREATE DATABASE fsdb_decimal_top;
USE fsdb_decimal_top;
CREATE TABLE digit(d INT PRIMARY KEY);
INSERT INTO digit VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
CREATE TABLE requests(id INT PRIMARY KEY,k DECIMAL(24,3),cutoff INT);
INSERT INTO requests VALUES
    (1,9007199254740992.010,19),(2,9007199254740992.020,19),
    (3,NULL,19),(4,9007199254740992.011,19);
CREATE TABLE events(
    id INT PRIMARY KEY,k DECIMAL(23,2),stamp INT,amount DECIMAL(9,2),
    KEY recent(k,stamp DESC,id)
);
INSERT INTO events
    SELECT t.d*100+x.d*10+y.d,
        CASE t.d WHEN 0 THEN 9007199254740992.01 WHEN 1 THEN 9007199254740992.02
            WHEN 2 THEN NULL ELSE t.d END,
        x.d*10+y.d,y.d/10
    FROM digit t CROSS JOIN digit x CROSS JOIN digit y;
ANALYZE TABLE events;
SELECT o.id,q.id,q.stamp
FROM requests o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM events e
    WHERE e.k<=>o.k AND e.stamp<=o.cutoff AND e.amount>=0.20
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY o.id,q.stamp DESC,q.id;
SELECT o.id,(SELECT e.id FROM events e
    WHERE e.k=o.k AND e.stamp<=o.cutoff AND e.amount>=0.20
    ORDER BY e.stamp,e.id DESC LIMIT 1 OFFSET 1) AS chosen
FROM requests o ORDER BY o.id;
EXPLAIN SELECT o.id,(SELECT e.id FROM events e
    WHERE e.k<=>o.k AND e.stamp<=o.cutoff AND e.amount>=0.20
    ORDER BY e.stamp DESC,e.id LIMIT 1 OFFSET 1) AS chosen
FROM requests o ORDER BY o.id;
DROP DATABASE fsdb_decimal_top;
