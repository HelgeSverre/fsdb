# Geographic endpoint oracle

Compare shared geographic endpoint topology and the recorded global ring
corpus with the digest-pinned MySQL 8.4.11 oracle. The probe starts its own
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
relation differences and exactly the previously reviewed ring-76 mismatch
for seed 9173. Any other mismatch, including an unexpected resolution, fails
the run and must be investigated. The earlier case-92 difference is now a
normal regression in the F# suite.

To reproduce the remaining nonadjacent crossing directly, run
`uv run python check.py --crossing`. This checks all eight endpoint-order and
operand-order combinations against MySQL, prints their relation and distance
results, and deliberately exits nonzero while the discrepancy remains open.
It is a focused parity assertion, separate from the default known-gap corpus.

The shared-endpoint triples compare ST_Intersects, ST_Crosses, ST_Touches,
and ST_Distance. Ring generation reuses the maintained validity oracle so
case numbers preserve their recorded meaning. The default SRS axis order is
latitude/longitude in the generated WKT.

After returning to the repository root, remove the disposable oracle data:

```sh
docker compose -f torture/compose.yaml down -v
```
