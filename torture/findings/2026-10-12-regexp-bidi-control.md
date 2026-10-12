# Bidi-control regular-expression property

MySQL 8.4.11 accepts `\p{Bidi_Control}` and `\p{Bidi_C}`. The pinned
oracle matches U+061C, U+200E–U+200F, U+202A–U+202E, and U+2066–U+2069;
it rejects nearby U+200C–U+200D, U+2060, and U+206A. Negated forms match
those nonmembers and consume a supplementary emoji as one Unicode scalar.

Fsdb previously returned regex error 3685 for these ICU property names. Its
bounded normalizer now maps the audited aliases to those ranges, including
inside brackets. The focused regression failed before the change and passes
after it. The implementation also consolidates the small binary-property
class mappings into one table shared with the existing ASCII hex, Unicode
hex, and join-control aliases. Other ICU binary and script properties remain
open.

`just check` passed 3,477 tests without build warnings or errors. Both new
steps passed in `20261012T043252786-7584/contracts` against MySQL 8.4.11;
the 139-case, 18,878-step run retained the same eleven previously known
difference signatures and added none.
