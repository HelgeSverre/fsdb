# `utf8mb4_general_ci` kana-script and width weights

The size-2 corpus still let `ひらがなゟ` find an extra document whose title
contains `かな`. ICU's primary comparison equated voiced `が` with `か`, so
their two-character ngrams shared a key. The same loss of voiced and small
kana distinctions affected many size-1 searches and query-expansion seeds.

Native MySQL 8.4.11 `WEIGHT_STRING` assigns literal code-point weights to
all 189 assigned characters in U+3041–U+30FF (with the U+3097–U+3098 gap)
and all 63 halfwidth-kana characters in U+FF61–U+FF9F. Direct equality keeps
hiragana, katakana, halfwidth, voiced, unvoiced, and small forms distinct,
including `が` versus `か` and `ゟ` versus `ヿ`. Fsdb now preserves these
written characters in escaped primary keys for `utf8mb4_general_ci`. Earlier
dedicated folds still handle the combining and halfwidth voicing marks.

A focused four-document MySQL oracle for natural, Boolean, and expansion
searches at ngram size 2 is covered by an Expecto regression. Build the
Release CLI and run the maintained differential lanes:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3040:3100 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range ff61:ffa0 --collation utf8mb4_general_ci 1 2
```

The broad two-column corpus, 387 searches at each ngram size, changes from
188 mismatch entries to 4. By token size, size 1 falls from 175 to 4,
size 2 falls from 13 to 0, and sizes 3–10 remain at 0. Exact comparison by
size, mode, query, and membership/relevance kind finds 184 resolved entries
and none newly introduced.

The expanded kana lane has zero mismatches across 1,134 searches at each
of sizes 1 and 2. The halfwidth lane has zero mismatches across 378 searches
at each size. The four residual reports are relevance-only differences for
`神` and `神` at size 1; the corpus does not prove parity for other
collation characters or search shapes.
