# Enclosed CJK forms under the legacy Unicode collation

The prior ngram corpus covered punctuation, kana, and selected compatibility
ideographs. A new `--legacy-range 3200:3400` lane in
[`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py) puts all 511
printable U+3200–U+33FF forms in one `utf8mb4_unicode_ci` table. It compares
natural, Boolean, and query-expansion membership and relevance against native
MySQL 8.4.11 at every startup token size from 1 through 10. The same lane
compares each of 502 forms with its distinct Unicode NFKC expansion.

Before the change, the size-two ngram lane had 36 membership or relevance
differences among 1,533 queries. For example, fsdb gave `中㉆` an extra match
on `中㊥` and conflated `中㉊` with `中㉚`. MySQL's legacy weights distinguish
U+3246 (`FBC0B246`) from ordinary `文`, while U+32A5 uses the same weight as
`中`. MySQL also gives black-square number U+324A a distinct implicit weight
(`FBC0B24A`), whereas circled thirty U+325A expands to the weights of `30`.

The NFKC comparison exposed a broader split: 39 enclosed Korean and
ideograph forms compare *unequal* to their expansion in MySQL but equal in
fsdb; 135 enclosed katakana and square-unit forms compare *equal* in MySQL
but unequal in fsdb. The legacy collation now protects the measured distinct
forms with collision-escaped keys and expands the measured equal forms before
case/kana folding. Original text still drives ngram token boundaries. Focused
key, hash, ordering, and SQL ngram regressions pin representative cases.

The comparison also exposed a separate SQL path: `CAST(... AS CHAR CHARACTER
SET utf8mb4) COLLATE utf8mb4_unicode_ci` reported the requested collation
but compared under generic value semantics because the cast produced an
encoded-text value. Text comparisons now decode encoded-text operands and
apply the resolved collation. An oracle-backed regression covers literal and
row-derived casts while leaving binary values on their byte-comparison path.

The new range lane reports zero ngram and NFKC-equality differences at every
token size from 1 through 10: 1,533 ngram queries and 502 NFKC comparisons
per size. The earlier size-two legacy-punctuation lane (936 queries) and
default-collation lane (387 queries) also remain at zero. This is an audited
range, not complete legacy UCA weight-byte parity or MeCab support.
