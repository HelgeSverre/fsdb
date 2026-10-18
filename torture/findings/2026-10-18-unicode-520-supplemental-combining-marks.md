# Unicode 5.2 supplemental combining marks in CJK ngram searches

The U+1DC0–U+1DFF block mixes several MySQL 8.4.11 weight classes under
`utf8mb4_unicode_520_ci`. U+1DC0–U+1DC9, U+1DCB–U+1DD1, and U+1DFD–U+1DFF
are primary-ignorable. Some older marks have explicit weights and genuine
equivalences; for example, U+1DD8 and U+1DD9 share one weight. U+1DE7–U+1DFC
have distinct implicit weights that ICU ignores or merges. MySQL gives U+1DCA
the same primary weight as U+036C, while U+1DE3 has a different weight that
ICU merges with it.

Before the fix, the native multi-column ngram oracle found 268 mismatches at
size 1 and 544 at size 2, each across 384 searches. The collation fold now
gives only the implicit-weight subrange separate escaped keys, maps U+1DCA
to its U+036C equivalent, and separates U+1DE3. It leaves MySQL's
primary-ignorable and other existing equivalence groups alone.

The expanded oracle adds U+036C as a document and query control, and places
each mark beside and on either side of a base character. After building
`Fsdb.Cli` in Release, run:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py 1DC0 1E00 --collation utf8mb4_unicode_520_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1DC0:1E00 --collation utf8mb4_unicode_520_ci
```

The expanded comparison has zero differences across 777 searches at each
token size from 1 through 10 in natural, Boolean, and query-expansion modes.
