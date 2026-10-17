# Legacy Unicode collations and CJK ngram membership

The existing multi-column CJK corpus passes under the server's default
collation, but `utf8mb4_unicode_520_ci` and `utf8mb4_unicode_ci` change which
ngrams compare equal.
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

The full `utf8mb4_unicode_520_ci` corpus now has zero differences at every
token size from 1 through 10 (3,870 searches). At size 5, fsdb previously
matched the short Boolean term `하늘` against the five-Jamo ngram `하늘`:
ICU composes that Jamo sequence into the same key, whereas MySQL's UCA 5.2
assigns distinct weights. The shared legacy Unicode Jamo folding preserves
the measured compatibility-Jamo equivalences without composing it into Hangul
syllables. Under both `utf8mb4_unicode_ci` and `utf8mb4_unicode_520_ci`, MySQL
gave distinct weights to all 256 U+1100–U+11FF Jamo checked, and all 146
compatibility or halfwidth Jamo whose NFKD decomposition lies in that range
compared equal to their decomposition. After combining the legacy UCA folds
with this Jamo rule, the full `utf8mb4_unicode_ci` corpus also has zero
differences at every token size from 1 through 10 (3,870 searches). The
size-2 legacy punctuation and radical lanes remain at zero. The pinned MySQL
8.4 container does not include a MeCab plugin; this comparison covers ngram
only.
