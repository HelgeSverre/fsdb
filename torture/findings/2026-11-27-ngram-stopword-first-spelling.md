# The first ngram spelling controls stopword filtering during writes

MySQL 8.4.11 groups collation-equivalent grams across all indexed columns of
a document before deciding whether the write cache should keep the posting.
The first spelling decides whether every occurrence contributes to relevance.
When MySQL bulk-builds an index, it tests each occurrence before grouping.
[MySQL document tokenization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0fts.cc#L4278-L4332)
[MySQL cache stopword check](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0fts.cc#L941-L956)
[MySQL bulk tokenization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/ddl/ddl0fts.cc#L628-L670)

```sql
CREATE DATABASE cjk_fts;
USE cjk_fts;
CREATE TABLE stops(value VARCHAR(30) COLLATE utf8mb4_bin);
INSERT INTO stops VALUES ('a ');
SET SESSION innodb_ft_user_stopword_table = 'cjk_fts/stops';
CREATE TABLE docs(
    id INT PRIMARY KEY,
    a TEXT,
    b TEXT,
    FULLTEXT (a,b) WITH PARSER ngram
) COLLATE utf8mb4_0900_ai_ci;
INSERT INTO docs VALUES (1,'京a','京Ａ'), (2,'京Ａ','京a'), (3,'雑音','');
SELECT id, MATCH(a,b) AGAINST('京a') AS relevance
FROM docs WHERE MATCH(a,b) AGAINST('京a') ORDER BY id;
```

With bigrams, only document 2 matches and both positions contribute.
Inserting the same rows before adding the index admits documents 1 and 2,
with one contribution each. Query-side filtering also distinguishes the
spellings. A Boolean lookup for `京a` is blocked by the stopwords and
`京Ａ` can reach the surviving equivalent posting. This fixture behaves
the same with the built-in stopword list.

Fsdb now records the filtering rule with each document, so an index can
contain old bulk postings alongside individually inserted or updated
documents. The new FSNY snapshot format retains both rules. Loading an FSNX
snapshot preserves its earlier occurrence-filtered postings, and new writes
adopt the corrected rule. A mixed fixture matched the pinned MySQL oracle in
nine searches and reproduced the same results after WAL replay and after
checkpoint reload.

The focused matrix improved from 114 to 174 matches out of 180, covering
inserts, updates, and bulk builds across five collations and both stopword
policies. Across 22,464 broader custom-stopword searches, differences fell
from 687 to 472, with no newly differing queries. The remaining broad
differences are Boolean prefixes in the sampled Japanese, Chinese, and
general collations. The focused matrix also retains six differences for
fullwidth terms in binary-collation bulk builds. A separate lookup
showed that MySQL's binary bulk index retains lowercase `京ａ` for
the original `京Ａ`. The custom-list matrix at startup sizes one and
three matched 87 of 90 searches at each size, with the same binary
bulk-build difference. All 5,384 Boolean and query-expansion control
searches across four collations continued to match.
