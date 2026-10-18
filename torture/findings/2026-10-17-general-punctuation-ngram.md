# Curly quote weights in legacy ngram searches

The native MySQL 8.4.11 oracle is
[`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py), run with
`--legacy-range 2000:2070`. It indexes one U+2000–U+206F character per row
between CJK characters and compares two-column natural-language, Boolean,
and query-expansion searches, including relevance.

At token size 2, fsdb initially differed on 64 of 426 queries. MySQL kept
U+2018–U+201B and U+201C–U+201F as eight distinct primary-weight quote
forms; fsdb's ICU-backed key treated each four-character group as equivalent.
The mismatches included both extra matches and different scores. MySQL's
`utf8mb4_unicode_ci` `WEIGHT_STRING` values for the curly single quotes are
`0278`–`027B`, and for the curly double quotes `027F`–`0282`. The ordinary
apostrophe and double quote have weights `0277` and `027E`. The Unicode 5.2
and 0900 accent-insensitive collations also distinguish the eight forms.

The Unicode collation adapter now gives the curly forms separate comparison
and ngram keys while escaping the private marker used by those keys. Focused
collation and indexed-ngram tests cover each form. The maintained differential
runner reports zero membership or relevance mismatches across 426 queries
at each token size from 1 through 10 under `utf8mb4_unicode_ci`; the
size-two checks also report zero mismatches under `utf8mb4_unicode_520_ci`
and `utf8mb4_0900_as_cs`.

This establishes the audited search behavior, not exact MySQL weight bytes
or a complete ordering of these forms against every other punctuation mark.
