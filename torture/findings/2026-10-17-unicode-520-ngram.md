# Unicode 5.2 collation changes CJK ngram membership

The existing multi-column CJK corpus passes under the server's default
collation, but `utf8mb4_unicode_520_ci` changes which ngrams compare equal.
On native MySQL 8.4.11 and fsdb at ngram token size 2, the same 387 searches
produced 114 membership or relevance differences. This is a known gap, not a
passing differential lane.

Run the opt-in comparison after building `Fsdb.Cli` in Release:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_unicode_520_ci 2
```

The command exits nonzero while differences remain. It starts disposable
native MySQL and fsdb servers, verifies the MySQL version, and compares ordered
IDs and relevance across natural, Boolean, and query-expansion modes. The
collation option also accepts `utf8mb4_unicode_ci` and `utf8mb4_general_ci` for
subsequent comparisons.

A seven-document reduction isolates Unicode spacing and format characters.
With `body` values `生<character>日`, empty `title`, and characters U+3000,
U+00A0, U+200C, U+200D, U+2060, U+180E, and ASCII space, MySQL's natural
search for `生\u3000日` matches the first five rows. Fsdb matches only the
U+3000 and U+00A0 rows. MySQL also distinguishes U+180E from U+200C/U+200D/
U+2060 in this corpus, while fsdb conflates them. The same data expose
relevance differences, so returning the missing row IDs alone would not
complete the fix.

The next implementation step is to model the collation's ngram key equivalence
classes and scoring for these controls, then rerun the full size-2 corpus and
other startup token sizes. The pinned MySQL 8.4 container does not include a
MeCab plugin; this comparison covers ngram only.
