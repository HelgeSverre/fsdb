# Full-text scoring with equality-join bounds

Status: bounded inference implemented; broader MATCH planning remains in GAPS.md.

For an inner join on `o.id=d.owner_id`, `WHERE o.id=42` restricts the possible
values of `d.owner_id`. Fsdb uses this bound to select indexed scoring candidates
before preparing full-text source rows. Equality chains can span multiple
physical sources, with literal bounds in WHERE or ON. The original predicates
and joins still execute, and relevance uses the complete indexed corpus.

Inference requires uniquely resolved columns, identical column types and compatible
text collations. Each ON clause resolves names against its visible join prefix;
WHERE resolves against all sources. A later source with the same column name
does not make an earlier ON reference ambiguous. Numeric columns accept numeric literal bounds; text columns
accept string literal bounds. Outer joins, USING joins, disjunctions, mixed
comparison domains, and invalid forward ON references do not supply inferred
bounds. Existing source-local access remains available.

The restriction on literal domains matters: under `utf8mb4_0900_ai_ci`, `'①'`
and `'1'` compare equal, but `d.k=1` rejects `'①'`. Propagating a numeric literal
across a text equality would incorrectly drop a matching row.

## Oracle and regressions

The [native oracle](../scripts/fulltext-join-bounds-oracle.py) checks MySQL 8.4.11
on a disposable server:

```sh
python3 torture/scripts/fulltext-join-bounds-oracle.py
```

It verifies IDs and rounded relevance for direct, transitive, ON-literal, and
cross joins, plus text coercion, outer joins, and disjunctions. Unqualified
keys and bounds include a later-source name collision. Ambiguous WHERE/ON
references return error 1052; forward ON references return error 1054. The matching
Expecto regressions compare results with the explicit-bound query and count
virtual-column evaluations to verify candidate preparation. Before inference,
the 1,000-row fixture prepares 800 rows despite returning ten; the regression
requires fewer than 30 evaluations after inference.

## Targeted performance evidence

These timings measure the qualified-bound implementation at `7b4da9ff`.

The embedded Debug, in-memory profile described in the
[baseline snapshot](../../benchmarks/results/884fece3-fulltext-snapshot.md)
uses 10,000 documents and returns 100 rows. Five warmups precede seven batches
of ten queries, with `DOTNET_PROCESSOR_COUNT=4` on the same host.

| Query | Before median ms | After median ms |
|---|---:|---:|
| Bound supplied through join | 6.917 | 1.149 |
| Explicit redundant source bound | 0.811 | 1.217 |

After-change batch averages in milliseconds:

- Join bound: 1.11221, 1.23457, 1.10175, 1.25141, 1.17055, 1.13346, 1.14867.
- Explicit bound: 1.20385, 1.25312, 1.21730, 1.26226, 1.20239, 1.30870, 1.12162.

The formerly unbounded query is about 6 times faster in this profile. The
explicit-bound control is slower in this run; graph analysis adds work, and background activity remains uncontrolled.
This profile does not isolate the cause of the control difference. These figures
are diagnostic engine timings, not durable deployment or MySQL comparisons.

## Validation

`just check` passes 2,936 tests without build warnings or errors. The native
join-bound oracle and natural-phrase oracle pass. The contract lane passes
49 cases / 5,117 steps without differences at
`20261007T101140766-63814/contracts`.
