# Boolean ngram wildcards inside collation expansions

MySQL 8.4.11 matches a short wildcard against the leading collation
weights of a gram. Under `utf8mb4_0900_ai_ci`, `a*` finds `æ中`, `株*`
finds `㍿中`, and `ᄒ*` finds `한글`. One source character emits
multiple weights in each case. Fsdb previously built prefix keys only
at source character boundaries.

Fsdb now also records prefix keys inside the audited expansions for
the default accent-insensitive UCA 9 collation and the legacy 4.0 and
5.2 Unicode collations. It uses the same keys for live and retained
postings, cache lookup, and scoring. Exact grams and stopword filtering
keep their original keys and input. The legacy 4.0 collations keep `æ`
outside an `a*` search, and the legacy 4.0 and 5.2 collations keep
precomposed Hangul syllables outside a leading Jamo search.

The [SQL fixture](2026-11-27-ngram-collation-prefixes.sql) uses the
default bigram parser and stopwords. It covers cached and flushed
postings, Japanese symbols, width and ligature variants, a binary
control, and `㋿`, which postdates the audited Unicode versions.

A sweep of 408 short wildcard searches across the four collations now
matches MySQL membership in every case. Before the fix, 389 searches
in that sweep differed. The sweep covers enclosed numbers, Japanese
compatibility symbols, Latin ligatures, and sampled Hangul syllables.
Another 243 targeted searches matched membership and ranking across
token sizes 2–10, including expansions after a literal CJK prefix.
The existing 3,870-query CJK corpus matched membership and ranking
across token sizes 1–10. A durable database created before the fix
returned the corrected results on reopen, including a wildcard after
a phrase on flushed postings.

The [Kana and Jamo follow-up](2026-11-27-ngram-kana-and-jamo.md) covers
the outstanding Kana and Hangul equivalences in a dedicated mixed
corpus. Accent-sensitive wildcards and broader flushed-posting ranking
remain open beyond the [audited CJK expansion cases](2026-11-27-ngram-accent-sensitive-prefixes.md).

MySQL's [InnoDB wildcard cache search](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L844-L875)
uses the [UCA prefix comparison](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1667-L1717).

## Weighted prefix posting visits

For `utf8mb4_0900_as_cs`, index the two documents `ff駅` and `ﬃ駅` with
bigrams and stopwords disabled, then run a full-text-only optimize.
The query `>ff駅 <f*` matches document 1. A ligature key stops the
flushed cursor before it reaches that document's second prefix gram.
Only one posting cancels the boost. Fsdb now gets the adjustment
count from the cursor, which changes the score from about −0.728143
to MySQL's 0.271857. Replacing the ligature document with `駅前`
lets the cursor visit both postings and produces about −0.818762.
[MySQL posting rank adjustment](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L1232-L1264)

Against pinned MySQL 8.4.11, 58,000 searches matched membership and
scores within 0.00001. The corpus covered binary punctuation and
expanded accent-sensitive, case-sensitive and case-insensitive
bigrams. Each lane exercised fresh, flushed, mixed, and mixed
postings after deletion. The fix resolved all nine case-sensitive
relevance differences. The weighted score remained stable when
the WAL-backed index reopened from a checkpoint.

The separate Japanese wildcard case is covered in the
[kana-sensitive full-text cursor follow-up](2026-11-06-japanese-kana-order-and-weights.md#kana-sensitive-full-text-cursors).

## Circled Katakana in accent-insensitive ngrams

The `utf8mb4_0900_ai_ci` full-text key folds the 47 circled Katakana
scalars U+32D0–U+32FE into their ordinary forms after tokenization.
For example, `ア駅` finds a document containing `㋐駅`. Boolean
tokenization still classifies a circled character as punctuation
when it occurs in the query.

The two-column membership oracle matched all 188 natural-language
and Boolean checks on freshly inserted and flushed bigrams. The
expanded `utf8mb4_0900_ai_ci` corpus went from 2,236 differences to
zero across 26,356 searches. The maintained two-column corpus also
matched all 3,870 searches at token sizes 1–10. A checkpoint written
before the fix returned the corrected membership on reopen.
