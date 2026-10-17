# Unicode 5.2 collation and CJK ngram membership

The existing multi-column CJK corpus passes under the server's default
collation, but `utf8mb4_unicode_520_ci` changes which ngrams compare equal.
Before the fix, native MySQL 8.4.11 and fsdb differed on 114 of 387 searches
at token size 2. The spacing and format-control differences now match.

Run the opt-in comparison after building `Fsdb.Cli` in Release:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_unicode_520_ci 2
```

The command exits nonzero if differences remain. It starts disposable
native MySQL and fsdb servers, verifies the MySQL version, and compares ordered
IDs and relevance across natural, Boolean, and query-expansion modes. The
collation option also accepts `utf8mb4_unicode_ci` and `utf8mb4_general_ci` for
subsequent comparisons.

A seven-document reduction isolates Unicode spacing and format characters.
With `body` values `生<character>日`, empty `title`, and characters U+3000,
U+00A0, U+200C, U+200D, U+2060, U+180E, and ASCII space, MySQL's natural
search for `生\u3000日` matches the first five rows. Fsdb previously matched
only the U+3000 and U+00A0 rows. MySQL also distinguishes U+180E from
U+200C/U+200D/U+2060 in this corpus, while ICU treats all four as ignorable.
The fix applies MySQL's measured PAD SPACE equivalences for the legacy Unicode
collations and preserves U+180E's UCA 5.2 primary weight with an escaped key.

The full `utf8mb4_unicode_520_ci` corpus now has zero differences at token
sizes 1–4 and 6–10. At size 5, one pre-existing Boolean search for `하늘`
still returns a row in fsdb and none in MySQL, affecting both membership and
relevance; the same two mismatches occur at the parent revision. The size-2
`utf8mb4_unicode_ci` corpus improves from 88 to 36 differences; those
remaining cases require separate legacy UCA work. The size-2 legacy
punctuation and radical lanes remain at zero. The pinned MySQL 8.4 container
does not include a MeCab plugin; this comparison covers ngram only.
