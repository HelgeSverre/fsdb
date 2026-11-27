# Kana and Hangul Jamo in ngram postings

Under `utf8mb3_unicode_ci`, MySQL 8.4.11 treats ordinary, small, and
halfwidth Kana as equivalent posting keys where they have the same
primary collation weight. For example, `ガ中` finds `か中`, `ガ中`,
`ｶ中`, and `ゕ中`. A short wildcard also reaches the decomposed
voicing forms. The Hangul syllable in `하*` still has a different key
from the conjoining Jamo in `하`.
[MySQL ngram full-text parser](https://dev.mysql.com/doc/refman/8.4/en/fulltext-search-ngram.html)

The audited accent-insensitive Unicode 4.0, 5.2, and 9.0 collations
also let `ヿ*` match the expanded spelling `こと`. The converse
`こ*` can stop inside the ligature's primary weights. The same rule
applies to `ゟ` and `より`. The [SQL fixture](2026-11-27-ngram-kana-and-jamo.sql)
shows the expected rows.

All 1,220 bigram searches matched MySQL membership in memory and with
WAL enabled, up from 1,089. The matrix includes five collations,
natural-language and Boolean searches, and a binary control. A further
4,880 searches matched at startup token sizes 1, 3, 5, and 10.
The [maintained multicolumn corpus](../scripts/ngram-multicol-oracle.py)
also matched membership and relevance in 3,870 searches across token
sizes 1–10 against native MySQL 8.4.11.

An Fsdb database created before the fix reopened with the corrected
keys for postings flushed before the upgrade and excluded a row
deleted before the upgrade. The ordinary unique index still found
both stored labels by their original keys. Tokenization, stopword
rules, and ordinary SQL equality keep their existing behavior.
