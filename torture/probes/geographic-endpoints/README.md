# Geographic topology and validity oracle

Compare geographic endpoints, line topology, and ring/polygon validity
with the digest-pinned MySQL 8.4.11 oracle. The probe starts its own
in-memory FSDB server, terminates that server afterward, and connects to the
disposable MySQL service without modifying application databases.

From the repository root:

```sh
docker compose -f torture/compose.yaml up -d --wait mysql
dotnet build src/Fsdb.Cli -c Release --nologo
cd torture/probes/geographic-endpoints
uv sync --locked
uv run ruff check .
uv run ruff format .
uv run python check.py
```

The probe compares 1,000 deterministic shared-endpoint triples and 1,000
global rings for each of seeds 9173, 9174, and 9175. It requires no endpoint
relation or ring-validity differences. Any mismatch fails the run and must
be investigated. Both recorded long-edge cases are regressions in the F# suite.

To check the recorded nonadjacent crossing directly, run
`uv run python check.py --crossing`. This checks all eight endpoint-order and
operand-order combinations against MySQL, prints their relation and distance
results, and requires parity. It is separate from the default random corpus.

Run `uv run python check.py --line-pairs 1000` to compare independent global
segments using seed 33076, with 1,000 pairs for each of WGS 84 (4326), GRS 1980
(4258), and Clarke 1866 (4267). This checks intersects/crosses/touches and whether
distance is zero. Nonzero distance accuracy is outside this mode's scope.

Run `uv run python check.py --polygons` for the polygon-validity parity
assertion. It covers holes, multipart shapes, collections, independently
reversed rings, contact cycles, boundary-only vertices, antimeridian and polar
configurations, and global shapes with seed 9176. It also rechecks the two
recorded global component relations. The mode requires parity; any mismatch
exits nonzero. Its JSON records all fixed-fixture oracle results, the total
disagreement count, error counts, and the first 20 examples.
The verdict checks the complete corpus, rather than the truncated examples.
Use `--polygon-seed 9177` for an independent global sample or
`--polygon-srid 4258` / `--polygon-srid 4267` for GRS 1980 / Clarke 1866.

The shared-endpoint triples compare ST_Intersects, ST_Crosses, ST_Touches,
and ST_Distance. Ring generation reuses the maintained validity oracle so
case numbers preserve their recorded meaning. The default SRS axis order is
latitude/longitude in the generated WKT.

After returning to the repository root, remove the disposable oracle data:

```sh
docker compose -f torture/compose.yaml down -v
```
