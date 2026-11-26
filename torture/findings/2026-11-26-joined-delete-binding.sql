CREATE DATABASE delete_binding_probe;
USE delete_binding_probe;

CREATE TABLE base (id INT PRIMARY KEY);
CREATE TABLE partner (id INT PRIMARY KEY);
INSERT INTO base VALUES (1), (2);
INSERT INTO partner VALUES (1), (2);
CREATE VIEW joined_v AS SELECT b.id FROM base b JOIN partner p ON b.id=p.id;
CREATE VIEW layered_v AS SELECT * FROM joined_v;
CREATE VIEW outer_v AS SELECT b.id FROM base b LEFT JOIN partner p ON b.id=p.id;
CREATE ALGORITHM=TEMPTABLE VIEW materialized_v AS SELECT * FROM base;
CREATE VIEW writable_v AS SELECT * FROM base;
CREATE TABLE removed (id INT);
CREATE VIEW invalid_v AS SELECT * FROM removed;
DROP TABLE removed;

DELETE v FROM joined_v v JOIN partner p ON p.id=v.missing;
DELETE p,v FROM partner p JOIN joined_v v ON p.id=v.missing;
DELETE FROM v USING layered_v v JOIN partner p USING(missing);
DELETE v FROM joined_v v JOIN (partner p JOIN base b ON p.missing=b.id) ON TRUE;
DELETE v FROM outer_v v JOIN partner p ON p.id=v.missing;
DELETE v FROM materialized_v v JOIN partner p ON p.id=v.missing;
DELETE y,x FROM joined_v x JOIN outer_v y ON TRUE;
DELETE x,y FROM outer_v y JOIN joined_v x ON TRUE;
DELETE d FROM partner p JOIN (SELECT id FROM base) d ON p.missing=d.id;

DELETE v,unknown_target FROM joined_v v JOIN partner p ON p.id=v.missing;
DELETE unknown_target FROM joined_v v JOIN absent p ON TRUE;
DELETE unknown_target FROM base t JOIN partner t ON TRUE;
DELETE v FROM joined_v v JOIN absent p ON p.id=v.missing;
DELETE v FROM joined_v v JOIN invalid_v i ON i.id=v.missing;
DELETE v FROM joined_v v JOIN (SELECT missing FROM base) d ON TRUE;
DELETE v FROM joined_v v JOIN partner p FORCE INDEX(absent) ON p.id=v.missing;
DELETE v FROM joined_v v JOIN partner p USING(id) JOIN LATERAL (SELECT id AS i) d ON d.i=v.id;
DELETE v FROM joined_v v JOIN partner p USING(id)
JOIN JSON_TABLE(JSON_ARRAY(id), '$[*]' COLUMNS(i INT PATH '$')) d ON d.i=v.id;
DELETE v FROM joined_v v JOIN JSON_TABLE('bad', '$[*]' COLUMNS(i INT PATH '$')) d ON TRUE;

WITH read_source AS (SELECT missing FROM base)
DELETE v,unknown_target FROM joined_v v JOIN read_source r ON TRUE;
WITH read_source AS (SELECT missing FROM base)
DELETE v FROM joined_v v JOIN read_source r ON TRUE;
WITH joined_v AS (SELECT id FROM base)
DELETE v FROM joined_v v JOIN partner p ON p.id=v.missing;

DELETE v FROM writable_v v JOIN partner p ON p.id=v.missing;
EXPLAIN DELETE v FROM joined_v v JOIN partner p ON p.id=v.missing;
PREPARE p FROM 'DELETE v FROM joined_v v JOIN partner p ON p.id=v.missing';

DELETE v FROM joined_v v JOIN partner p ON GET_LOCK('delete-binding-probe',0)=1;
SELECT IS_FREE_LOCK('delete-binding-probe');
DELETE v FROM joined_v v
JOIN (SELECT GET_LOCK('delete-binding-source-probe',0) AS held FROM partner) d ON d.held=1;
SELECT IS_FREE_LOCK('delete-binding-source-probe');
SELECT id FROM base ORDER BY id;
SELECT id FROM partner ORDER BY id;

DELETE v,p FROM writable_v v JOIN partner p ON p.id=v.id WHERE v.id=2;
SELECT ROW_COUNT();
SELECT id FROM base ORDER BY id;
SELECT id FROM partner ORDER BY id;
