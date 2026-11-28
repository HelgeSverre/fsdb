-- Use a disposable server. This fixture adds a named time zone.
CREATE DATABASE timestamp_topn;
USE timestamp_topn;
INSERT INTO mysql.time_zone (Time_zone_id,Use_leap_seconds) VALUES (900001,'N');
INSERT INTO mysql.time_zone_name VALUES ('Fsdb/Test_Eastern',900001);
INSERT INTO mysql.time_zone_transition_type
    (Time_zone_id,Transition_type_id,Offset,Is_DST,Abbreviation)
VALUES (900001,0,-18000,0,'EST'),(900001,1,-14400,1,'EDT');
INSERT INTO mysql.time_zone_transition VALUES (900001,1710054000,1),(900001,1730613600,0);

SET time_zone='+00:00';
CREATE TABLE picks (id INT PRIMARY KEY, instant TIMESTAMP(6) NULL, wall DATETIME(6) NULL);
CREATE TABLE events (
    id INT PRIMARY KEY, instant TIMESTAMP(2) NULL, ordinal INT, enabled INT,
    KEY by_slot(instant,ordinal DESC,id)
);
INSERT INTO picks VALUES
    (1,'2024-11-03 05:30:00.20','2024-11-03 01:30:00.20'),
    (2,'2024-11-03 06:30:00.20','2024-11-03 01:30:00.20'),
    (3,'2024-11-03 06:30:00.21','2024-11-03 01:30:00.210001'),
    (4,NULL,NULL);
INSERT INTO events VALUES
    (11,'2024-11-03 05:30:00.20',3,0),(12,'2024-11-03 05:30:00.20',2,1),(13,'2024-11-03 05:30:00.20',1,1),
    (21,'2024-11-03 06:30:00.20',3,0),(22,'2024-11-03 06:30:00.20',4,1),(23,'2024-11-03 06:30:00.20',1,1),
    (31,'2024-11-03 06:30:00.21',3,0),(32,'2024-11-03 06:30:00.21',2,1),(33,'2024-11-03 06:30:00.21',1,1),
    (41,NULL,3,0),(42,NULL,2,1),(43,NULL,1,1);

SET time_zone='Fsdb/Test_Eastern';
SELECT p.id,
    (SELECT e.id FROM events e FORCE INDEX(by_slot)
     WHERE e.instant<=>p.instant AND e.enabled>0 ORDER BY e.ordinal DESC,e.id LIMIT 1 OFFSET 1) AS indexed,
    (SELECT e.id FROM events e IGNORE INDEX(by_slot)
     WHERE e.instant<=>p.instant AND e.enabled>0 ORDER BY e.ordinal DESC,e.id LIMIT 1 OFFSET 1) AS scanned
FROM picks p ORDER BY p.id;
SELECT p.id,
    (SELECT e.id FROM (SELECT id,instant,ordinal,enabled FROM events FORCE INDEX(by_slot)) e
     WHERE e.instant<=>p.wall AND e.enabled>0 ORDER BY e.ordinal ASC,e.id ASC LIMIT 1 OFFSET 1) AS chosen
FROM picks p ORDER BY p.id;

SELECT p.id,q.chosen
FROM picks p LEFT JOIN LATERAL (
    SELECT e.id AS chosen FROM events e FORCE INDEX(by_slot)
    WHERE e.instant=p.instant AND e.enabled>0 ORDER BY e.ordinal DESC,e.id LIMIT 2
) q ON TRUE ORDER BY p.id,q.chosen;

SELECT p.id,
    (SELECT e.id FROM events e IGNORE INDEX(by_slot)
     WHERE e.instant=p.wall AND e.enabled>0 ORDER BY e.instant,e.ordinal DESC,e.id LIMIT 1) AS direct,
    (SELECT e.id FROM (SELECT * FROM events IGNORE INDEX(by_slot)) e
     WHERE e.instant=p.wall AND e.enabled>0 ORDER BY e.instant,e.ordinal DESC,e.id LIMIT 1) AS merged,
    (SELECT /*+ NO_MERGE(e) */ e.id FROM (SELECT * FROM events IGNORE INDEX(by_slot)) e
     WHERE e.instant=p.wall AND e.enabled>0 ORDER BY e.instant,e.ordinal DESC,e.id LIMIT 1) AS materialized
FROM picks p ORDER BY p.id;

