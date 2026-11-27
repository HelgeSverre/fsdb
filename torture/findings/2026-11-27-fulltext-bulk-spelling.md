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
regressed. Differences after that pass covered wildcard behavior and
`utf8mb4_general_ci` ngram keys involving dotted and dotless I. The
previous 180-case stopword grouping matrix now matches in full.

All 45 checks using mixed stored documents matched the oracle and
reproduced after WAL-only recovery and checkpoint reload. The FSNZ
snapshot records the document's build path. An FSNY checkpoint made
before the change reloaded with its historical search results.

## Dotted and dotless I

Both `utf8mb3_general_ci` and `utf8mb4_general_ci` give `I`, `i`, `İ`,
and `ı` the primary weight `0049`. Fullwidth `Ｉ` has the separate
weight `FF29`. The pinned case table already lowercases `İ` to `i`.
The full-text key now folds the remaining dotless `ı` to that same
posting key. [MySQL legacy collation tables](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-utf8.cc)

```sql
SET SESSION innodb_ft_enable_stopword=OFF;
CREATE TABLE docs(id INT PRIMARY KEY,a TEXT,b TEXT) COLLATE utf8mb4_general_ci;
INSERT INTO docs VALUES (1,'京İ花','京ı花'),(2,'雑音',''),(3,'東京',''),(4,'京Ｉ花','');
ALTER TABLE docs ADD FULLTEXT(a,b) WITH PARSER ngram;
SELECT id, MATCH(a,b) AGAINST('京i' IN BOOLEAN MODE) AS score
FROM docs WHERE MATCH(a,b) AGAINST('京i' IN BOOLEAN MODE) ORDER BY id;
-- 1, 0.7249524593353271 on MySQL 8.4.11
```

The score previously counted one occurrence and was about 0.3624762.
All 36 focused wire checks match, including natural and Boolean searches,
quoted terms, and the fullwidth control. A checkpoint with the two old
cache keys syncs the merged postings on load, including retained deletes.
All 48 recovery queries match MySQL after loading WAL and checkpoints
written both before and after this change.
All 378 custom-stopword checks also match across the two general
collations and `utf8mb4_bin`, varying the stopword table and index
collations independently in bulk, mixed, and incremental histories.

The 22,320-query matrix now matches 22,060 results, up from 21,970.
The ngram parser gained 72 matches and the default parser gained 18,
with no regressions. All 260 remaining differences in that matrix are
Boolean wildcard searches. SQL comparison and weight-string handling
of dotless I remain open.

## Boolean wildcard relevance

In a default-parser probe, eight documents include three with words
starting with `京ap`. The first matching document contains three
`京apple`, one `京apply`, and two `京apex`. The other matching documents
contain three `京apply` and one `京apex`. For `京ap*`, MySQL uses a
document frequency of five, the sum of matching word postings, and
the first visited word's frequency for each row. The first document
scores about 0.08333 after a bulk build and 0.12499 when it is in the
write cache. Fsdb previously scored it about 1.08870 in both histories.
[MySQL posting frequency accumulation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L2736-L2824)

The default parser now uses the same posting-count rule as ngrams.
All 72 focused queries match MySQL across bulk, incremental, and
mixed histories, including required and excluded terms. The bulk
and mixed results also reproduce from WAL and checkpoints written
before and after the fix.

The 22,320-query matrix gained 204 matches with no regressions.
The remaining 56 cases all use `utf8mb4_0900_as_cs` and involve
Boolean wildcard matching of width variants.

A separate 126-query compound search probe gained 45 matches after
the posting-count change. Repeated lookups and soft wildcard terms
accounted for most of the differences that remained in that probe.

A separate 270-query width probe found 15 divergences, all in
`utf8mb4_0900_as_cs` ngrams. With rows containing `Ａ花`, `ａ花`,
`A花`, `a花`, `ａ木`, and `Ａ木`, MySQL returns no rows for `ａ*`
after a bulk build and a subset from the write cache. The exact
`ａ花` query matches the two fullwidth flower rows in both histories.
MySQL uses a prefix comparator to traverse the cache and an InnoDB
internal `LIKE` cursor for flushed postings.
[Cache comparator](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/handler/ha_innodb.cc#L7460-L7475)
[Flushed posting cursor](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0opt.cc#L448-L499)

The flushed cursor is sensitive to where the first width variant falls
in index order. In a corpus with postings ordered as `a品`, `a字`, `ａ木`,
and `a竹`, MySQL returns the first two postings for `a*`. In single-width
corpora it returns every posting of the requested width. For mixed
widths the cached prefix can return a different contiguous range.

### Repeated and soft wildcard terms

Word-parser wildcard queries now share the posting visitor used for
ngram ranking. For `京ap* 京ap*`, MySQL counts both visits toward
document frequency and applies one contribution for the prefix.
A wildcard and an exact term with the same key share that state,
including a quoted exact term. MySQL applies a soft wildcard to rows
already selected in its group. For example, `~京ap* 京apple` keeps the
unmodified `京apple` rank, while `京apple ~京ap*` adjusts it.
[InnoDB Boolean visitor order](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0ast.cc#L478-L599)

All 234 queries in the focused compound probes match MySQL after this
change, up from 99. A separate 766-query stress probe gained 319 matches
with no regressions. The 92 differences left in that probe involved
the visitation or ranking of nested groups. The 22,320-query bulk spelling
matrix retains its previous results. All 28 recovery checks match MySQL
from WAL and checkpoints written before and after this change.

### Parenthesized word queries

The default parser completes a run of operator terms before it
merges a following parenthesized group. With the example corpus,
`+京ap* (noise)` returns rows 1–4 in MySQL. Moving the group before
the prefix, as in `(noise) +京ap*`, returns rows 1–3. Fsdb now
returns the same rows in both cases.
The MySQL grammar wraps preceding expressions in a list when it
appends a group, giving required and excluded terms an earlier
visit in the first query.
[InnoDB group grammar](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0pars.y#L88-L127)

The posting visitor carries the required-term state through those
preceding lists and starts with fresh required-term state inside a
parenthesized subexpression. An exclusion skips lookup when the
current set is empty. Required words reset or constrain the posting
bounds used by later required phrases.

All 766 queries in the original stress probe, all 1,568 in the deeper
nesting probe, and all 117 in the focused group probe now match
MySQL. The three probes gained 92, 290, and 90 matches respectively
with no regressions. Another 96 queries match with SQL candidate
filters. All 26 recovery queries match from WAL and checkpoints
written before and after the fix.
