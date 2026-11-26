# Ngram Boolean query character classification

## Finding

On the pinned MySQL 8.4.11 oracle, ngram indexing and natural-language search
accept some characters that the Boolean query lexer does not treat as word
characters. fsdb currently uses the host .NET Unicode categories for Boolean
query parsing, so it treats many later-assigned letters and numbers as query
terms. This changes result membership; the discrepancy is in query parsing,
not in the ngram postings or relevance formula.

With `FULLTEXT KEY ft(body) WITH PARSER ngram` and a row containing `中㉎文`,
`MATCH(body) AGAINST('中㉎' IN NATURAL LANGUAGE MODE)` finds the row on both
servers. The Boolean form finds it on fsdb but returns no row on MySQL. The
same occurs for `㉎文`, `"中㉎"`, and `+中㉎`. By contrast, the older circled
digit in `中①` is accepted by MySQL Boolean search. The difference persists
under `utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, `utf8mb4_0900_ai_ci`,
and `utf8mb4_bin`.

An expanded deterministic corpus at ngram token size 2 sampled 148 characters
from U+2000–206F, U+2E80–2EFF, U+2F00–2FDF, U+3200–32FF, U+3300–33FF,
U+FFA0–FFDC, and U+FE10–FE19. Its 444 searches across natural, Boolean, and
query-expansion modes produced exactly two discrepancies, both the Boolean
`中㉎` case. A wider sample of 120 BMP letters and numbers assigned after
Unicode 3.2 found 85 that MySQL Boolean search excluded while fsdb accepted
all 120. Natural and query-expansion searches still found their indexed rows.

## Cause and fix

MySQL's `strings/my_uctype.cc` states that its character-type data was
generated from `UnicodeData-5.0.0d9.txt`. Its multibyte classifier uses that
table for BMP code points. Comparing the table's letter/number/mark bits with
the public Unicode 5.0.0 `UnicodeData.txt` gives 48,108 versus 48,129 code
points. The public final file has exactly 21 additional CJK ideographs,
U+9FA6–U+9FBA; there are no code points present only in MySQL's table.

The Boolean lexer now uses compact ranges generated from the public Unicode
5.0.0 data, excluding U+9FA6–U+9FBA. Ngram indexing and natural/query-expansion
tokenization remain unchanged. The generator verifies the public file's hash
and the expected 48,108-character set. An Expecto regression covers natural
and Boolean modes, quoted terms, an older circled digit, a newer ideograph,
and a draft/final exception. The full `just check` gate passed with 3,707 tests.
The expanded 148-character differential corpus then matched MySQL on all 444
queries at each of ngram token sizes 1, 2, and 10 (1,332 searches total),
including Boolean membership and relevance.

This does not claim that every Boolean operator/phrase edge or collation is
covered by the sampled corpus. MeCab remains separate.

## Lowercase boundaries in Boolean ngrams

MySQL 8.4.11 applies the collation's lowercase mapping before classifying
Boolean ngram words. For example, Unicode 9 collations fold `Ɥ` to `ɥ`
and accept it inside `中Ɥ`. Unicode 5.2 collations accept `中ẞ` through
its fold to `中ß`. Binary ngram queries use the original classification.
Folding can also create a boundary. In Chinese 0900, uppercase Cherokee
`Ꭰ` folds to `ꭰ`, which lies outside the historical classifier.

Comparing `LOWER` for every BMP scalar on the pinned server gives nine
additional word characters for Unicode 5.2. Unicode 9 adds ten beyond
those nine and excludes 85 Cherokee capitals that fold to later letters.
Fsdb applies these boundary changes to the ngram lexer and keeps token
lookup on the existing posting path.

The wire fixture matched MySQL on all 452 bare and quoted Boolean
membership checks built from the affected uppercase and lowercase forms.
A separate Chinese fixture verifies that a folded Cherokee capital ends
the word. The maintained Chinese 0900 and Unicode 5.2 corpora also match
all 7,740 searches across token sizes 1–10. The subsequent posting change
uses the pinned [legacy lowercase tables](2026-10-07-fulltext-case-folding.md#legacy-lowercase-tables).

The Chinese Latin-extended lane matches all 384 searches at sizes 1 and 2.
Run it with a Release CLI and native MySQL 8.4.11.

```sh
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py \
  --collation utf8mb4_zh_0900_as_cs --legacy-range A780:A7A0 1 2
