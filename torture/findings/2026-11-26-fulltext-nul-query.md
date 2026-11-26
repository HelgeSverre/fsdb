# Embedded NUL in UTF-8 full-text queries

For a full-text index using `utf8mb4_0900_ai_ci`, MySQL 8.4.11 treats
`生日\0快乐` as `生日` in natural language mode, Boolean mode, and query
expansion. It also discards a closing parenthesis after the NUL before the
Boolean parser sees it. Previously fsdb could return rows for the discarded
term or reject the query because of the discarded syntax.

MySQL's in-place UTF-8 lowercase conversion ends at the first NUL. The
binary collations and the legacy Turkish UTF-8 collations use length-aware
paths and retain both sides. The boundary applies to the query. A document
containing `生日\0快乐` still has searchable terms on both sides.
[MySQL query setup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3439-L3467)
and [lowercase conversion](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/handler/ha_innodb.cc#L7477-L7493).

The [wire fixture](2026-11-26-fulltext-nul-query.sql) matched all 18 result
rows, including relevance rounded to six decimal places, against the
[pinned MySQL 8.4.11 image](../config/toolchain.json). It covers the built-in
word parser, bigrams, leading NUL, quoted phrases, malformed Boolean syntax
after NUL, binary and legacy Turkish exceptions, and searchable document
content after NUL. The Expecto regression also covers legacy Unicode
collations and the single-term scoring path. The maintained
`ngram-fulltext` differential contract passed all 283 steps.

A separate two-column bigram corpus compared 2,929 Boolean searches for
each collation against MySQL 8.4.11. Both servers disabled stopwords for
the fixture. MySQL ran `ANALYZE TABLE` after loading the 39 documents to
stabilize its document count. Matches require the same row IDs and scores
within `1e-5` relative or absolute tolerance.

| Collation | Matches at `bfdc7367` | Matches with the fix |
| --- | ---: | ---: |
| `utf8mb4_bin` | 2,929 | 2,929 |
| `utf8mb4_0900_ai_ci` | 2,676 | 2,908 |
| `utf8mb4_unicode_520_ci` | 2,676 | 2,908 |
| Total | 8,281 | 8,745 |

The remaining differences include Boolean relevance when a quoted term
and wildcard prefix overlap. A three-document probe under
`utf8mb4_0900_ai_ci` matched the same document for each of `"快"`,
`快*`, and `"快" +快*`. The first two searches agreed in relevance.
For the combined search, MySQL scored the row near zero and fsdb scored
it at approximately 0.258653. The matching document had `生日\u034F快乐`
in its first indexed column and `生\u2060日` in its second.
The [shared-relevance follow-up](2026-11-26-ngram-shared-relevance.md)
closed all 42 differences in this sweep.
