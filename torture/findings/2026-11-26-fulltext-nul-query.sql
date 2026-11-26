SET SESSION innodb_ft_enable_stopword = OFF;

CREATE TABLE folded (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body) WITH PARSER ngram
) COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE binary_grams (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body) WITH PARSER ngram
) COLLATE utf8mb4_0900_bin;

CREATE TABLE turkish (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body) WITH PARSER ngram
) COLLATE utf8mb4_turkish_ci;

INSERT INTO folded VALUES
    (1,'生日'), (2,'快乐'), (3,'生日快乐'), (4,'生日\0快乐'), (5,'大阪');
INSERT INTO binary_grams SELECT * FROM folded;
INSERT INTO turkish SELECT * FROM folded;

SELECT id,
    ROUND(MATCH(body) AGAINST('生日\0快乐'),6) AS natural_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' IN BOOLEAN MODE),6) AS boolean_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' WITH QUERY EXPANSION),6) AS expansion_score,
    ROUND(MATCH(body) AGAINST('"生日\0快乐"' IN BOOLEAN MODE),6) AS quoted_score,
    ROUND(MATCH(body) AGAINST('生日\0)' IN BOOLEAN MODE),6) AS suffix_score,
    ROUND(MATCH(body) AGAINST('\0快乐' IN BOOLEAN MODE),6) AS leading_score,
    ROUND(MATCH(body) AGAINST('快乐'),6) AS tail_score
FROM folded ORDER BY id;

SELECT id,
    ROUND(MATCH(body) AGAINST('生日\0快乐'),6) AS natural_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' IN BOOLEAN MODE),6) AS boolean_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' WITH QUERY EXPANSION),6) AS expansion_score,
    ROUND(MATCH(body) AGAINST('"生日\0快乐"' IN BOOLEAN MODE),6) AS quoted_score
FROM binary_grams ORDER BY id;

SELECT id,
    ROUND(MATCH(body) AGAINST('生日\0快乐'),6) AS natural_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' IN BOOLEAN MODE),6) AS boolean_score,
    ROUND(MATCH(body) AGAINST('生日\0快乐' WITH QUERY EXPANSION),6) AS expansion_score,
    ROUND(MATCH(body) AGAINST('"生日\0快乐"' IN BOOLEAN MODE),6) AS quoted_score
FROM turkish ORDER BY id;

CREATE TABLE words (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body)
) COLLATE utf8mb4_0900_ai_ci;

INSERT INTO words VALUES (1,'生日会'), (2,'庆祝会'), (3,'大阪府');

SELECT id,
    ROUND(MATCH(body) AGAINST('生日会\0庆祝会'),6) AS natural_score,
    ROUND(MATCH(body) AGAINST('生日会\0)' IN BOOLEAN MODE),6) AS boolean_score,
    ROUND(MATCH(body) AGAINST('生日会\0庆祝会' WITH QUERY EXPANSION),6) AS expansion_score
FROM words ORDER BY id;

DROP TABLE words, turkish, binary_grams, folded;