```

## Default UTF-8 word parser

The default InnoDB word parser also uses the historical character table.
It counts letters, numbers, marks, and underscores from that table.
ASCII apostrophes, supplementary scalars, and later BMP characters
such as `㉎` separate words. The tokenizer classifies the stored text
before folding each document token. Search folds the query first, so
`Ɥ` can separate words in a document and become the older letter `ɥ`
in a query under a Unicode 9 collation. [MySQL token classifier](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/handler/ha_innodb.cc)
· [MySQL query preparation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

Fsdb now makes the same distinction on UTF-8 word indexes and queries.
The Boolean lexer applies the modifier to each word in a punctuation-split
term, while a trailing wildcard applies to its last retained word.
`utf8mb3_general_ci` uses the same full-text combining-mark distinctions
as its four-byte counterpart. Unicode 9 lowercase folding now uses the
captured MySQL map, so later capitals such as `Ʂ` retain their boundaries.
The generator checked all 1,112,064 input scalars and reproduced both
earlier case tables byte for byte.

The pinned MySQL 8.4.11 wire checks matched all 900 membership queries
across nine UTF-8 collations and all 840 relevance values rounded to
five decimals. The smaller mixed-script matrix had 173 differences
before the change. Both an older checkpoint and an older WAL-only store
rebuilt postings that matched all 216 queries in that matrix.
The maintained ngram corpus also matched all 8,901 searches across
default and Chinese Unicode 9 collations at sizes 1–10 and Japanese
kana-sensitive Unicode 9 at sizes 1, 2, and 10.

## ASCII apostrophes in ngram queries

MySQL's InnoDB Boolean lexer splits on ASCII apostrophes before the ngram
parser turns words into grams. In a bare search, `+北京'飯店` requires
`北京` and leaves `飯店` optional. In a quoted search, `"東京'駅"`
contains a short trailing term, which yielded no matches in the tested
bigram index. Fsdb now uses the same boundary for bare terms and phrases.
[MySQL Boolean lexer](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/include/fts0tokenize.h)
· [MySQL ngram parser](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/plugin/fulltext/ngram_parser/plugin_ngram.cc).

The focused native MySQL 8.4.11 corpus matched all 960 queries in ordered
document IDs and relevance rounded to five decimals across eight UTF-8
collations and token sizes 1–10. The oracle refreshes table statistics
before scoring. The maintained
multi-column corpus matched all 1,935 queries at sizes 1, 2, 3, 5, and 10.

The 790-query binary punctuation corpus sets `SESSION
innodb_ft_enable_stopword=OFF` in both connections before creating the
index. A `GLOBAL` assignment changes the default for subsequent sessions.
The earlier corpus used different session settings, which skewed its
mismatch counts and led to the reported uppercase phrase differences.
With aligned settings, the three uppercase apostrophe queries agree,
and the binary phrase oracle now includes them.

Against MySQL's optimized full-text index the corrected corpus found 12
mismatches before the apostrophe fix and zero after it. Against a freshly
populated MySQL index the counts were 72 before and 70 after. All 70
remaining cases involved Boolean wildcards. For example, `東京 駅*`
matched only `東京` rows on the fresh index and included additional
`駅*` rows after full-text optimization.

## Short Boolean ngram wildcard ranking

