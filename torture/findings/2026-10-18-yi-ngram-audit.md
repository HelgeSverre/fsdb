# Yi syllable and radical ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared fsdb with native MySQL 8.4.11 for printable characters in
U+A000–U+A4FF. The range includes Yi syllables and Yi radicals. Each query
checks matching row IDs and relevance scores in natural-language, Boolean,
and query-expansion modes, with the character embedded between CJK text.

`utf8mb4_general_ci` matched in 15,216 searches at token sizes 1 and 2.
An additional 1,464 size-1 searches over U+A400–U+A4FF matched under
`utf8mb4_unicode_ci`. No mismatch was found.

The general-ci corpus was split into three ranges to bound each disposable
server's document count:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range A000:A100 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range A100:A300 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range A300:A500 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range A400:A500 --collation utf8mb4_unicode_ci 1
```

This narrows the sampled CJK-adjacent collation and ngram gap. It does not
establish behavior for other token sizes, MeCab, or exact primary-weight bytes.
