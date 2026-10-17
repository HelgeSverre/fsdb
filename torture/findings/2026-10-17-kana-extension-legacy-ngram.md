# Legacy kana extension and Bopomofo ngram keys

The maintained `ngram-multicol-oracle.py --legacy-range 3000:3200` lane puts
all printable U+3000–U+31FF forms in one `utf8mb4_unicode_ci` full-text index.
It compares natural, Boolean, and query-expansion membership and relevance,
including quoted phrases, with native MySQL 8.4.11. It also checks NFKC
equalities and explicit kana/Bopomofo pairs.

At size two, fsdb initially had 966 differences in 2,952 full-text queries
and 116 NFKC comparisons. MySQL expands `ゟ` to `より` and `ヿ` to `コト` for
legacy SQL equality. Sixteen small katakana forms U+31F0–U+31FF share full-text
keys with their ordinary kana; that missing fold accounted for most posting
and relevance differences across the combined range. ICU also conflated
`ㄮ` with `ㄜ` and `ㆻ` with the older equal pair `ㄍ`/`ㆣ`, while MySQL
keeps the newer forms distinct. MySQL's historical Boolean word table still
excludes the newer Bopomofo forms, independently of their natural-mode keys.

The legacy collation now expands the measured kana forms before ordinary kana
folding and protects the two distinct Bopomofo forms with collision-escaped
keys. After the change, the range reports zero differences at every startup
token size from 1 through 10: 2,952 full-text queries and 137 direct legacy
equality comparisons per size. The U+2100–U+21FF letterlike and
U+3200–U+33FF enclosed-CJK size-two lanes also remain at zero. The full
fsdb suite passes 3,715 tests.

This is an audited range, not full legacy UCA parity. A separate sweep of
U+FA00–U+FAFF and U+2F800–U+2F8FF still finds many compatibility-ideograph
differences; those forms require their own equivalence mapping.
