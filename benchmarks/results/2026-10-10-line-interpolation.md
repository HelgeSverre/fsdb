# Line-interpolation wire latency snapshot

Measured at `681f8bc8` on 2026-10-10 against disposable native MySQL
8.4.11 and an in-memory fsdb Release build. Both targets used the same
PyMySQL client over local wire connections. Each case had 30 warmups and
five timed batches; the table reports the median per query, with the minimum
and maximum batch means in parentheses. Point cases ran 100 queries per
batch, multipoint cases 30, and the control 150. Results were fetched before
the next query. The two servers ran concurrently; cases were measured in
MySQL-then-fsdb order.

| Query shape | MySQL ms/query | fsdb ms/query |
|---|---:|---:|
| `SELECT 1` | 0.034 (0.027–0.036) | 0.404 (0.385–0.445) |
| Planar `ST_LineInterpolatePoint`, fraction 0.35 | 0.036 (0.031–0.045) | 0.489 (0.466–0.608) |
| EPSG 4326 `ST_LineInterpolatePoint`, fraction 0.35 | 0.044 (0.036–0.056) | 0.590 (0.575–0.634) |
| Planar `ST_LineInterpolatePoints`, fraction 0.01 | 0.049 (0.045–0.051) | 0.606 (0.589–0.633) |
| EPSG 4326 `ST_LineInterpolatePoints`, fraction 0.01 | 0.082 (0.076–0.095) | 0.687 (0.631–0.703) |

The point queries returned equivalent WKT to printed precision except for
sub-ULP geographic coordinate rounding. The multipoint queries returned
100 planar and 99 geographic members from both servers. The 99-member result
is consistent with their floating-point distance accumulation, not a
100-point assertion.

The control varied more than the repository's usual 20% repeatability
threshold, especially for MySQL. This short run is directional. It suggests
fixed query, parser, and wire costs dominate the simple interpolation cases;
it does not establish a precise engine ratio or identify a code-level
regression. The larger multipoint cases are only modestly above fsdb's
control in this sample. A longer alternating-order campaign is needed before
targeting interpolation arithmetic for optimization.
