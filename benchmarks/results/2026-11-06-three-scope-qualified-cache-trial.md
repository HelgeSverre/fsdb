# Three-scope qualified scalar metadata cache trial

At `315a918a`, the nested
correlated scalar path caches successful metadata for a source-free
projection when its immediate outer scope has no further ancestor. A
temporary candidate let the same qualified-expression cache run with
additional ancestors. The guard still admitted only literals, qualified
columns, and binary operators in both projection and predicate.

The checked Release profile in
[`CorrelatedNestedProfile`](../CorrelatedNestedProfile/Program.fs)
used `FSDB_PROFILE_QUERY=three` and `FSDB_PROFILE_SECONDS=12`. It
parsed once, seeded 10,000 users and 50,000 orders, warmed ten queries,
then counted completed executions of a three-scope nested scalar query.
Every execution checked 100 result rows and a count total of 250.
Build and seed time were outside the measured window.

| Run order | Code | Queries in 12 seconds |
| --- | --- | ---: |
| A1 | Existing guard | 3,112 |
| B1 | Deeper qualified cache | 3,325 |
| A2 | Existing guard | 3,163 |
| B2 | Deeper qualified cache | 3,139 |

The first candidate run was 6.8% above its adjacent control; the
second was 0.8% below. The focused source-free scalar SELECT-boundary
test passed with the candidate, including ancestor references, missing
column errors, and repeated LATERAL evaluation. The measured gain did
not repeat, so the candidate was reverted and the Release profile
binary was rebuilt from unchanged production code. A future change
needs a stronger profile or a lower-cost binding strategy before
extending the cache to deeper scopes.
