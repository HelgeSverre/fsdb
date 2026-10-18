# Unicode 5.2 extended combining marks in CJK ngram searches

Native MySQL 8.4.11 assigns 80 distinct implicit primary weights to
U+1AB0–U+1AFF under `utf8mb4_unicode_520_ci`, including code points that
were unassigned when Unicode 5.2 was published. ICU treats many newer marks
as ignorable. In a multi-column ngram index containing one document per
printable code point, fsdb therefore merged postings that MySQL kept apart.

Before the fix, the native oracle found 156 mismatches across 186 searches at
token size 1 and 332 at size 2, including different row sets and relevance
scores. The collation now gives this block separate escaped synthetic keys.
The marker is escaped in literal input so private-use text cannot alias a
generated key. The mapping applies only to `utf8mb4_unicode_520_ci`.

The probe also searches marks beside and on either side of a base character.
After building `Fsdb.Cli` in Release, run:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py 1AB0 1B00 --collation utf8mb4_unicode_520_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1AB0:1B00 --collation utf8mb4_unicode_520_ci
```

The expanded native comparison has zero differences across 372 searches at
each token size from 1 through 10, including natural, Boolean, and
query-expansion modes.
