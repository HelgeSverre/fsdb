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
