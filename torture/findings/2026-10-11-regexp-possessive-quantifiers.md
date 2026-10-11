# REGEXP possessive quantifiers

The pinned MySQL 8.4.11 server accepts ICU possessive `*+`, `++`, `?+`, and
interval `{m,n}+` quantifiers. Unlike greedy quantifiers, they do not give
consumed text back to satisfy the remainder of a pattern. For example,
`REGEXP_LIKE('aa','a++a','c')` returns 0 while `a+a` returns 1. The same
distinction was observed for `a*+a`, `a?+a`, `a{1,3}+a`, `[ab]++b`, and
`(ab)++ab`. Repeated possessive modifiers such as `a+++`, `a+?+`, and
`a{1,2}++` fail with error 3688 (HY000); the reported character is 4, 4,
and 8 respectively.

Fsdb's .NET regex backend rejected valid possessive forms as nested
quantifiers. The shared compiler now wraps the quantified atom in a
noncapturing atomic group, retaining its capture groups and preventing
backtracking. The scanner recognizes escaped atoms, bracket classes, groups,
Unicode properties, and supplementary characters in the audited cases.
Focused function regressions and the `regexp-posix-classes` differential
contract cover accepted matches and rejected repeated modifiers. Other ICU
grammar and diagnostic differences remain open.
