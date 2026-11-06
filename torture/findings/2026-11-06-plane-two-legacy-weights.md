# Plane-two legacy implicit weights

The [Extension J plane-three audit](2026-11-06-extension-j-plane-three-legacy-weights.md)
exposed a similar boundary after CJK Extension I, U+2EE5F. Native MySQL
8.4.11 gives U+2EE60 and later unassigned positions replacement primary
`FFFD` in `utf8mb4_general_ci` and `utf8mb4_unicode_ci`, and their own
implicit primaries in `utf8mb4_unicode_520_ci`. Fsdb's exact encoder fell
back for mixed strings crossing U+2EE5F/U+2EE60.

The supplementary-ideograph rule now covers U+20000–U+2FFFF as well as
plane three. The existing U+2F800–U+2FA1F CJK compatibility-ideograph
mapping remains earlier in the match order. A focused regression pins the
Extension I boundary, U+2F7FF/U+2F800, U+2FA1F/U+2FA20, and U+2FFFF. It
failed before the change and passes after it.

The native oracle reports zero differences in all 65,536 singleton and
65,535 internal adjacent-pair weights under each of the three legacy
collations: 393,213 exact-byte comparisons. This includes the compatibility
subrange and the unassigned tail. The separate ngram key path did not change.
Two 32-position boundary corpora, U+2EE50–U+2EE6F and U+2FA10–U+2FA2F,
match MySQL in 3,456 two-column searches at sizes 1–3 across the three
collations. The latter also matches 126 compatibility equality checks.
Broader unassigned-tail ngram behavior and MeCab remain open.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 20000 30000 --collation utf8mb4_unicode_520_ci --compare-fsdb
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 20000 2FFFF --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 2EE50:2EE70 --all-range-scalars --collation utf8mb4_unicode_520_ci 1 2 3
```

Repeat with `utf8mb4_general_ci` and `utf8mb4_unicode_ci`, and use
`2FA10:2FA30` for the second ngram boundary.
