# Matching-collation text keys in correlated joins

A joined correlated `COUNT` or `EXISTS` over compatible text columns can
group the inner rows once and reuse that result for every outer row. The
lookup applies the shared collation's equality key, including its accent,
case, and trailing-space rules. One tuple can mix text and exact numeric
keys. Ordinary equality excludes NULL on its key while `<=>` can match it.

The [SQL fixture](2026-11-26-correlated-text-keys.sql) checks padded
`utf8mb4_unicode_ci`, NO PAD `utf8mb4_0900_bin`, mixed collation comparisons,
text with CJK characters, and a nullable count argument. The mixed-collation
controls keep the ordinary comparison path. The fixture also checks a
coercive `DOUBLE` filter that produces warning 1292. A speculative pass
keeps its diagnostics and their deduplication state local so ordinary
evaluation can report the warning at the proper point. The strict `UPDATE`
probe retained error 1292 with SQLSTATE `22007` and left both rows unchanged.

The MySQL oracle is 8.4.11, pinned in [the toolchain configuration](../config/toolchain.json).
Both Fsdb storage modes returned the same rows and warning output.
The grouped path also matched the ordinary `LIMIT 1` scalar count and
MySQL across seven `utf8mb4` collations, including the Japanese kana-sensitive
and Chinese 0900 collations.

| Same-host in-memory Fsdb control | Before | After |
| --- | ---: | ---: |
| Examined rows for 206 outer rows and seven inner rows | 3,526 | 262 |
| Median client round trip for 500 outer rows and 128 inner rows | 861.68 ms | 13.01 ms |
| Median round trip with integer tags at the same size | 9.20 ms | 9.25 ms |

The timed control alternated five runs per build after one warm-up and
compared every result row. The baseline was `2d38edca`. Both processes used
the Debug CLI, a loopback connection, and the same full-result query with
an unindexed joined `COUNT` and `EXISTS`.
Outer IDs used three tenants and 41 distinct tags. The inner table used
the same tenants, 37 distinct tags, and a NULL payload on every fourth row.
The integer control alternated nine runs per build after two warm-ups.
