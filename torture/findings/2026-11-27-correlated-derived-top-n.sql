DROP DATABASE IF EXISTS fsdb_derived_top;
CREATE DATABASE fsdb_derived_top;
USE fsdb_derived_top;
CREATE TABLE digit(n INT PRIMARY KEY);
INSERT INTO digit VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
CREATE TABLE requests(id INT PRIMARY KEY,k INT);
INSERT INTO requests SELECT n,n FROM digit;
CREATE TABLE events(id INT PRIMARY KEY,k INT,stamp INT,visible INT,KEY recent(k,stamp DESC,id));
INSERT INTO events
    SELECT a.n*100+b.n*10+c.n,a.n,b.n*10+c.n,c.n%3
    FROM digit a CROSS JOIN digit b CROSS JOIN digit c;
ANALYZE TABLE events;
SELECT o.id,(SELECT d.event_id
    FROM (SELECT e.id AS event_id,e.k AS tenant,e.stamp AS tick FROM events e WHERE e.visible>0) d
    WHERE d.tenant=k ORDER BY d.tick DESC,d.event_id LIMIT 1 OFFSET 1
) AS chosen FROM requests o ORDER BY o.id;
EXPLAIN SELECT o.id,(SELECT d.event_id
    FROM (SELECT e.id AS event_id,e.k AS tenant,e.stamp AS tick FROM events e WHERE e.visible>0) d
    WHERE d.tenant=k ORDER BY d.tick DESC,d.event_id LIMIT 1 OFFSET 1
) AS chosen FROM requests o ORDER BY o.id;
EXPLAIN ANALYZE SELECT o.id,(SELECT d.event_id
    FROM (SELECT e.id AS event_id,e.k AS tenant,e.stamp AS tick FROM events e WHERE e.visible>0) d
    WHERE d.tenant=k ORDER BY d.tick DESC,d.event_id LIMIT 1 OFFSET 1
) AS chosen FROM requests o ORDER BY o.id;
DROP DATABASE fsdb_derived_top;
