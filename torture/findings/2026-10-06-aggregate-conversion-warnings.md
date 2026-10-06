# Numeric aggregate conversion warnings

MySQL 8.4.11 emits warning 1292 when ordinary SUM or AVG converts malformed
text or a binary string to DOUBLE. Numeric prefixes contribute their parsed
value; invalid nonblank input contributes zero. Empty strings, whitespace,
and underflow to zero do not warn. Overflow clamps to the finite DOUBLE limit
and warns. DISTINCT compares the converted numbers but suppresses these
conversion warnings. COUNT, MIN, and MAX do not perform this numeric conversion.

For inputs `12x`, `12x`, `bad`, an empty string, whitespace, NULL, and ` 2 `,
SUM is 26 and AVG is 26/6. Each emits three conversion warnings. SUM DISTINCT
is 14 and AVG DISTINCT is 14/3, with no conversion warnings.

Window evaluation affects the warning count. A prefix ending at CURRENT ROW
and a whole-partition window each convert every input once. A ROWS frame from
one preceding row through the current row rereads malformed inputs and emits
six warnings for this dataset. Conversion diagnostics must follow evaluation;
deduplicating warning messages would incorrectly discard warnings for repeated
input values and sliding frames. Partition key order is also observable in
the warning rows, so partition evaluation follows collation-aware key order.

The executor shares its numeric accumulator between grouped aggregates and
stable SUM/AVG inputs in growing ROWS frames and default RANGE prefixes.
Exact inputs retain DECIMAL accumulation; approximate inputs retain DOUBLE
accumulation. DISTINCT suppresses diagnostics only around numeric conversion,
leaving expression evaluation outside that suppression. Unclassified custom
functions and volatile expressions retain their existing evaluation strategy.
Offset RANGE frames remain outside this optimization.

Reproduction: `torture/scripts/aggregate-warning-oracle.fsx` checks values and
warning rows against native MySQL 8.4.11 over text and prepared protocols.
The `numeric-aggregate-conversion` differential contract additionally covers
binary strings, overflow/underflow, partitions, peers, and frame offsets.
Expecto regressions check the conversion boundary and custom-function behavior.

Validation: `just check` builds without warnings and passes 2,834 tests.
Native MySQL 8.4.11 passes both maintained oracle scripts. The differential
run passes 3,529 steps across 40 cases with no differences; its manifest is
`torture/artifacts/runs/20261006T214444058-484/contracts/manifest.json`.
The disposable oracle server is shut down after validation.
