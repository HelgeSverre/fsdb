CREATE TABLE customers (
    id INT PRIMARY KEY,
    tenant VARCHAR(24),
    segment INT,
    lower_bound INT,
    upper_bound INT
) COLLATE utf8mb4_0900_ai_ci;

CREATE TABLE customer_events (
    id INT PRIMARY KEY,
    tenant VARCHAR(24),
    segment INT,
    seq INT,
    amount INT,
    KEY ix_full (tenant, segment DESC, seq DESC),
    KEY ix_short (tenant(3), segment DESC, seq DESC)
) COLLATE utf8mb4_0900_ai_ci;

INSERT INTO customers VALUES
    (1,'CAFÉ',1,3,6), (2,'cafe',2,3,6), (3,'cafe ',1,3,6),
    (4,NULL,1,3,6), (5,'CAFÉ',1,6,3), (6,'alphabet',1,3,6);
INSERT INTO customer_events VALUES
    (1,'cafe',1,3,11), (2,'Cafe',1,4,12), (3,'café',1,6,13),
    (4,'CAFÉ',1,2,14), (5,'cafe',2,6,15), (6,'CAFÉ',2,5,NULL),
    (7,'cafe ',1,4,17), (8,'café',1,NULL,18), (9,NULL,1,4,19),
    (10,'ALPHA',1,4,20), (11,'alphabet',1,4,21),
    (12,'alphabetical',1,4,22);

WITH events AS (SELECT tenant AS owner, segment AS shard, seq AS sequence_no, amount FROM customer_events)
SELECT p.id,
    (SELECT COUNT(*) FROM customer_events c WHERE c.tenant=p.tenant AND c.segment=p.segment AND c.seq BETWEEN p.lower_bound AND p.upper_bound) AS tally,
    (SELECT COUNT(c.amount) FROM customer_events c WHERE c.tenant=p.tenant AND c.segment=p.segment AND c.seq<=p.upper_bound) AS upper_only,
    (SELECT SUM(c.amount) FROM events c WHERE p.tenant=c.owner AND p.segment=c.shard AND c.sequence_no BETWEEN p.lower_bound AND p.upper_bound) AS total,
    (SELECT COUNT(*) FROM customer_events c FORCE INDEX (ix_short) WHERE c.tenant=p.tenant AND c.segment=p.segment AND c.seq BETWEEN p.lower_bound AND p.upper_bound) AS prefix_index_tally,
    (SELECT COUNT(*) FROM customer_events c WHERE c.tenant COLLATE utf8mb4_0900_bin=p.tenant AND c.segment=p.segment AND c.seq BETWEEN p.lower_bound AND p.upper_bound) AS binary_tally,
    (SELECT COUNT(*) FROM customer_events c WHERE c.tenant='CAFÉ' AND c.segment=p.segment AND c.seq BETWEEN p.lower_bound AND p.upper_bound) AS literal_tally
FROM customers p ORDER BY p.id;

ALTER TABLE customers CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
ALTER TABLE customer_events CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

SELECT p.id,
    (SELECT COUNT(*) FROM customer_events c WHERE c.tenant=p.tenant AND c.segment=p.segment AND c.seq BETWEEN p.lower_bound AND p.upper_bound) AS pad_space_tally
FROM customers p ORDER BY p.id;

DROP TABLE customer_events, customers;
