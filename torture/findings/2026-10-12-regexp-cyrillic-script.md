# Cyrillic-script regular-expression properties

MySQL 8.4.11 accepts `\p{Cyrillic}`, `\p{Cyrl}`, `\p{IsCyrillic}`,
`sc=` and `Script=` aliases, plus `scx=` and `Script_Extensions=` aliases.
Fsdb previously returned error 3685 for these names. The pinned Docker
image has 508 Cyrillic Script scalars in ten ranges, including the
supplementary Cyrillic Extended-D letters. Script_Extensions adds 13
shared marks in ten disjoint ranges, for 521 total. Both sets are
unchanged by case-insensitive matching.

The [`pinned-image MySQL oracle`](../scripts/regexp-unicode-property-oracle.py)
and [`compiled-Fsdb scan`](../scripts/regexp-unicode-property-fsdb-scan.fsx)
enumerate every Unicode scalar and agree on the counts and SHA-256 digests
in both match modes. `RegexpScriptProperties` keeps these ranges beside the
other audited scripts; `Regexp` uses their compiled scalar-aware atom for
positive, negated, and bracket forms. The focused regression failed with
error 3685 before the implementation and passes afterward. `just check`
passed all 3,484 tests without build warnings or errors. The
`cyrillic-script-properties` and `cyrillic-script-negation` steps pass in
`20261012T084710638-43040/contracts`, bringing the pinned-image run to
140 cases and 18,974 steps with the same ten documented differences.

Other ICU script values remain open.
