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

This does not claim that every Boolean operator/phrase edge or collation is
covered by the sampled corpus. MeCab remains separate.