MySQL uses the first matching gram's term frequency for each document.
It adds the document frequency of each matching gram, so one document
can count several times toward the prefix IDF. A repeated prefix can
increase this count again while contributing one term to a document's
score. [MySQL 8.4.11 InnoDB full-text query source](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

With a binary bigram index on `東京 東北 東京`, `東海`, `大阪`,
and `札幌`, the first document's `東*` score was 0.271857 in Fsdb
and 0.031219 on the optimized MySQL index. Fsdb now produces 0.031219.
Its prefix postings keep the corpus-wide frequency when another
predicate limits the candidate rows. Removing and replacing documents
updates both the row frequencies and the prefix document count.

The wire fixture compared 128 queries across eight UTF-8 collations on
native MySQL 8.4.11 with session stopwords off and full-text optimization
enabled. All returned IDs and scores matched within MySQL's float
rounding. The queries include single and repeated prefixes, a singleton
required group, compound terms, exclusions, and absent prefixes. A
separate fixture covers document frequencies larger than the population.
The maintained multi-column corpus also matched all 1,935 searches at
token sizes 1, 2, 3, 5, and 10. A durable Fsdb restart after a document
update preserved the single and repeated prefix scores.

For repeats across groups, Fsdb collects document frequencies from the
terms visited while evaluating the query. Each returned document uses the
distinct terms from its matched groups. In the binary fixture, `(東* 東*)`
therefore scores the same as `東* 東*`. A wildcard exclusion only visits
postings if its group already has matches, while an exact ngram follows
the phrase visitor even in an empty group. An exclusion in a subgroup can
change the relevance of rows that survive outside that subgroup.

Weights adjust the result by up to one point before term relevance is
added. A soft term only adjusts an existing match. Opposing operators can
adjust the same row differently if one prefix matches multiple grams.
For `>東* <東*`, the fixture's first two documents score −0.937984
and 0.031008. [MySQL 8.4.11 ngram parser](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/plugin/fulltext/ngram_parser/plugin_ngram.cc).

The optimized-index comparison checked membership and relevance on 2,253
queries across eight UTF-8 collations, including grouped exclusions,
optional and required groups, opposing weight operators, and phrases.
All 1,211 differences from the previous Fsdb revision were resolved in
that corpus at a relative tolerance of 0.00001 and an absolute tolerance
of 0.0000001 for MySQL's single-precision scores.

In the four-document example, MySQL's fresh-cache score was 0.015610 for
both matching documents. Cache-dependent wildcard differences remain.

## Adjacent Boolean modifiers

MySQL 8.4.11 recognizes ngram Boolean modifiers at the beginning of the
query or after an ASCII space. In `生日-快乐`, the hyphen separates two
optional terms. In `生日 -快乐`, it excludes documents containing `快乐`.
A tab or fullwidth punctuation before a modifier does not enable it,
and a space between the modifier and the term resets it. A run of
modifiers at an enabled boundary uses the final required/excluded flag,
the net rank adjustment, and the parity of the soft marker, in that
order. [MySQL ngram Boolean lexer](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/include/fts0tokenize.h).

A four-document binary bigram fixture compares `生日`, `快乐`,
`生日快乐`, and `東京` with session stopwords disabled. Optimizing the
MySQL full-text index and refreshing table statistics isolates the lexer
from cache-dependent wildcard scores. MySQL verified all 14 modifier
equivalences in the regression fixture. Before the change, the 19
distinct queries produced 11 membership and 12 relevance differences.

With the lexer change all 19 queries matched. The optimized binary
index also matched 2,750 punctuation queries at token sizes 1, 2, 3, 5,
and 10, plus 3,142 grouped, weighted, and wildcard queries at sizes 2
and 3. The maintained two-column `utf8mb4_0900_ai_ci` corpus matched all
3,870 natural-language, Boolean, and query-expansion searches at sizes
1–10.

With the same grouped corpus and refreshed statistics on a fresh
MySQL index, the comparison still found 314 membership and 345
relevance differences at sizes 2 and 3. This index-state-dependent
behavior remains open. [MySQL full-text query source](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

## Boolean rank cancellation

In a 20-document binary bigram index, `快乐` occurs once in one
document and twice in another. Its unweighted scores are 1 and 2.
The `<快乐` penalty cancels the first score exactly and leaves the
second at 1. MySQL 8.4.11 omits the first row from `WHERE MATCH`.
Fsdb now preserves that outcome on both its dictionary and map
paths, including quoted, grouped, and short-prefix forms. The
IDF floor still applies while scoring ubiquitous terms.
[InnoDB full-text ranking](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

A separate 1,934-query binary bigram corpus with optimization and
refreshed table statistics went from 31 membership and 34 relevance
differences to 3 and 6. The remaining cases involve nested groups
and wildcard ranking. For example, the optimized MySQL fixture
returns no rows for `+서울역 ((+快乐 +京*))`, while Fsdb returns the
two rows containing `서울역`. The maintained two-column corpus
still matches all 3,870 queries at token sizes 1–10.

A native-only control on the same 20 stored rows compared 440
Boolean queries before and after full-text optimization. MySQL's
cache held 46 distinct tokens before the operation and zero after it.
Membership changed for 54 queries in each of three insertion orders.
For example, the fresh index returned eight IDs for `東京 東*`, and
the optimized index returned eleven. The membership changes were
repeatable across the insertion orders, and the number of score
differences ranged from 60 to 177. Cache-dependent results remain open.

## Required prefix bounds across Boolean groups

In `+서울역 (+快乐 +京*)`, the required prefix intersects the group's
existing `快乐` matches and leaves their document-ID bounds in the
query state. A later required phrase skips any posting entirely outside
those bounds. An overlapping posting remains available in full. Putting
`+京*` first in the group resets the bounds. Making the outer phrase
optional lets it run before the group can set them.
[InnoDB full-text query source](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

After full-text optimization on MySQL 8.4.11, the nested-group matrix
matches in both membership and relevance. Each of the binary and
accent-insensitive optimized bigram corpora went from nine differences
to zero across 1,934 queries. The binary trigram corpus still has one
query with a membership and relevance difference around a zero score.

Changing an indexed document can move its position in the full-text
index while keeping its primary key. Fsdb preserves this position
through metadata-only ALTER, checkpoints, and WAL replay. Snapshots from
older versions continue to use the previous lookup behavior until the
index is rebuilt or emptied because they lack the original document
order. Fsdb uses one posting per gram. InnoDB can split a gram across
multiple on-disk nodes and keep separate in-memory postings, so the
document bounds verified here do not establish parity across those
storage states.

## Single-precision rank accumulation

In an optimized ten-document binary trigram index, five rows have
`東京駅` and one of those rows also has `京都駅`.
For `+(京* 東京*) <東京*`, MySQL 8.4.11 gives the row with both
terms a rank of zero, and the other Tokyo rows rank −1. Its accumulator
starts at −1, rounds the tiny Tokyo contribution away in single
precision, then adds Kyoto's +1. With `京* <東京* +(東京*)`, Kyoto
contributes first, and the same row retains Tokyo's
`1.885928302414186e-9` relevance.
[InnoDB rank calculation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3011)
[InnoDB rank type](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/include/fts0fts.h#L148).

Fsdb's grouped, repeated, and weighted ngram evaluator now accumulates
in the order terms first contribute to matching documents,
rounding after each addition. The regression covers both term orders,
candidate-restricted evaluation, and the dictionary path.
The 1,934-query optimized binary trigram corpus now matches in both
membership and relevance. The same corpus matches at binary size four,
with both collations at size two, and with the accent-insensitive
collation at size three. A separate 3,856-query binary trigram holdout
also matches. The maintained two-column corpus matches all 3,870
searches at sizes 1–10.

At size two on a fresh MySQL index, the 3,856-query holdout had the
same mismatch counts before and after this fix: 606 for membership and
944 for relevance. The fresh-cache behavior remains open.

## Cached and flushed prefix postings

A binary bigram index on `東京`, `東海`, and `大阪` isolates
the membership change. On a fresh index, `東*` returns IDs 1 and 2,
but `東京 東*` returns only ID 1. Putting the prefix first or making
it required returns both IDs. The same behavior holds through a
parenthesized group and after a phrase with zero matches.

InnoDB retains its phrase-search flag after evaluating a phrase. Optional,
weighted, and excluded short prefixes then skip the wildcard cache
lookup. Required prefixes take a different path that still searches
the cache. All of these paths continue to visit disk postings.
[InnoDB Boolean query execution](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc).

| MySQL 8.4.11 operation | Cached postings | Disk postings | IDs for `東京 東*` |
| --- | ---: | ---: | --- |
| Insert the three rows into an empty indexed table | 3 | 0 | 1 |
| Analyze, flush the table, or run default optimize in this fixture | 3 | 0 | 1 |
| Optimize with `innodb_optimize_fulltext_only=ON` | 0 | 3 | 1, 2 |
| Insert `東北` and `東京 東海 東海` after the flush | 3 | 3 | 1, 2, 5 |
| Run full-text-only optimize again | 0 | 6 | 1, 2, 4, 5 |

Adding a full-text index to an already-populated table starts
with disk postings. A metadata-only ALTER preserved a mixed layout.
Truncation followed by insertion returned the index to cached postings.
A clean MySQL restart flushed the postings of the three-row fixture.
After abrupt restart, the metadata tables initially showed no postings.
The first search reconstructed them in cache and kept the fresh-index
membership.

Storage also affects term frequency independently of membership. With
`東京 東北 東京`, `東海`, `大阪`, and `札幌`, the first row's
`東*` rank is about 0.0156097 in cache in that insertion order and
0.0312194 on disk. Twelve of the 24 insertion orders give each of these
scores while the postings are cached. InnoDB
keeps the first matching gram's frequency. The cache starts at the
first prefix match on its tree search path, then visits predecessors
and successors. It searches this cache before visiting disk postings.
[InnoDB wildcard cache lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L844).

The cache also retains history from deleted rows. Inserting an
extra `大阪` row between the first two live documents, deleting it,
and finally inserting the live `大阪` row leaves six cache postings
instead of five. The live rows and their relative insertion order
are identical, but the first row's `東*` score changes from
0.0156097 to 0.0312194. The cache tree therefore depends on terms
from deleted documents as well as live rows.

The differential oracle refreshes MySQL's estimated document count
after a full-text flush to isolate the effect of posting lookup on
relevance. It verifies the cache and disk counts, then compares
Boolean membership and relevance at each stage.

Fsdb now separates flushed and newly written ngram postings by
their document order. Full-text-only optimize publishes a flush
position, and Fsdb preserves it through metadata-only ALTER,
checkpoints, and WAL replay. The lifecycle fixture now matches all
221 membership checks. Counting visits to the right posting source
reduced differences on the 3,856-query fresh binary bigram holdout
from 606 to zero for membership and from 944 to 168 for relevance.

Fsdb now retains the cache terms in their first insertion order and
reproduces the cache tree's prefix traversal. Terms from committed
deletions keep their positions until a flush. A term from a row
inserted and deleted within one transaction stays out of the
committed cache, including during WAL replay. Checkpoints preserve
the insertion order, including retained terms whose rows are gone.

The 221 lifecycle checks now match in membership and relevance.
Another 256 prefix checks match across 24 exhaustive insertion
orders of the four-row fixture and 40 seeded orders of the larger
corpus. Two relevance differences remained in the 3,856-query fresh
holdout in each bigram collation. Reduced examples are
`生日 (+東京駅 +京*) (+生日)` and `学校 (+京都 +生* +学校)`.
Both revisit a phrase from a required group after a short prefix
has restricted the posting range. With `京都`, `生日`, `学校`,
and `大阪`, MySQL scores `学校 (+京都 +生* +学校)` at 0.362476
with cached postings and 0.090619 with flushed postings. The
cache skips the disjoint posting before counting it. Disk includes
its frequency before checking its bounds. Fsdb now applies each rule
to its own posting source. An overlapping source stays available
in full, and a multi-gram phrase stops at the first gram for which
neither source has an available posting.

The two bigram relevance differences are resolved. The same holdout
also matches on fresh and flushed binary trigrams and on flushed
binary bigrams. The 28 focused mixed-index checks cover disjoint
postings, overlap in either source, and a multi-gram phrase. The
full bigram holdout also matches with ten flushed documents in
both collations and with two or eighteen flushed documents in the
binary collation. The maintained two-column corpus matches all
3,870 searches at sizes 1–10.

Deleted postings still contribute to MySQL's Boolean frequency totals.
With four live documents and a deleted row containing `東京 東北`,
MySQL returns about 0.090619 for `東京`. The bare prefix `東*`
subtracts one deleted document from the four matching gram postings
and scores about 0.015610. Required `+東*` counts all four postings
and scores about 1.89e-9. Deleted rows never appear in either result.
After full-text-only optimize, `東京` scores about 0.362476 and
`+東*` returns about 0.090619.
[InnoDB rank optimization and deleted-document adjustment](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3119).

Fsdb preserves those frequencies across WAL replay, checkpoints, and
clean shutdown. Clean shutdown moves retained postings to disk, where
they can influence compound searches that skip cached prefixes.
Full-text-only optimize purges them. An `FSNT` snapshot loaded with its
original ranks, then a new `FSNU` checkpoint preserved deleted postings
written after the upgrade.

A required prefix can set bounds for a later required gram even in a
flat query. After flushing four documents and deleting a newly cached
`東京 東北` row, `+京都 +東* +東京` skips the disjoint cached `東京`
posting and scores about 0.462487. With `東京` optional or inside
`+(東京)`, it still counts the posting and scores about 0.387477.
The lifecycle fixture matches 594 queries across all three search modes.
A 3,856-query Boolean deletion holdout matched with both cached and
mixed postings for binary bigrams, binary
trigrams, and `utf8mb4_0900_ai_ci` bigrams. Another 45 comparisons
matched across multi-update commits, deletes after updates, full rollback,
and rollback to a savepoint.

In the same four-live-row corpus, natural-language `東京` uses the
live frequency and scores about 0.362476. Natural-language `東京 東北`
and query expansion on `東京` use the retained frequency and score
about 0.090619. Repeated `東京 東京` counts both posting visits and
scores about 1.89e-9 in both modes. A deleted-only term supplies no
result or expansion seed. These cases also matched after a clean
restart and after full-text-only optimize. A 3,856-query holdout run
in both modes matched with mixed binary bigrams, mixed binary
trigrams, and cached and mixed `utf8mb4_0900_ai_ci` bigrams.
[InnoDB's single-term optimization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3327).

Automatic cache flushing remains open. A MySQL 8.4.11 probe starts
with `東京`, `東海`, and `大阪`. It adds rows containing
`REPEAT('日', 2048)` in batches. At the 1,600,000-byte cache setting,
the two `東*` postings had moved to disk by the 64-row observation.
At the default 8,000,000-byte setting, they were still cached at
256 rows and had moved by 384. `東京 東*` began returning both
matching rows after the move. Fsdb still returned one row after
512 inserts. The observation points bound the flush and can vary
with the background worker's scheduling.
[InnoDB full-text cache](https://dev.mysql.com/doc/refman/8.4/en/innodb-fulltext-index.html#innodb-fulltext-index-cache).

Fsdb's orderly CLI shutdown now closes accepted clients, waits for
their work to finish, and flushes the ngram cache in the final
checkpoint. SQL `SHUTDOWN`, SIGTERM, and SIGINT all reopened the
three-row fixture with IDs 1 and 2 matching `東京 東*`. After SIGKILL
and WAL recovery only ID 1 matched, as on the fresh index. No
uncommitted peer rows survived either form of restart.

Closing an owned durable database through the embedding API uses
the same final checkpoint. Ordinary checkpoints retain the cache
and its insertion order. A prepared XA branch stays pending across
the close, and its postings enter the cache when it commits after
reopen.

```sh
dotnet build src/Fsdb.Cli -c Release
uv run --no-project --with pymysql python torture/scripts/ngram-multicol-oracle.py --cache-lifecycle
```
