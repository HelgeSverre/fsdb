CREATE DATABASE fsdb_lateral_top;
USE fsdb_lateral_top;
CREATE TABLE digit(d INT PRIMARY KEY);
INSERT INTO digit VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
CREATE TABLE outer_row(id INT PRIMARY KEY, tenant BIGINT UNSIGNED, cutoff INT NOT NULL);
INSERT INTO outer_row VALUES (1,1,95),(2,2,105),(3,3,140),(4,1,95),(5,NULL,95),(6,99,95);
CREATE TABLE evt(id INT PRIMARY KEY, tenant BIGINT UNSIGNED, state INT, stamp INT, visible INT,
    KEY recent(tenant,state,stamp DESC,id ASC));
INSERT INTO evt SELECT t.d*1000+h.d*100+x.d*10+y.d, t.d+1, 1, h.d*50+x.d*5+FLOOR(y.d/2), y.d%3
FROM digit t CROSS JOIN digit h CROSS JOIN digit x CROSS JOIN digit y WHERE t.d<5 AND h.d<4;
INSERT INTO evt VALUES (10000,NULL,1,300,1),(10001,1,2,300,1),(10002,1,1,NULL,1);
SELECT o.id,q.id,q.stamp FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e WHERE e.tenant=o.tenant AND e.state=1
    ORDER BY e.stamp DESC,e.id LIMIT 2
) q ON TRUE ORDER BY o.id,q.stamp DESC,q.id;
SELECT o.id,q.id,q.stamp FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e WHERE e.tenant=o.tenant AND e.state=1 AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY o.id,q.stamp DESC,q.id;
SELECT o.id,q.id,q.stamp FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e WHERE e.tenant=o.tenant AND e.state=1 AND e.stamp>=o.cutoff
    ORDER BY e.stamp,e.id DESC LIMIT 2
) q ON TRUE ORDER BY o.id,q.stamp,q.id DESC;
SELECT o.id,q.id,q.stamp FROM outer_row o LEFT JOIN LATERAL (
    SELECT e.id,e.stamp FROM evt e IGNORE INDEX (recent) WHERE e.tenant=o.tenant AND e.state=1 AND e.stamp<=o.cutoff AND e.visible=1
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY o.id,q.stamp DESC,q.id;
SELECT o.id,(SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1 AND e.stamp<=o.cutoff AND e.visible=1 ORDER BY e.stamp DESC,e.id LIMIT 1) AS latest
FROM outer_row o ORDER BY o.id;
DELIMITER //
CREATE PROCEDURE routine_order()
BEGIN
    DECLARE stamp INT DEFAULT 0;
    SELECT o.id,q.id FROM outer_row o LEFT JOIN LATERAL (
        SELECT e.id FROM evt e WHERE e.tenant=o.tenant AND e.state=1
        ORDER BY stamp DESC,e.id LIMIT 1
    ) q ON TRUE ORDER BY o.id;
END//
DELIMITER ;
CALL routine_order();
DROP DATABASE fsdb_lateral_top;
