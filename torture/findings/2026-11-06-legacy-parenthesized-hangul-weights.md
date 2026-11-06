# Legacy parenthesized Hangul weights

The native MySQL 8.4.11 `WEIGHT_STRING` oracle found 32 exact-byte mismatches
in U+3200–U+321F for each of `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, and `utf8mb3_unicode_ci`. These are parenthesized
Hangul Jamo plus the unassigned U+321F. The existing comparison and ngram
corpus already matched; this audit concerns exact weight bytes.

The legacy weight path now reuses compatibility-Jamo primaries for the first
28 characters, surrounds them with the version-specific opening and closing
weights, and records the three exceptional multiweight sequences. U+321F
uses MySQL's implicit legacy weight. The same base-Jamo list serves the
existing U+3260–U+327F enclosed Hangul path.

After rebuilding the Release CLI, the oracle compared all 32 code points in
each of the three collations: **0/96 exact-weight mismatches**. The focused
`legacy WEIGHT_STRING follows MySQL primary weights` test also passed with
representative ordinary, suffixed, exceptional, and unassigned characters.

The remaining exact-weight work in U+3200–U+33FF starts at U+3220 and
continues around the separately covered U+3260–U+327F range.
