# Supplementary-scalar regular-expression properties

MySQL 8.4.11 accepts `\p{Regional_Indicator}`/`\p{RI}` for
U+1F1E6–U+1F1FF and `\p{Emoji_Modifier}`/`\p{EMod}` for
U+1F3FB–U+1F3FF. Each match consumes one Unicode scalar. The same rule
holds inside bracket expressions and for negated forms; `REGEXP_INSTR`
reports scalar positions after these matches.

Fsdb previously returned error 3685 for the ICU property names. The regex
normalizer now uses explicit UTF-16 pair atoms for these two ranges. A
focused regression also exposed a shared negation bug: after a property
rejected a matching pair, .NET could retry at the low surrogate and report a
false match. The scalar-negation helper now rules out that retry, including
for negated bracket unions. Positive, negated, bracket, and position cases
are covered by focused and MySQL differential tests. Other supplementary
properties and general ICU script matching remain open.

The focused regression failed with 3685 before the implementation and passes
afterward. `just check` passed 3,478 tests without build warnings or errors.
Both new comparison steps passed in the pinned MySQL 8.4.11 run at
`20261012T044121065-8440/contracts`. Across 139 cases and 18,880 steps,
the same eleven previously known difference signatures remained, with no
new mismatch.
