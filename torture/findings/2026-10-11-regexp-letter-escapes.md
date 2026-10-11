# REGEXP letter escapes

The pinned MySQL 8.4.11 oracle treats audited `\C`, `\F`, `\K`, `\L`,
`\q`, and `\y` as literal letters. For each pattern, `REGEXP_SUBSTR` of
its letter returns that letter. The regex backend previously rejected these
patterns as invalid .NET escapes. The shared normalizer now removes the
backslash for these six cases, including when they appear in a bracket class.

The same oracle rejects `\N` with error 3685 (HY000), rather than treating
it as a literal or returning .NET's generic parse diagnostic. Patterns were
passed as UTF-8 hex values so the SQL client did not consume their backslashes.
Focused function regressions and the `regexp-posix-classes` differential
contract cover these cases. Other ICU escape forms remain unverified.
