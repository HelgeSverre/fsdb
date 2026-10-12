# Arabic and Devanagari regular-expression script properties

MySQL 8.4.11 accepts the long and short Arabic (`Arabic`/`Arab`) and
Devanagari (`Devanagari`/`Deva`) script names, `Is`, `sc=`, and `Script=`
forms, and their `scx=` and `Script_Extensions=` forms. Fsdb previously
returned error 3685 for these names.

The digest-pinned Docker MySQL image has 1,373 Arabic Script scalars and
1,421 Arabic Script_Extensions scalars. The extra 48 include U+060C ARABIC
COMMA and U+0640 ARABIC TATWEEL. Devanagari has 164 Script scalars and
221 Script_Extensions scalars; shared Vedic marks and punctuation account
for the extra 57. Both scripts include supplementary scalars, and neither
set changes under case-insensitive matching.

The [`pinned-image MySQL oracle`](../scripts/regexp-unicode-property-oracle.py)
and [`compiled-Fsdb scan`](../scripts/regexp-unicode-property-fsdb-scan.fsx)
enumerate every Unicode scalar and agree on the counts and SHA-256 digests
in both modes. `RegexpScriptProperties` uses the existing scalar-range
builder and alias map, keeping the normalizer unchanged. Focused Arabic
and Devanagari regressions both failed with error 3685 before the change
and pass afterward. `just check` passed all 3,486 tests with no build
warnings or errors. The four new differential steps pass in
`20261012T090000726-48003/contracts`, bringing the pinned-image run to
140 cases and 18,978 steps with the same ten documented differences.

Other ICU script values remain open.
