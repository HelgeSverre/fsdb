# Variation-selector regular-expression property

MySQL 8.4.11 accepts `\p{Variation_Selector}` and `\p{VS}`. Fsdb previously
returned error 3685 for these property names. The case-sensitive and
case-insensitive sets are identical: 260 scalars in U+180B–U+180D, U+180F,
U+FE00–U+FE0F, and U+E0100–U+E01EF. The supplementary range must match as
one scalar inside and outside brackets. Negated forms must reject a selector
without retrying at its low surrogate.

The native MySQL oracle in
[`regexp-unicode-property-oracle.py`](../scripts/regexp-unicode-property-oracle.py)
and the compiled-Fsdb scan in
[`regexp-unicode-property-fsdb-scan.fsx`](../scripts/regexp-unicode-property-fsdb-scan.fsx)
enumerate every Unicode scalar and agree on the member count and SHA-256
digest in both match modes. The implementation adds one atom to the existing
scalar-aware binary-property map. A focused Expecto regression failed with
3685 before the change and passes afterward.
`just check` passed all 3,481 tests with no build warnings or errors.

The `variation-selector-properties` and `variation-selector-negation`
differential steps pass against the pinned MySQL 8.4.11 image in
`20261012T062509286-24490/contracts`. The run has 140 cases and 18,968
steps, with the same ten previously documented differences. Other ICU binary
properties remain outside this implementation.
