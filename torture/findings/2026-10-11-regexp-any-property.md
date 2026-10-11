# REGEXP Any property

The pinned MySQL 8.4.11 oracle treats `\p{Any}` as one Unicode scalar,
including a supplementary emoji or newline. `\P{Any}` matches no scalar.
The same behavior applies in audited positive, mixed, and complemented
bracket classes. `\p{ANY}+` can consume multiple whole scalars.
`\p{gc=Any}` is invalid (3685, HY000): `Any` is a binary property, not a
general category.

Fsdb previously delegated these forms to .NET, which has no `Any` property.
The shared normalizer now expands positive `Any` to its existing scalar atom
and negative `Any` to an impossible atom. In bracket classes, the existing
scalar-aware union path keeps positive and negative membership atomic, so
supplementary symbols are returned whole. Focused regressions and the
`regexp-posix-classes` differential contract cover the audited forms.
Other ICU binary and script properties remain open.
