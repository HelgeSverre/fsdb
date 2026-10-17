# Unicode 5.2 combining half marks in CJK ngram searches

Native MySQL 8.4.11 makes U+FE20–U+FE26 primary-ignorable under
`utf8mb4_unicode_520_ci`, then gives U+FE27–U+FE2F distinct weights. ICU's
current collation ignores the whole block. With one-character ngrams, this
made fsdb match all 16 documents for any of the marks where MySQL matched
only the equivalent seven or the single written mark. Relevance scores also
diverged because the document frequency was wrong. `WEIGHT_STRING()` confirms
the distinction: U+FE20 and U+FE24 have an empty weight string, while U+FE27
has weight `FBC1FE27`. `中︠` and `︠中` both have the same primary weight as
`中`.

The collation fold now leaves the first seven marks ignorable and encodes the
later distinct weights using an escaped private-use marker. The oracle probes
marks alone, beside a base character, and on either side of a base character
so primary-ignorable marks cannot pass by accidentally acquiring a weight.
Run the disposable native MySQL comparison after building `Fsdb.Cli` in Release:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range FE20:FE30 --collation utf8mb4_unicode_520_ci 1 2
```

The original one-character corpus changed from 112 differences across 96
searches to zero. The expanded corpus has zero differences across 192 searches
at each of token sizes 1 and 2, including natural, Boolean, and query
expansion modes. The wider FE10–FE6F corpus also has zero differences across
504 searches at each of sizes 1 and 2, and the default multi-column corpus
has zero across 387 at each size. The apparent Boolean parser gap was caused
by incorrectly assigning a primary weight to the first seven marks in the
initial fix, now corrected.
