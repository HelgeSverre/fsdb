# Legacy enclosed Hangul primary weights

The native MySQL 8.4.11 weight oracle compared every code point in
U+3260–U+327F against the Release fsdb CLI under `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, and `utf8mb3_unicode_ci`. Before the change,
`utf8mb4_unicode_ci` differed on all 32 characters. The legacy primary
weight path now reuses the matching compatibility-Jamo primaries for the
first 14 enclosed letters, appends the parenthesized suffix for the next
14, and records the four exceptional sequences explicitly.

| Collation | Code points | Exact `WEIGHT_STRING` mismatches after |
| --- | ---: | ---: |
| `utf8mb4_unicode_ci` | 32 | 0 |
| `utf8mb4_unicode_520_ci` | 32 | 0 |
| `utf8mb3_unicode_ci` | 32 | 0 |

Representative regression cases cover a plain enclosed letter, its
parenthesized form, the multiweight U+327C/U+327D sequences, and the
version-dependent U+327E/U+327F values. This establishes exact primary
weight bytes for these characters, including bytes used by legacy text
keys. It does not establish full ngram membership or relevance parity for
queries containing them; that requires a separate full-text oracle run.

The broader U+3200–U+33FF `utf8mb4_unicode_ci` pre-change probe had
512/512 exact-weight mismatches. This change covers 32 of those code points;
the rest of that block remains open under the legacy Unicode collations.
