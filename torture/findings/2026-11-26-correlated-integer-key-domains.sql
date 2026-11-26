CREATE TABLE range_bounds (
    id INT PRIMARY KEY,
    tenant BIGINT UNSIGNED,
    wide_low BIGINT UNSIGNED,
    wide_high BIGINT UNSIGNED,
    unsigned_low INT UNSIGNED,
    unsigned_high INT UNSIGNED,
    signed_low BIGINT,
    signed_high BIGINT
);

CREATE TABLE range_events (
    id INT PRIMARY KEY,
    tenant BIGINT UNSIGNED,
    wide_key BIGINT UNSIGNED,
    unsigned_key INT UNSIGNED,
    signed_key BIGINT,
    payload INT,
    KEY ix_wide (tenant, wide_key DESC, id),
    KEY ix_unsigned (tenant, unsigned_key, id),
    KEY ix_signed (tenant, signed_key DESC, id)
);

INSERT INTO range_bounds VALUES
    (1, 18446744073709551615, 9223372036854775808, 18446744073709551615,
        4294967294, 4294967295, -9223372036854775808, -9223372036854775807),
    (2, 18446744073709551614, 18446744073709551614, 18446744073709551615,
        4294967295, 4294967295, 9223372036854775806, 9223372036854775807),
    (3, 18446744073709551613, 18446744073709551615, 18446744073709551614,
        4294967295, 4294967294, 1, 0),
    (4, NULL, NULL, 18446744073709551615, NULL, 4294967295, NULL, 0);

INSERT INTO range_events VALUES
    (10, 18446744073709551615, NULL, NULL, NULL, 50),
    (11, 18446744073709551615, 9223372036854775807, 4294967293, -9223372036854775808, 1),
    (12, 18446744073709551615, 9223372036854775808, 4294967294, -9223372036854775807, 2),
    (13, 18446744073709551615, 18446744073709551615, 4294967295, -9223372036854775806, NULL),
    (14, 18446744073709551614, 18446744073709551613, 4294967294, 9223372036854775806, 3),
    (15, 18446744073709551614, 18446744073709551614, 4294967295, 9223372036854775807, 4),
    (16, 18446744073709551614, 18446744073709551615, 4294967295, 9223372036854775807, 5),
    (17, 18446744073709551613, 18446744073709551615, 4294967295, 1, 6),
    (18, NULL, 18446744073709551615, 4294967295, 0, 7);

SELECT p.id,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND c.wide_key BETWEEN p.wide_low AND p.wide_high) AS wide_count,
    (SELECT COUNT(c.payload) FROM range_events c WHERE c.tenant=p.tenant AND c.wide_key BETWEEN p.wide_low AND p.wide_high) AS payload_count,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND c.wide_key<p.wide_high) AS upper_only,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND p.unsigned_low<=c.unsigned_key AND c.unsigned_key<=p.unsigned_high) AS unsigned_count,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND c.signed_key BETWEEN p.signed_low AND p.signed_high) AS signed_count
FROM range_bounds p ORDER BY p.id;

WITH projected AS (SELECT tenant AS owner, wide_key AS reading, payload AS amount FROM range_events)
SELECT p.id,
    (SELECT SUM(c.amount) FROM projected c WHERE c.owner=p.tenant AND c.reading BETWEEN p.wide_low AND p.wide_high) AS total,
    (SELECT MIN(c.reading) FROM projected c WHERE c.owner=p.tenant AND c.reading>=p.wide_low) AS minimum,
    (SELECT COUNT(*) FROM range_events c IGNORE INDEX(ix_wide) WHERE c.tenant=p.tenant AND c.wide_key BETWEEN p.wide_low AND p.wide_high) AS index_ignored,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND c.wide_key>=18446744073709551615) AS literal_bound,
    (SELECT COUNT(*) FROM range_events c WHERE c.tenant=p.tenant AND c.unsigned_key<=p.wide_high) AS well_above_int
FROM range_bounds p ORDER BY p.id;

DROP TABLE range_events, range_bounds;
