CREATE DATABASE shadow_delete_probe;
USE shadow_delete_probe;
CREATE TABLE base(id INT PRIMARY KEY,n INT);
CREATE TABLE child(id INT PRIMARY KEY, parent INT, FOREIGN KEY(parent) REFERENCES base(id) ON DELETE CASCADE);
INSERT INTO base VALUES(1,10),(2,20),(3,30),(4,40);
INSERT INTO child VALUES(10,1),(20,2),(30,3),(40,4);
CREATE VIEW inner_v AS SELECT id AS k,n FROM base WHERE n>0;
CREATE VIEW outer_v AS SELECT * FROM inner_v;
DELIMITER //
CREATE TRIGGER refuse BEFORE DELETE ON base FOR EACH ROW BEGIN
  IF OLD.id=3 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='blocked'; END IF;
END//
DELIMITER ;
CREATE TEMPORARY TABLE base(id INT PRIMARY KEY,temporary_value VARCHAR(10));
INSERT INTO base VALUES(1,'temp1'),(2,'temp2'),(3,'temp3'),(4,'temp4');
DELETE v FROM outer_v v JOIN base t ON t.id=v.k WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM outer_v ORDER BY k;
SELECT id,temporary_value FROM base ORDER BY id;
SELECT id,parent FROM child ORDER BY id;
DELETE t,v FROM base t JOIN outer_v v ON t.id=v.k WHERE v.k=2;
SELECT ROW_COUNT();
SELECT k,n FROM outer_v ORDER BY k;
SELECT id,temporary_value FROM base ORDER BY id;
SELECT id,parent FROM child ORDER BY id;
DELETE t,v FROM base t JOIN outer_v v ON t.id=v.k WHERE v.k=3;
SELECT k,n FROM outer_v ORDER BY k;
SELECT id,temporary_value FROM base ORDER BY id;
SELECT id,parent FROM child ORDER BY id;
DELETE t FROM base t JOIN outer_v v ON t.id=v.k WHERE v.k=3;
SELECT ROW_COUNT();
SELECT k,n FROM outer_v ORDER BY k;
SELECT id,temporary_value FROM base ORDER BY id;
SELECT id,parent FROM child ORDER BY id;

CREATE TABLE cascade_base(id INT PRIMARY KEY);
CREATE TABLE cascade_child(id INT PRIMARY KEY, parent INT, FOREIGN KEY(parent) REFERENCES cascade_base(id) ON DELETE CASCADE);
CREATE VIEW cascade_v AS SELECT id AS k FROM cascade_base;
INSERT INTO cascade_base VALUES(5);
INSERT INTO cascade_child VALUES(50,5);
CREATE TEMPORARY TABLE cascade_child(id INT PRIMARY KEY, marker TEXT);
INSERT INTO cascade_child VALUES(50,'private');
DELETE v FROM cascade_v v JOIN cascade_child t ON t.id=v.k*10 WHERE v.k=5;
SELECT ROW_COUNT();
SELECT id,marker FROM cascade_child WHERE id=50;
DROP TEMPORARY TABLE cascade_child;
SELECT COUNT(*) FROM cascade_child WHERE id=50;
SELECT COUNT(*) FROM cascade_base WHERE id=5;
