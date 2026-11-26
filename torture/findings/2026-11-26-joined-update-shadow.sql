CREATE DATABASE update_shadow_probe;
USE update_shadow_probe;
CREATE TABLE base(id INT PRIMARY KEY,n INT);
INSERT INTO base VALUES(1,10),(2,20),(3,30),(4,40);
CREATE VIEW inner_v AS SELECT id AS k,n FROM base WHERE n>0 WITH CASCADED CHECK OPTION;
CREATE VIEW outer_v AS SELECT * FROM inner_v WITH CASCADED CHECK OPTION;
DELIMITER //
CREATE TRIGGER refuse_update BEFORE UPDATE ON base FOR EACH ROW BEGIN
  IF NEW.n=99 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='blocked update'; END IF;
END//
DELIMITER ;
CREATE TEMPORARY TABLE base(id INT PRIMARY KEY,n VARCHAR(12));
INSERT INTO base VALUES(1,'temp1'),(2,'temp2'),(3,'temp3'),(4,'temp4');
UPDATE outer_v v JOIN base t ON t.id=v.k SET v.n=v.n+1 WHERE v.k=1;
SELECT ROW_COUNT();
UPDATE base t JOIN outer_v v ON t.id=v.k SET t.n='private',v.n=v.n+2 WHERE v.k=2;
SELECT ROW_COUNT();
UPDATE base t JOIN outer_v v ON t.id=v.k SET t.n='rollback',v.n=99 WHERE v.k=3;
UPDATE base t JOIN outer_v v ON t.id=v.k SET t.n='check',v.n=-1 WHERE v.k=4;
UPDATE outer_v v JOIN base t ON t.id=v.k SET v.n=v.n+8,t.id=2 WHERE v.k=1;
SELECT k,n FROM outer_v ORDER BY k;
SELECT id,n FROM base ORDER BY id;
START TRANSACTION;
UPDATE outer_v v JOIN base t ON t.id=v.k SET v.n=v.n+7,t.n='tx' WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM outer_v WHERE k=1;
SELECT id,n FROM base WHERE id=1;
ROLLBACK;
SELECT k,n FROM outer_v WHERE k=1;
SELECT id,n FROM base WHERE id=1;
CREATE TABLE articles(id INT PRIMARY KEY,body VARCHAR(100),marked INT,FULLTEXT KEY ix_body(body) WITH PARSER ngram) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
INSERT INTO articles VALUES(1,'東京駅鉄道',0),(2,'東京の鉄道駅',0),(3,'北京地铁',0);
CREATE VIEW article_v AS SELECT id,body,marked FROM articles;
CREATE TEMPORARY TABLE articles(id INT PRIMARY KEY,note VARCHAR(20));
INSERT INTO articles VALUES(1,'other1'),(2,'other2'),(3,'other3');
UPDATE article_v v JOIN articles t ON t.id=v.id
SET v.marked=1,t.note='hit' WHERE MATCH(v.body) AGAINST ('"東京駅"' IN BOOLEAN MODE);
SELECT ROW_COUNT();
SELECT id,marked FROM article_v ORDER BY id;
SELECT id,note FROM articles ORDER BY id;

CREATE TABLE joined_base(id INT PRIMARY KEY,n INT);
CREATE TABLE joined_partner(id INT PRIMARY KEY,delta INT);
INSERT INTO joined_base VALUES(1,100);
INSERT INTO joined_partner VALUES(1,5);
CREATE VIEW joined_v AS SELECT b.id,b.n,p.delta FROM joined_base b JOIN joined_partner p ON p.id=b.id;
CREATE TEMPORARY TABLE joined_partner(id INT PRIMARY KEY,note VARCHAR(20));
INSERT INTO joined_partner VALUES(1,'private');
UPDATE joined_v v JOIN joined_partner t ON t.id=v.id SET v.n=v.n+v.delta,t.note='changed' WHERE v.id=1;
SELECT ROW_COUNT();
SELECT id,n,delta FROM joined_v;
SELECT id,note FROM joined_partner;

CREATE TABLE nested_data(id INT PRIMARY KEY,n INT);
CREATE TABLE nested_lookup(id INT PRIMARY KEY,delta INT);
CREATE TABLE nested_flags(id INT PRIMARY KEY,enabled INT);
INSERT INTO nested_data VALUES(1,10);
INSERT INTO nested_lookup VALUES(1,5);
INSERT INTO nested_flags VALUES(1,1);
CREATE VIEW correlated_v AS SELECT b.id,b.n,p.delta FROM nested_data b JOIN nested_lookup p ON p.id=b.id AND EXISTS(SELECT 1 FROM nested_flags f WHERE f.id=b.id AND f.enabled=1);
CREATE VIEW correlated_where_v AS SELECT b.id,b.n FROM nested_data b WHERE EXISTS(SELECT 1 FROM nested_flags f WHERE f.id=b.id AND f.enabled=1);
CREATE VIEW shared_predicate AS SELECT id,n FROM nested_data WHERE EXISTS(SELECT 1 FROM nested_flags f WHERE f.enabled=1);
CREATE TEMPORARY TABLE nested_flags(id INT PRIMARY KEY,enabled INT);
INSERT INTO nested_flags VALUES(1,0);
UPDATE correlated_v v JOIN nested_flags t ON t.id=v.id SET v.n=v.n+v.delta,t.enabled=2 WHERE v.id=1;
SELECT ROW_COUNT();
SELECT id,n,delta FROM correlated_v;
SELECT id,enabled FROM nested_flags;
UPDATE correlated_where_v v JOIN nested_flags t ON t.id=v.id SET v.n=v.n+4,t.enabled=3 WHERE v.id=1;
SELECT ROW_COUNT();
SELECT id,n FROM correlated_where_v;
SELECT id,enabled FROM nested_flags;
UPDATE shared_predicate v JOIN nested_lookup t ON t.id=v.id SET v.n=v.n+100
WHERE v.id=1 AND EXISTS(SELECT 1 FROM nested_flags f WHERE f.enabled=1);
SELECT ROW_COUNT();
SELECT id,n FROM shared_predicate;

CREATE TABLE second(id INT PRIMARY KEY,m INT);
INSERT INTO second VALUES(1,50);
CREATE VIEW second_v AS SELECT * FROM second;
UPDATE outer_v v JOIN second_v s ON s.id=v.k JOIN base t ON t.id=v.k SET v.n=v.n+1,s.m=s.m+1,t.n='all' WHERE v.k=1;
SELECT ROW_COUNT();
SELECT k,n FROM outer_v WHERE k=1;
SELECT id,m FROM second_v;
SELECT id,n FROM base WHERE id=1;