CREATE ALGORITHM=MERGE VIEW merged_events AS SELECT * FROM events IGNORE INDEX(by_slot);
CREATE ALGORITHM=TEMPTABLE VIEW materialized_events AS SELECT * FROM events IGNORE INDEX(by_slot);
SELECT p.id,
    (SELECT e.id FROM merged_events e
     WHERE e.instant=p.wall AND e.enabled>0 ORDER BY e.instant,e.ordinal DESC,e.id LIMIT 1) AS merged,
    (SELECT e.id FROM materialized_events e
     WHERE e.instant=p.wall AND e.enabled>0 ORDER BY e.instant,e.ordinal DESC,e.id LIMIT 1) AS materialized
FROM picks p ORDER BY p.id;

PREPARE timezone_lookup FROM '
    SELECT p.id,(SELECT e.id FROM events e FORCE INDEX(by_slot)
        WHERE e.instant<=>p.instant AND e.enabled>0 ORDER BY e.ordinal DESC,e.id LIMIT 1 OFFSET 1) AS chosen
    FROM picks p ORDER BY p.id';
EXECUTE timezone_lookup;
SET time_zone='-04:00';
EXECUTE timezone_lookup;
DEALLOCATE PREPARE timezone_lookup;

SET time_zone='Fsdb/Test_Eastern';
SELECT a.id, ROW_NUMBER() OVER(ORDER BY a.id) AS row_number_by_id
FROM events a IGNORE INDEX(by_slot)
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,a.ordinal DESC,a.id;

SELECT a.id,
    ROW_NUMBER() OVER(ORDER BY a.instant,a.ordinal DESC,a.id) AS row_number_by_timestamp,
    DENSE_RANK() OVER(ORDER BY a.instant) AS timestamp_peer_rank
FROM events a IGNORE INDEX(by_slot)
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,a.ordinal DESC,a.id;

SELECT a.id,COUNT(*) OVER() AS total
FROM events a IGNORE INDEX(by_slot)
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,a.ordinal DESC,a.id;

SELECT a.id,ROW_NUMBER() OVER(ORDER BY a.id) AS row_number_by_id,
    ROW_NUMBER() OVER(by_stamp) AS row_number_by_timestamp
FROM events a IGNORE INDEX(by_slot)
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
WINDOW by_stamp AS (ORDER BY a.instant,a.ordinal DESC,a.id)
ORDER BY a.id;

SELECT STRAIGHT_JOIN a.id
FROM events a JOIN events b ON a.id=b.id
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,(SELECT b.ordinal) DESC,a.id;

SELECT STRAIGHT_JOIN a.id
FROM events a JOIN events b ON a.id=b.id
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,(SELECT a.ordinal) DESC,a.id;

SELECT a.id
FROM events a JOIN events b ON a.id=b.id
WHERE b.id BETWEEN 12 AND 22 AND b.enabled=1
ORDER BY a.instant,a.ordinal DESC,a.id;

WITH d AS (SELECT * FROM events IGNORE INDEX(by_slot))
SELECT /*+ NO_MERGE(b) */ a.id
FROM d a JOIN d b ON a.id=b.id
WHERE a.id BETWEEN 12 AND 22 AND a.enabled=1
ORDER BY a.instant,b.ordinal DESC,a.id;

SET time_zone='+00:00';
CREATE TABLE partition_events(id INT PRIMARY KEY, instant TIMESTAMP NOT NULL, KEY by_instant(instant,id));
INSERT INTO partition_events VALUES
    (4,'2024-11-03 05:15:00'),(3,'2024-11-03 05:45:00'),(2,'2024-11-03 06:15:00'),(1,'2024-11-03 06:45:00'),
    (5,'2024-11-03 06:45:00'),(6,'2024-11-03 05:45:00'),(7,'2024-11-03 05:15:00');
SET time_zone='Fsdb/Test_Eastern';
SELECT id, ROW_NUMBER() OVER(PARTITION BY instant ORDER BY id) AS row_number_by_timestamp,
    COUNT(*) OVER(PARTITION BY instant ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS partition_size
FROM partition_events IGNORE INDEX(by_instant) ORDER BY id;
SELECT id, ROW_NUMBER() OVER(PARTITION BY instant ORDER BY id) AS r
FROM partition_events IGNORE INDEX(by_instant) ORDER BY instant,r,id LIMIT 5;
