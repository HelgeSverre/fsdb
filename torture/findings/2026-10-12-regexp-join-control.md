# Join-control regular-expression property

MySQL 8.4.11 accepts `\p{Join_Control}` and `\p{Join_C}` for U+200C
(ZERO WIDTH NON-JOINER) and U+200D (ZERO WIDTH JOINER). They do not match
the adjacent U+200B (ZERO WIDTH SPACE). Negated forms match U+200B and
consume a supplementary emoji as one Unicode scalar, including inside a
bracket expression.

Fsdb previously passed these ICU property names to .NET, which returned
error 3685. The regex normalizer now maps both aliases to the two audited
code points. A focused regression failed before the change and passes after
it; the `regexp-posix-classes` contract compares positive, negated, and
bracket forms against the pinned MySQL server. Other ICU binary and script
properties remain open.

`just check` passed 3,476 tests without build warnings or errors. In the
`20261012T042603958-6949/contracts` run, both new steps passed against
MySQL 8.4.11. Across 139 cases and 18,876 steps, the same eleven previously
known difference signatures remained; none were added.
