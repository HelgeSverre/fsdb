# Indexed binary-collated LIKE prefixes

Native MySQL 8.4.11 uses `range` access on an indexed
`VARCHAR(20) COLLATE utf8mb4_0900_bin` column for `LIKE 'ab%'`,
`LIKE 'ab_%'`, `LIKE 'é%'`, and `LIKE '😀%'`. The ASCII probes return the
same rows as a table without the index:
`ab` matches only the first pattern, while `abc`, `abZ`, `ab `, and
`ab%` match both. A leading wildcard cannot supply a lower bound.

Fsdb now derives an ordered-key interval from the fixed UTF-8 portion
before the first unescaped wildcard. The query still evaluates its full
`LIKE` predicate on candidates, so `_` and later pattern characters retain
their normal meaning. The upper bound advances the last Unicode scalar with
room for a successor, skipping the surrogate range; an all-maximum prefix has
only a lower bound. The planner chooses the range only when its observed
candidate count beats a scan. This path covers literal patterns with default
backslash or explicit `ESCAPE` semantics, including escaped wildcard characters
and exact patterns, on `utf8mb4_0900_bin` `VARCHAR` keys.
For a prefix-length key such as `v(2)`, storage widens the interval to
include every row sharing the truncated key; the residual `LIKE` test
selects `abc` from an `abc%` probe. Other
collations, column types, expression patterns, and composite-key prefixes
remain open.

The focused Expecto tests first reported `ALL` where MySQL chose `range`.
They now check both ASCII prefix patterns, BMP and supplementary Unicode
prefixes, escaped `_` and `%`, an explicit escape
character, an exact pattern, result parity with a scan, the
leading-wildcard control, and the truncated-index candidate range. The
`binary-like-prefix-range` differential contract compares the returned
rows with native MySQL.

All ten contract steps matched in `20261012T180958894-17978/contracts`
against native MySQL 8.4.11, and the case restored its database objects.
The full 150-case, 19,102-step run retained two previously documented
row differences in the Han REGEXP property and mixed-type join cases.

The [focused quick benchmark](../../benchmarks/results/2cf93f5d-quick.md)
used 10,000 `functional_users` rows and the same `prefix42%` query with
the key enabled or ignored. Fsdb averaged 691 µs indexed versus 21.1 ms
scanning (about 30.5× faster); MySQL averaged 85 µs indexed versus
1.64 ms scanning (about 19.3× faster). Fsdb's indexed read remained about
8.1× slower than MySQL's. These are three-iteration ShortRun measurements
on one host, not a broader throughput or durability claim.
