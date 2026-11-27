# Bulk full-text stopwords, casing, and document feedback

MySQL 8.4.11 checks the source spelling against stopwords when building
an InnoDB full-text index. It lowercases the token for the sort key even
when the index uses a binary collation. This applies to the ngram and
default parsers. The document write path uses its own casing and
stopword rules. [MySQL bulk tokenization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/ddl/ddl0fts.cc#L628-L670)
[MySQL write tokenization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0fts.cc#L4278-L4332)

```sql
SET SESSION innodb_ft_enable_stopword=OFF;
CREATE TABLE docs(id INT PRIMARY KEY,a TEXT,b TEXT) COLLATE utf8mb4_bin;
INSERT INTO docs VALUES(1,'東京','京Ａ'),(2,'京ａ','雑音'),(3,'京都','韓国');
ALTER TABLE docs ADD FULLTEXT(a,b) WITH PARSER ngram;
INSERT INTO docs VALUES(4,'別文','京Ａ');
SELECT id FROM docs WHERE MATCH(a,b) AGAINST('京ａ') ORDER BY id;
-- 1, 2
SELECT id FROM docs WHERE MATCH(a,b) AGAINST('京Ａ') ORDER BY id;
-- 4
SELECT id FROM docs
WHERE MATCH(a,b) AGAINST('東京' WITH QUERY EXPANSION) ORDER BY id;
-- 1, 4
```

Phrase matching starts from positions retained in the posting. For
example, a stopped ASCII spelling in one column cannot anchor a
phrase using an equivalent retained fullwidth spelling in a different
column. Query expansion fetches the original document tokens. In the
example, row 1 supplies the uppercase binary term, which finds row 4.
[MySQL relevance feedback](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3896-L3971)

A 22,320-query matrix covered bulk-only and mixed bulk-and-write
histories, five UTF-8 collations, both parsers, built-in and custom
stopwords, and disabled filtering. Results were compared by row ID and
relevance, with a relative tolerance of 0.000005 for scores.

| Parser | Matches before | Matches after | Queries |
|---|---:|---:|---:|
| ngram | 7,466 | 11,080 | 11,160 |
| default | 8,971 | 10,890 | 11,160 |
| Total | 16,437 | 21,970 | 22,320 |

All 3,720 query-expansion cases matched. No previously matching case
regressed. The remaining differences cover wildcard behavior and
`utf8mb4_general_ci` ngram keys involving dotted and dotless I. The
previous 180-case stopword grouping matrix now matches in full.

All 45 checks using mixed stored documents matched the oracle and
reproduced after WAL-only recovery and checkpoint reload. The FSNZ
snapshot records the document's build path. An FSNY checkpoint made
before the change reloaded with its historical search results.
