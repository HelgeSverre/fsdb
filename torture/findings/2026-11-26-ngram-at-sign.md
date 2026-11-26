# Quoted ngram phrases and numeric terms

In MySQL Boolean ngram search, `"東京駅" @2東京` searches for either the quoted
phrase or `2東京`. The ngram lexer treats `@` as punctuation. Fsdb had reused
the word parser's proximity suffix and consumed the `2`. In a nine-document
fixture, that returned seven rows where MySQL returned four. Fsdb now leaves
the suffix for the ngram lexer. The word parser still supports proximity
distances. [MySQL ngram tokenizer](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/include/fts0tokenize.h)

Two related cases affected relevance:

- When a phrase reaches a gram absent from the index, MySQL stops reading
  its postings. An absent leading `8東` in `8東京` leaves the rank of
  `"東京駅"` unchanged. An absent trailing `京8` in `東京8` comes after a
  successful `東京` lookup and retains that lookup's effect on rank.
  [MySQL phrase search](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc)
- The 94 fullwidth forms from U+FF01 through U+FF5E share keys with their ASCII
  counterparts under `utf8mb4_0900_ai_ci` and `utf8mb4_0900_as_ci`. The
  form `０東` now shares its document frequency with `0東`. The audited
  case-sensitive generic and Chinese collations and `utf8mb4_bin` keep all
  94 pairs distinct.

The [SQL fixture](2026-11-26-ngram-at-sign.sql) matches all 19 result rows
against the [pinned MySQL 8.4.11 image](../config/toolchain.json), including
rounded relevance. The durable fixture also matches after crash WAL replay
and after an orderly checkpoint and reopen. A separate two-column bigram
sweep used 37 query shapes and 26 seeded CJK term selections per shape, with
zero differences after the fix versus 26 on the baseline. The maintained
native MySQL 8.4.11 corpus also matches all 1,548 searches across the two
case-insensitive UCA 9 collations at token sizes 1 and 2.

A digit-heavy two-column sweep compared matching rows and relevance with
both `@` and `#` as delimiters at startup token sizes 1–10. All 11,088
comparisons agreed on row membership. Both delimiters produced identical
scores within each engine, including after full-text-only optimization.
Fresh-index scores matched across engines within the comparison tolerance
(`2e-5` relative and `1e-7` absolute). After optimization MySQL sometimes
estimated fewer rows than the 20 actually present, and ranks diverged.
For example, with document frequency six and an estimated corpus of 15,
MySQL returned 0.158356 for the `22` contribution. Fsdb used the actual
corpus size and returned 0.273402. This follows the statistics policy
documented in [GAPS](../../GAPS.md#16-deliberate-divergences-accepted-not-targeted-for-parity).
