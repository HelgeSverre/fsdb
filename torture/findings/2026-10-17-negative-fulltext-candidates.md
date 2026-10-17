# Negative Boolean full-text terms and candidate scopes

Flat Boolean searches with only `-` terms cannot match a row. The previous
posting accumulator nevertheless visited every excluded posting. An indexed
predicate that supplied no candidate rows did not prevent that work because
the cost choice counted only positive postings. Repeating one common negative
term therefore repeated a full posting traversal for an empty result.

The flat scorer now returns an empty result before constructing postings for
all-negative queries or empty candidate scopes. Mixed positive/negative
queries include exclusion postings in the candidate-probe cost choice. When
accumulating positive postings, each exclusion walks the smaller of its
posting and the matched-result set.

Native MySQL 8.4.11 and fsdb returned the same rows for a disposable fixture
covering an impossible indexed predicate, repeated exclusions, an excluded
positive match, and an allowed positive match. The focused full-text
regression also compares optimized scoring with the general scorer for
all-negative and missing-positive terms and checks empty candidate scopes.

For a same-host in-memory fsdb profile, a fresh server held 5,000 rows of
`common token` in `docs(id INT PRIMARY KEY, body TEXT, FULLTEXT(body))`. The
query was `SELECT COUNT(*) FROM docs WHERE id=-1 AND MATCH(body) AGAINST
(? IN BOOLEAN MODE)`, with `?` containing repeated `-common`. Each timing is
the median of three PyMySQL calls after loading the table; both builds used
the same Release CLI and port on macOS arm64. This measures the empty-scope
path, not general full-text throughput.

| Negative terms | Before | After | Result |
|---:|---:|---:|---:|
| 100 | 20.71 ms | 1.23 ms | 0 rows |
| 500 | 105.93 ms | 2.48 ms | 0 rows |

The 500-term query was about 43 times faster in this small run. Parsing still
depends on query length; the result does not imply a constant-time search or
remove broader full-text planning gaps.
