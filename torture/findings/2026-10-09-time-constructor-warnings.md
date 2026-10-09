# TIME constructor overflow warnings

MySQL 8.4.11 clamps `SEC_TO_TIME(3020400)` and `MAKETIME(839,0,0)` to
`838:59:59`, but returns warning 1292 with the value that overflowed:
`'3020400'` for the first call and `'839:00:00'` for the second. Negative
inputs retain their sign. `MAKETIME` also retains the seconds argument's
declared fractional precision in that warning: decimal `2.5` gives `02.5`,
text `'2.500'` gives `02.500000`, and approximate `2e0` gives `02.000000`.

fsdb already clamped the results but emitted a generic warning. The scalar
functions now format the source value in their warning and share the TIME
warning emitter. The focused Expecto regression failed before the change and
passes after it. `just check` passed all 3,226 tests. The
`function-family-contracts` wire case compares both values and `SHOW WARNINGS`
against MySQL; both new steps pass. The full contract run still reports the
same nine identifier-case differences as the preceding run.
