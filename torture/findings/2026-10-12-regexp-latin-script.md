# Latin-script regular-expression properties

MySQL 8.4.11 accepts `\p{Latin}`, `\p{Latn}`, `\p{IsLatin}`, `sc=` and
`Script=` aliases, plus `scx=` and `Script_Extensions=` aliases. Fsdb
previously returned error 3685 for these names. Latin Script includes
ASCII and accented Latin letters, IPA and modifier forms, full-width
letters, and supplementary Latin letters. Script_Extensions additionally
includes shared combining marks and punctuation; for example, U+0300
COMBINING GRAVE ACCENT matches `scx=Latn` but not `Latin`.

The digest-pinned Docker MySQL 8.4.11 image has 1,487 Latin Script scalars
in 39 ranges. Script_Extensions adds 68 scalars in 26 disjoint ranges, for
1,555 total. Both sets are unchanged by case-insensitive matching. The
[`pinned-image MySQL oracle`](../scripts/regexp-unicode-property-oracle.py)
and [`compiled-Fsdb scan`](../scripts/regexp-unicode-property-fsdb-scan.fsx)
enumerate every Unicode scalar and agree on the counts and SHA-256 digests
in both modes.

`Regexp` uses the shared scalar-range builder and script-alias map, so
supplementary matches consume complete scalars inside and outside brackets.
The focused regression failed with error 3685 before the change and passes
afterward. `just check` passed all 3,483 tests without build warnings or
errors. The `latin-script-properties` and `latin-script-negation` steps pass
against the pinned image in `20261012T083144127-39457/contracts`. The run
has 140 cases and 18,972 steps, with the same ten documented differences.
Other ICU script values remain open.
