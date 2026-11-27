CREATE DATABASE fsdb_ngram_kana_fixture;
USE fsdb_ngram_kana_fixture;
CREATE TABLE legacy(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT ft(a,b) WITH PARSER ngram) COLLATE=utf8mb3_unicode_ci;
INSERT INTO legacy VALUES
  (1,'か中','日か'),(2,'ガ中','日ガ'),(3,'ｶ中','日ｶ'),(4,'ゕ中','日ゕ'),
  (5,'が中','日が'),(6,'ｶﾞ中','日ｶﾞ'),(7,'하中','日하'),(8,'하中','日하'),(9,'ᄒ中','日ᄒ');

SELECT 'exact Kana',GROUP_CONCAT(id ORDER BY id) FROM legacy WHERE MATCH(a,b) AGAINST('ガ中');
SELECT 'Kana wildcard',GROUP_CONCAT(id ORDER BY id) FROM legacy WHERE MATCH(a,b) AGAINST('ｶ*' IN BOOLEAN MODE);
SELECT 'Hangul syllable wildcard',GROUP_CONCAT(id ORDER BY id) FROM legacy WHERE MATCH(a,b) AGAINST('하*' IN BOOLEAN MODE);
SELECT 'Jamo wildcard',GROUP_CONCAT(id ORDER BY id) FROM legacy WHERE MATCH(a,b) AGAINST('ᄒ*' IN BOOLEAN MODE);

CREATE TABLE ligatures(id INT PRIMARY KEY,a TEXT,FULLTEXT ft(a) WITH PARSER ngram) COLLATE=utf8mb4_0900_ai_ci;
INSERT INTO ligatures VALUES(1,'ヿ中'),(2,'こと中'),(3,'ゟ中'),(4,'より中'),(5,'花束');
SELECT 'Katakana ligature wildcard',GROUP_CONCAT(id ORDER BY id) FROM ligatures WHERE MATCH(a) AGAINST('ヿ*' IN BOOLEAN MODE);
SELECT 'Katakana leading weight',GROUP_CONCAT(id ORDER BY id) FROM ligatures WHERE MATCH(a) AGAINST('こ*' IN BOOLEAN MODE);
SELECT 'Hiragana ligature wildcard',GROUP_CONCAT(id ORDER BY id) FROM ligatures WHERE MATCH(a) AGAINST('ゟ*' IN BOOLEAN MODE);
SELECT 'Hiragana leading weight',GROUP_CONCAT(id ORDER BY id) FROM ligatures WHERE MATCH(a) AGAINST('よ*' IN BOOLEAN MODE);

DROP DATABASE fsdb_ngram_kana_fixture;
