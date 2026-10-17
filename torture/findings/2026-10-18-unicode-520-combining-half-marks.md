# Unicode 5.2 combining half marks in CJK ngram searches

Native MySQL 8.4.11 gives U+FE20–U+FE26 one primary weight under
`utf8mb4_unicode_520_ci`, then gives U+FE27–U+FE2F distinct weights. ICU's
current collation merges the whole block. With one-character ngrams, this
made fsdb match all 16 documents for any of the marks where MySQL matched
only the equivalent seven or the single written mark. Relevance scores also
diverged because the document frequency was wrong.

The collation fold now encodes MySQL's observed equivalence classes using an
escaped private-use marker. Run the disposable native MySQL comparison after
building `Fsdb.Cli` in Release:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range FE20:FE30 --collation utf8mb4_unicode_520_ci 1 2
```

The one-character corpus changed from 112 differences across 96 searches to
zero; the wider FE10–FE2F corpus also has zero differences at size 1. At size
2, natural and query-expansion searches match, but 48 Boolean result/score
comparisons remain. They involve U+FE24–U+FE2F in terms such as `中︤`.
`BooleanWordChars` uses MySQL's historical Unicode 5.0 table, where only
U+FE20–U+FE23 of this block are word characters. MySQL's ngram Boolean
behavior for the later marks needs a separate parser investigation before
changing that shared table or search grammar.
