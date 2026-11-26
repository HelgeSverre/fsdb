CREATE TABLE phrase_probe (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body) WITH PARSER ngram
) COLLATE utf8mb4_0900_ai_ci;

INSERT INTO phrase_probe VALUES
    (1,'東京駅'), (2,'東京'), (3,'2東京'),
    (4,'22'), (5,'北京'), (6,'東京駅 22'),
    (7,'東京駅 北京'), (8,'0東京'), (9,'２０東京');

SELECT id, ROUND(MATCH(body) AGAINST('"東京駅" @2東京' IN BOOLEAN MODE),6) AS score
FROM phrase_probe HAVING score <> 0 ORDER BY id;

SELECT id, ROUND(MATCH(body) AGAINST('"東京駅"@22' IN BOOLEAN MODE),6) AS score
FROM phrase_probe HAVING score <> 0 ORDER BY id;

SELECT id, ROUND(MATCH(body) AGAINST('"東京駅" @２０東京' IN BOOLEAN MODE),6) AS score
FROM phrase_probe HAVING score <> 0 ORDER BY id;

SELECT id, ROUND(MATCH(body) AGAINST('+"東京駅" @22' IN BOOLEAN MODE),6) AS score
FROM phrase_probe HAVING score <> 0 ORDER BY id;

SELECT id, ROUND(MATCH(body) AGAINST('("東京駅"@22 -北京)' IN BOOLEAN MODE),6) AS score
FROM phrase_probe HAVING score <> 0 ORDER BY id;

SELECT ROUND(MATCH(body) AGAINST('"東京駅" @8東京' IN BOOLEAN MODE),6),
       ROUND(MATCH(body) AGAINST('"東京駅" @東京8' IN BOOLEAN MODE),6),
       ROUND(MATCH(body) AGAINST('"東京駅" @東京8東京' IN BOOLEAN MODE),6),
       ROUND(MATCH(body) AGAINST('"東京駅" @8東京駅' IN BOOLEAN MODE),6)
FROM phrase_probe WHERE id = 1;

DROP TABLE phrase_probe;
