# `utf8mb4_general_ci` decomposed kana ngrams

The remaining token-size-4 Boolean `ガラス` mismatch was a collation-key
collision. A document containing decomposed `カ\u3099ラス` has a four-rune
ngram posting. Fsdb's ICU primary comparison treated the three-rune query
`ガラス` as the same key, so Boolean lookup returned that document. Native
MySQL 8.4.11 returns no row.

Direct MySQL equality and `WEIGHT_STRING` checks show that
`ガ` differs from `カ\u3099`, and `パ` differs from `ハ\u309a`, under
`utf8mb4_general_ci`. Fsdb now preserves U+3099 and U+309A as separate,
positional collation keys while retaining literal private-use marker safety.
An eight-document MySQL oracle at ngram sizes 3 and 4 established natural,
Boolean, and query-expansion membership for precomposed and decomposed kana;
the Expecto regression covers those results.

After a Release build, compare the maintained two-column corpus against
native MySQL 8.4.11:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 260 | 248 |
| 2 | 68 | 68 |
| 3 | 17 | 15 |
| 4 | 4 | 0 |
| 5–10 | 0 | 0 |

Comparison by token size, search mode, query, and membership/relevance kind
shows 18 previous mismatch entries resolved and none introduced. The corpus
still has 331 mismatch entries at sizes 1–3. The remaining
families need individual MySQL reductions; this audit does not establish
general-collation parity beyond the sampled data.
