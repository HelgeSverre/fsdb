# ICU ASCII and Basic Latin regex properties

A disposable MySQL 8.4.11 server matched `A` but not `é` with
`REGEXP_LIKE(value, '\\p{ASCII}', 'c')`. `\\P{ASCII}` matched `é` and a
supplementary emoji. MySQL also accepted `blk=Basic_Latin`,
`Block=Basic_Latin`, and `InBasicLatin` inside `\\p{...}`. Positive and
negative forms worked inside bracket expressions, including
`[A\\P{ASCII}]` matching `é`.

MySQL returned error 3685 for `\\p{Basic_Latin}` and
`\\p{IsBasicLatin}`. The latter is a valid .NET block spelling, so passing
it through to .NET would answer differently from MySQL. Fsdb maps only the
audited MySQL spellings to .NET's exact `IsBasicLatin` block and rejects
that .NET-only spelling during regex normalization. The focused
`REGEXP ASCII and Basic Latin property aliases match MySQL` regression
covers positive, negative, bracket, supplementary, and rejected forms.

Other ICU script and binary properties, and supplementary-scalar category
matching, remain outside this audit.

A later MySQL 8.4.11 probe found that `REGEXP_SUBSTR('😀', '\\P{ASCII}')`
and its bracket form both return the complete emoji. `\\W`, `\\H`, and
`\\V` behave the same.
Fsdb now makes these audited negative-property and shorthand matches consume
one supplementary scalar rather than one UTF-16 surrogate. Positive ICU
properties on supplementary scalars and script-property coverage remain open.
