# Vertical punctuation legacy weights

A disposable native MySQL 8.4.11 oracle exposed exact `WEIGHT_STRING`
differences for U+FE10–U+FE2F. The existing full-text key mapping preserved
the relevant distinctions, but the byte encoder fell back to ICU for these
characters. A mixed string crossing U+FE1F/U+FE20 also triggered that
fallback for the whole string.

The legacy collations have different primary-weight rules:

- `utf8mb4_general_ci` assigns each character its code-point weight.
- `utf8mb4_unicode_ci` uses implicit weights for U+FE10–U+FE1F and
  U+FE24–U+FE2F; U+FE20–U+FE23 have no primary weight.
- `utf8mb4_unicode_520_ci` assigns primaries to U+FE10–U+FE19, with
  U+FE19 expanding to three copies; U+FE1A–U+FE1F and U+FE27–U+FE2F
  have implicit weights; U+FE20–U+FE26 have no primary weight.

After encoding these rules, all 32 singleton and adjacent-pair probes match
MySQL exactly in each of the three `utf8mb4` collations and in
`utf8mb3_general_ci` and `utf8mb3_unicode_ci`. The two-column ngram oracle
also matches all 156 searches and 10 equality checks at each startup token
size 1–10 in the three `utf8mb4` collations, with no membership or relevance
difference. Focused regressions cover mixed strings across assigned, implicit,
and zero-primary boundaries.

Reproduce after a Release CLI build:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py FE10 FE30 --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_unicode_520_ci --legacy-range FE10:FE30
```

Repeat with `utf8mb4_unicode_ci` and `utf8mb4_general_ci` for the other
full-text lanes. Other punctuation blocks and MeCab remain outside this audit.
