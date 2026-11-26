SET SESSION innodb_ft_enable_stopword = OFF;

CREATE TABLE rank_probe (
    id INT PRIMARY KEY,
    body TEXT,
    FULLTEXT (body) WITH PARSER ngram
) COLLATE utf8mb4_0900_ai_ci;

-- U+034F has no weight in this collation. The gram containing U+034F
-- and 快 shares the exact query key with the wildcard prefix 快*.
INSERT INTO rank_probe VALUES
    (1,'生日͏快乐 快乐 快乐'), (2,'͏快加 ͏快加 ͏快加'), (3,'大阪'), (4,'京都'),
    (5,'東京'), (6,'神戸'), (7,'松山');

SELECT id,
    ROUND(MATCH(body) AGAINST('"快"' IN BOOLEAN MODE),6) AS exact_term,
    ROUND(MATCH(body) AGAINST('快*' IN BOOLEAN MODE),6) AS prefix_term,
    ROUND(MATCH(body) AGAINST('"快" +快*' IN BOOLEAN MODE),6) AS exact_before_prefix,
    ROUND(MATCH(body) AGAINST('快* "快"' IN BOOLEAN MODE),6) AS prefix_before_exact,
    ROUND(MATCH(body) AGAINST('"快" 快*' IN BOOLEAN MODE),6) AS optional_prefix
FROM rank_probe WHERE id IN (1,2) ORDER BY id;

DROP TABLE rank_probe;
