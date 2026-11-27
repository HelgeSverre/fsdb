CREATE DATABASE fsdb_ignored_coercions;
USE fsdb_ignored_coercions;
CREATE TABLE typed (
    id INT PRIMARY KEY,
    signed_val TINYINT NOT NULL DEFAULT 12,
    unsigned_val INT UNSIGNED NOT NULL DEFAULT 20,
    description VARCHAR(3) NOT NULL DEFAULT 'abc',
    date_val DATE NOT NULL DEFAULT '2026-01-01'
);
INSERT INTO typed VALUES (1,5,5,'ini','2026-01-01'),(2,6,6,'ini','2026-01-01'),(3,7,7,'ini','2026-01-01');
UPDATE IGNORE typed SET signed_val='bad',unsigned_val='invalid' WHERE id=2;
SHOW WARNINGS;
SELECT id,signed_val,unsigned_val FROM typed ORDER BY id;
UPDATE IGNORE typed SET signed_val=999,unsigned_val=-1,description='longer' WHERE id IN (1,3) ORDER BY id DESC;
SHOW WARNINGS;
SELECT id,signed_val,unsigned_val,description FROM typed ORDER BY id;
UPDATE IGNORE typed SET signed_val=NULL,unsigned_val=NULL,description=NULL,date_val=NULL WHERE id=2;
SHOW WARNINGS;
SELECT id,signed_val,unsigned_val,description,date_val FROM typed WHERE id=2;
UPDATE IGNORE typed SET date_val='not-a-date' WHERE id=3;
SHOW WARNINGS;
SELECT id,date_val FROM typed ORDER BY id;
CREATE TABLE observed(id INT PRIMARY KEY,n INT NOT NULL DEFAULT 12,seen VARCHAR(20));
INSERT INTO observed VALUES(1,5,'');
CREATE TRIGGER observe BEFORE UPDATE ON observed FOR EACH ROW SET NEW.seen=CONCAT(NEW.seen,':',NEW.n);
UPDATE IGNORE observed SET n='bad',seen=n WHERE id=1;
SHOW WARNINGS;
SELECT id,n,seen FROM observed;
UPDATE IGNORE observed SET n=NULL,seen=n WHERE id=1;
SHOW WARNINGS;
SELECT id,n,seen FROM observed;

CREATE TABLE trigger_updates(id INT PRIMARY KEY,n INT NOT NULL,b INT NOT NULL);
CREATE TABLE audit(stage VARCHAR(20));
INSERT INTO trigger_updates VALUES(1,5,10);
DELIMITER $$
CREATE TRIGGER set_null BEFORE UPDATE ON trigger_updates FOR EACH ROW BEGIN
    INSERT INTO audit VALUES('before');
    SET NEW.b=NULL;
    SET NEW.n=NULL;
    IF NEW.id=2 THEN
        SET NEW.b=8;
        SET NEW.n=9;
    END IF;
    INSERT INTO audit VALUES('after');
END$$
DELIMITER ;
UPDATE IGNORE trigger_updates SET n=6,b=12;
SHOW WARNINGS;
SELECT * FROM trigger_updates;
SELECT stage FROM audit ORDER BY stage;
INSERT INTO trigger_updates VALUES(2,5,10);
UPDATE IGNORE trigger_updates SET n=NULL,b=12 WHERE id=2;
SHOW WARNINGS;
SELECT * FROM trigger_updates ORDER BY id;

CREATE TABLE computed(id INT PRIMARY KEY,n INT NOT NULL,g INT AS(COALESCE(n,100)) STORED,CHECK(g<50));
INSERT INTO computed(id,n) VALUES(1,5),(2,7);
UPDATE IGNORE computed SET n=NULL WHERE id=1;
SHOW WARNINGS;
SELECT * FROM computed ORDER BY id;

CREATE TABLE wanted(id INT PRIMARY KEY);
INSERT INTO wanted VALUES(1),(2);
UPDATE IGNORE computed a JOIN wanted t ON t.id=a.id SET a.n='bad';
SHOW WARNINGS;
SELECT * FROM computed ORDER BY id;

CREATE TABLE special(id INT PRIMARY KEY,e ENUM('first','second') NOT NULL,j JSON NOT NULL,p POINT NOT NULL);
INSERT INTO special VALUES(1,'second','{}',POINT(1,1));
UPDATE IGNORE special SET e='bad',j=NULL WHERE id=1;
SHOW WARNINGS;
SELECT e,e+0,j FROM special;
UPDATE IGNORE special SET p=NULL WHERE id=1;
SHOW WARNINGS;
SELECT ST_AsText(p) FROM special;

CREATE TABLE floating_values(id INT PRIMARY KEY,d DOUBLE,f FLOAT);
INSERT INTO floating_values VALUES(1,1,1);
UPDATE IGNORE floating_values SET d='bad',f='bad' WHERE id=1;
SHOW WARNINGS;
SELECT id,d,f FROM floating_values;
DROP DATABASE fsdb_ignored_coercions;
