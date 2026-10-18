# `utf8mb4_general_ci` Bopomofo and Kanbun weights

The two-column ngram differential corpus over U+3190–U+31BF found false
matches under `utf8mb4_general_ci`. At token size 1, 76 membership or
relevance comparisons differed among 288 searches. For example, a search for
`ㆣ` also found a document containing `ㆻ`, and `ㆤ` found `ㆥ`.

Native MySQL 8.4.11 gives each of the 48 code points its literal, distinct
primary weight in codepoint order. Fsdb's ICU-based key had only 44 distinct
keys. The shared collation-key path now preserves the written weight with an
escaped marker, so equality, indexes, and ngram postings agree on the same
distinction. Focused tests cover all 48 keys, their order, representative
collisions, marker escaping, and full-text searches.

The adjacent U+3100–U+318F Bopomofo block exposed the same problem. MySQL
assigns each of its 144 code points a distinct, codepoint-ordered primary
weight, while Fsdb initially produced only 143 keys. In particular, `ㄜ`
and `ㄮ` shared a key and each search found both documents. A separate
escaped mapping now preserves all 144 keys without changing keys from the
earlier fix. Focused equality, ordering, marker, and posting tests cover it.

The native differential command is:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3190:31C0 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3100:3190 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 31C0:3200 --collation utf8mb4_general_ci
```

The audited two-column corpus agrees on matching rows and relevance for all
288 extension-block searches and all 822 base-block searches at each token
size from 1 through 10. The adjacent U+31C0–U+31FF control also agrees for
all 330 searches at each size from 1 through 10, without needing another
weight override. Other Unicode blocks and full-text query shapes remain
outside this finding.
