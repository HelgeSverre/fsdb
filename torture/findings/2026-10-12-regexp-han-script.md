# Han-script regular-expression properties

MySQL 8.4.11 accepts `\p{Han}`, `\p{Hani}`, `\p{IsHan}`, `sc=` and
`Script=` aliases, plus `scx=` and `Script_Extensions=` aliases. Fsdb
previously returned error 3685 for these names. Han Script includes CJK
radicals, ideographs, compatibility forms, and supplementary ideograph
extensions. Script_Extensions also includes shared punctuation, strokes,
enclosed symbols, and other marks; for example, U+3001 IDEOGRAPHIC COMMA
matches `scx=Hani` but not `Han`.

The digest-pinned Docker MySQL 8.4.11 image contains 99,030 Han Script
scalars in 22 ranges. Script_Extensions adds 308 scalars in 26 disjoint
ranges, for 99,338 total. Both sets are unchanged by case-insensitive
matching. The MySQL scan in
[`regexp-unicode-property-oracle.py`](../scripts/regexp-unicode-property-oracle.py)
requires the exact Docker image digest for these assertions. The compiled
Fsdb scan in
[`regexp-unicode-property-fsdb-scan.fsx`](../scripts/regexp-unicode-property-fsdb-scan.fsx)
checks every Unicode scalar against the same counts and SHA-256 digests.

A disposable Homebrew MySQL 8.4.11 build on this host reports a different
Unicode property set: 103,351 Han Script and 103,659 Script_Extensions
members. Version text alone therefore does not identify the oracle data for
these properties. Fsdb follows the pinned Docker image used by the
differential suite.

`Regexp` builds both properties from scalar ranges using a shared UTF-16
pair generator. The focused regression failed with error 3685 before the
change, and `just check` passed all 3,482 tests with no build warnings or
errors. The `han-script-properties` and `han-script-negation` steps pass
against the pinned MySQL image in `20261012T081436307-30827/contracts`.
That run has 140 cases and 18,970 steps, with the same ten documented
differences. Other ICU scripts and Script_Extensions values remain open.
