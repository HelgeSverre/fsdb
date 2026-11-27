SET SESSION innodb_ft_enable_stopword=OFF;
CREATE DATABASE fsdb_ngram_accent_fixture;
USE fsdb_ngram_accent_fixture;
CREATE TABLE cjk(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body) WITH PARSER ngram) COLLATE=utf8mb4_0900_as_ci;
INSERT INTO cjk VALUES
  (1,'㍿中'),(2,'株中'),(3,'㌔中'),(4,'キ中'),(5,'ヿ中'),(6,'こと中'),
  (7,'ゟ中'),(8,'より中'),(9,'한中'),(10,'하中'),(11,'ᄒ中'),(12,'花束'),(13,'㌐中');

CREATE TABLE accents(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT ft(a,b) WITH PARSER ngram) COLLATE=utf8mb4_0900_as_ci;
INSERT INTO accents VALUES (1,'e中','日e'),(2,'é中','日é'),(3,'é中','日é'),(4,'㍿中','日㍿'),(5,'株中','日株'),(6,'花束','東京');

SELECT 'cached Han',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('株*' IN BOOLEAN MODE);
SELECT 'cached Katakana',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('キ*' IN BOOLEAN MODE);
SELECT 'cached Kana ligature',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('ゟ*' IN BOOLEAN MODE);
SELECT 'cached Hangul',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('하*' IN BOOLEAN MODE);
SELECT 'cached voiced Katakana',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('ギ*' IN BOOLEAN MODE);
SELECT 'cached accented e',GROUP_CONCAT(id ORDER BY id) FROM accents WHERE MATCH(a,b) AGAINST('é*' IN BOOLEAN MODE);

SET GLOBAL innodb_optimize_fulltext_only=ON;
OPTIMIZE TABLE cjk;
OPTIMIZE TABLE accents;
SET GLOBAL innodb_optimize_fulltext_only=OFF;

SELECT 'flushed Han',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('株*' IN BOOLEAN MODE);
SELECT 'flushed Katakana',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('キ*' IN BOOLEAN MODE);
SELECT 'flushed Kana ligature',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('ゟ*' IN BOOLEAN MODE);
SELECT 'flushed Hangul',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('하*' IN BOOLEAN MODE);
SELECT 'flushed voiced Katakana',GROUP_CONCAT(id ORDER BY id) FROM cjk WHERE MATCH(body) AGAINST('ギ*' IN BOOLEAN MODE);
SELECT 'flushed accented e',GROUP_CONCAT(id ORDER BY id) FROM accents WHERE MATCH(a,b) AGAINST('é*' IN BOOLEAN MODE);

CREATE TABLE cache_order(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT ft(a,b) WITH PARSER ngram) COLLATE=utf8mb4_0900_as_ci;
INSERT INTO cache_order VALUES
  (1,'中中 中花','田中 上海'),(2,'花中 花花','田花 上海'),(3,'株中 株花','田株 上海'),
  (4,'㍿中 ㍿花','田㍿ 上海'),(5,'ｷﾞ中 ｷﾞ花','田ｷﾞ 上海'),
  (6,'ǽ中 ǽ花','田ǽ 上海'),(7,'㋿中 ㋿花','田㋿ 上海');
SELECT 'cached Han after combining mark',GROUP_CONCAT(id ORDER BY id)
FROM cache_order WHERE MATCH(a,b) AGAINST('花*' IN BOOLEAN MODE);

DROP DATABASE fsdb_ngram_accent_fixture;
