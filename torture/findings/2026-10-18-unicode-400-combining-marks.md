# Unicode 4.0 combining marks in CJK ngram searches

MySQL 8.4.11's `utf8mb4_unicode_ci` uses UCA 4.0 weights. It assigns distinct
implicit primary weights to every position in U+1AB0–U+1AFF and
U+1DC0–U+1DFF, including code points assigned after Unicode 4.0. In
U+20D0–U+20FF, the first 27 marks through U+20EA are primary-ignorable, while
U+20EB–U+20FF have distinct implicit weights. ICU ignores or merges many of
these marks, causing full-text postings and relevance scores to diverge.

Before the fix, the native multi-column ngram oracle found 291 and 664
differences for U+1AB0–U+1AFF at token sizes 1 and 2, and 326 and 396 for
U+20D0–U+20FF. The shared legacy Unicode fold now preserves the UCA 4.0
implicit weights with escaped keys while retaining UCA 5.2's different
ignorable and explicit groups. U+20D0–U+20EA remain ignorable in the older
collation; U+20EB–U+20F0 remain ignorable in `utf8mb4_unicode_520_ci`.

Build `Fsdb.Cli` in Release, then run the disposable MySQL 8.4.11 oracle:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py 20D0 2100 --collation utf8mb4_unicode_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1AB0:1B00 --collation utf8mb4_unicode_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1DC0:1E00 --collation utf8mb4_unicode_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 20D0:2100 --collation utf8mb4_unicode_ci
```

At every token size from 1 through 10, the expanded corpora have zero
differences across 372, 777, and 396 searches per size, respectively,
including natural, Boolean, and query-expansion modes. The U+20D0 corpus
also has zero differences under `utf8mb4_unicode_520_ci` at sizes 1 and 2.
