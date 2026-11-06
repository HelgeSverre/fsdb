# Supplemental Punctuation legacy weights

The disposable native MySQL 8.4.11 oracle found exact `WEIGHT_STRING`
differences for every scalar in U+2E00–U+2E7F under
`utf8mb4_general_ci`, `utf8mb4_unicode_ci`, and
`utf8mb4_unicode_520_ci`. The initial printable-character ngram probe still
matched all 564 searches at token size 1, so this was a weight-byte gap rather
than a demonstrated full-text membership difference.

The full 128-scalar probe exposed three rules. `general_ci` gives each scalar
its code-point weight. Unicode 4.0 gives the whole block its implicit weight.
Unicode 5.2 has assigned primaries for U+2E00–U+2E31 and implicit weights
for U+2E32–U+2E7F. Fsdb now encodes those rules directly, including the 50
assigned Unicode 5.2 primaries.

After the change, exact singleton and adjacent-pair weights match on all 128
positions in each of the three `utf8mb4` collations. Singleton weights also
match in `utf8mb3_general_ci` and `utf8mb3_unicode_ci`. The two-column ngram
oracle matches all 564 searches at each startup token size 1–10 under the
three `utf8mb4` collations, with no membership or relevance mismatch. A
focused Expecto regression pins representative assigned and implicit
boundaries in mixed strings.

Reproduce after a Release CLI build:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 2E00 2E80 --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_unicode_520_ci --legacy-range 2E00:2E80
```

The same commands with `utf8mb4_unicode_ci` and `utf8mb4_general_ci` cover
the other two full-text lanes. This audit does not establish parity for
other punctuation blocks, collations, or MeCab.
