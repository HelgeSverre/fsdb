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

The mixed bigram corpus still shows six `utf8mb3_unicode_ci` membership
differences involving Kana and Hangul equivalence. Accent-sensitive
wildcards and broader flushed-posting ranking also remain open.

MySQL's [InnoDB wildcard cache search](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L844-L875)
uses the [UCA prefix comparison](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1667-L1717).
