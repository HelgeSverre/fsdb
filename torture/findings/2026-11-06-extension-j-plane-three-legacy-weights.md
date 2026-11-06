# Extension J and plane-three legacy weights

[Unicode 17 defines CJK Extension J at U+323B0–U+3347F](https://www.unicode.org/versions/Unicode17.0.0/core-spec/chapter-18/).
The first 80 positions all disagreed with native MySQL 8.4.11 in fsdb's
legacy `WEIGHT_STRING` encoder. MySQL returns `FFFD` under
`utf8mb4_general_ci` and `utf8mb4_unicode_ci`, and distinct implicit
weights under `utf8mb4_unicode_520_ci` (`FBC6A3B0` at U+323B0).

Extending the existing supplementary-ideograph rule through Extension J
resolved every singleton there. An adjacent-pair check then exposed the
U+3347F/U+33480 boundary: MySQL continues the implicit sequence into the
unassigned tail of plane three, while fsdb fell back for the combined
string. The encoder now covers U+30000–U+3FFFF as one contiguous range.
The focused regression pins the Extension H/J boundary, both Extension J
endpoints, the first position after the block, and the plane's last scalar.
It failed before the change and passes after it.

The native oracle now finds zero exact-byte mismatches for all 65,536
singletons and 65,535 internal adjacent pairs in plane three under each
of the three legacy collations: 393,213 comparisons. Its Extension J pair
lane also includes U+3347F/U+33480 and matches in all three collations.
This establishes the encoder's exact-byte behavior within plane three;
pairs crossing from U+3FFFF into plane four were not checked.

Python's Unicode 16 character database marks Extension J unassigned, so
the ngram oracle's old `isprintable()` filter produced **zero** searches.
The new `--all-range-scalars` option explicitly indexes the requested code
points, excluding surrogate values. With it, first, middle, and final
256-position pages of Extension J match MySQL in 1,536 searches per page,
collation, and token size. Across three legacy collations and sizes 1–3,
that is 41,472 searches with zero membership or relevance differences.
An attempted whole-block size-1 run was stopped after more than seven minutes
because its 4,304-row index and search corpus were too costly on this host;
it produced no completed comparison. The ngram key path did not change.

Reproduce the exact-byte lane after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 30000 40000 --collation utf8mb4_unicode_520_ci --compare-fsdb
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 30000 3FFFF --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 323B0:324B0 --all-range-scalars --collation utf8mb4_unicode_520_ci 1 2 3
```

Repeat those commands with `utf8mb4_general_ci` and
`utf8mb4_unicode_ci`; for the ngram lane also use ranges
`32C00:32D00` and `33380:33480`.
