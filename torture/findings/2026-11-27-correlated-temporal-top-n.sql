DROP DATABASE IF EXISTS temporal_top_n;
CREATE DATABASE temporal_top_n;
USE temporal_top_n;
CREATE TABLE digit(n INT PRIMARY KEY);
INSERT INTO digit VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9);
CREATE TABLE requests(
    id INT PRIMARY KEY, d DATE, dt DATETIME(6), clock TIME(6),
    cutoff TIME(6), min_stamp TIME(2) DEFAULT '00:00:00.01'
);
INSERT INTO requests(id,d,dt,clock,cutoff) VALUES
    (1,'2026-11-02','2026-11-02 00:00:00','01:00:00.12','00:00:09.550500'),
    (2,NULL,NULL,NULL,'00:00:09.550500'),
    (3,'2026-11-03','2026-11-03 00:00:00.000001','02:00:00.120001','00:00:08.100001'),
    (4,'2026-11-08','2026-11-08 00:00:00','-12:00:00.12','00:00:00.850001'),
    (5,'2026-12-01','2026-12-01 00:00:00','23:00:00.12','00:00:09.550500');
CREATE TABLE events(
    id INT PRIMARY KEY, day_key DATE, civil_key DATETIME(2), clock_key TIME(2),
    stamp TIME(2), visible INT,
    KEY by_day(day_key,stamp DESC,id),
    KEY by_civil(civil_key,stamp DESC,id),
    KEY by_clock(clock_key,stamp DESC,id)
);
INSERT INTO events
    SELECT g.n*100+x.n*10+y.n,
        IF(g.n=0,NULL,DATE_ADD('2026-11-01',INTERVAL g.n DAY)),
        IF(g.n=0,NULL,DATE_ADD('2026-11-01',INTERVAL g.n DAY)),
        CASE g.n WHEN 0 THEN NULL WHEN 7 THEN '-12:00:00.12' ELSE SEC_TO_TIME(g.n*3600+0.12) END,
        SEC_TO_TIME((x.n*10+y.n)/10),MOD(y.n,3)
    FROM digit g CROSS JOIN digit x CROSS JOIN digit y;

SELECT r.id, (
    SELECT e.id FROM events e FORCE INDEX (by_day)
    WHERE e.day_key<=>r.d AND e.stamp<=r.cutoff AND e.visible>0
    ORDER BY e.stamp DESC,e.id LIMIT 1 OFFSET 1
) AS chosen FROM requests r ORDER BY r.id;

SELECT r.id,q.id
FROM requests r LEFT JOIN LATERAL (
    SELECT e.id FROM events e FORCE INDEX (by_clock)
    WHERE e.clock_key<=>r.clock AND e.stamp BETWEEN r.min_stamp AND r.cutoff AND e.visible>0
    ORDER BY e.stamp DESC,e.id LIMIT 2 OFFSET 1
) q ON TRUE ORDER BY r.id,q.id;

EXPLAIN SELECT r.id, (
    SELECT e.id FROM events e FORCE INDEX (by_civil)
    WHERE e.civil_key<=>r.dt AND e.stamp<=r.cutoff AND e.visible>0
    ORDER BY e.stamp DESC,e.id LIMIT 1 OFFSET 1
) AS chosen FROM requests r ORDER BY r.id;
