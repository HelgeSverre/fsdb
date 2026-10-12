# Integer NULLIF functional keys

The digest-pinned MySQL 8.4.11 oracle accepts
`NULLIF(integer_column, integer_literal)` as a functional index, including a
unique key. Rows whose source equals the comparison literal project NULL, as
do source NULLs. A unique key admits multiple such rows but rejects duplicate
non-NULL results with 1062/23000. MySQL uses the maintained key for equality
and updates it after mutations.

The hidden result is always nullable. `TINYINT`, `SMALLINT`, `MEDIUMINT`, and
`INT` sources promote to an `INT` result, preserving signedness, with a
five-byte key. `BIGINT` sources retain their width and signedness, with a
nine-byte key. A `TINYINT NOT NULL` source still gets the NULL byte because
`NULLIF(v,5)` can produce NULL. These result types were checked with MySQL
`CREATE TABLE AS SELECT`; access and key width were checked with `EXPLAIN`.
The `BIGINT UNSIGNED` maximum remains exact. MySQL also indexes a composed
`ABS(NULLIF(v,0))` expression.

Fsdb now records a literal NULL comparison in the physical-expression model.
Key projection, uniqueness, equality probes, function-override guards, and
key-length metadata use that same expression identity. The focused Expecto
regression covers nullable uniqueness, narrow-type promotion, and the exact
unsigned maximum. The differential case covers rows, duplicate diagnostics,
a nonzero comparison literal, composition with `ABS`, and key maintenance
through an update. All 3,492 Fsdb tests pass. All 11
`nullif-functional-index` steps match MySQL in
`20261012T104107235-2663/contracts`; the full run has 143 cases and
19,019 steps, with the same ten previously documented differences as before.
Non-literal comparisons and broader compositions remain open.

## Text comparison with column collations

Native MySQL 8.4.11 also accepts a unique `NULLIF(v,_utf8mb4'x')` key on
`VARCHAR(20) COLLATE utf8mb4_0900_bin`. The hidden nullable text key is 83
bytes. Plain `'x'` and `_utf8mb4'x'` in the comparison use the same key;
`_latin1'x'` and numeric comparison scan. A source `x` projects NULL,
while `X` and `x ` remain distinct non-NULL values. An explicit duplicate
`X` fails with 1062. Fsdb now retains the comparison literal's charset
identity and maintains this exact-binary key.

MySQL also maintains the same nullable key for `utf8mb4_bin` (PAD SPACE),
`utf8mb4_0900_ai_ci` (case and accent folding without padding), and
`latin1_bin` (one-byte PAD SPACE). The `latin1_bin` hidden key is 23 bytes.
Fsdb now passes the source column's collation into physical projection, so
text `NULLIF` keys use the same equality classes as ordinary comparisons.
The focused regressions and `text-nullif-functional-index` differential
contract cover equality, nullable uniqueness, numeric comparison, and
updates across these representative collations. Non-`VARCHAR` text types,
broader mixed-charset expression identities, and non-literal comparisons
remain open. MySQL uses the default `utf8mb4_0900_ai_ci` key for an
equivalent `_latin1'X'` comparison against a stored `_utf8mb4'x'` literal.
Fsdb now does the same when the source collation says the literals are equal;
`_binary` retains its distinct comparison semantics and scans.

MySQL also uses a `utf8mb4_bin` key stored for `NULLIF(v,'x')` to answer
`NULLIF(v,'X')='x'`, even though a source `x` should satisfy that predicate.
The MySQL 8.4.11 indexed query returned no row in the audited fixture.
Fsdb scans when the two literals do not collate equal, preserving the SQL
result. This is an intentional optimizer-plan divergence, not a claimed
result match for that MySQL bug.

`just check` passed all 3,500 tests. All nine steps of the initial text contract
matched native MySQL 8.4.11 in
`torture/artifacts/runs/20261012T164135996-60894/contracts`. The broader
run retained two unrelated result differences in the established regex and
mixed-type join cases. The native server uses `lower_case_table_names=2` on
macOS; the digest-pinned container lane was not rerun for this change.
The later 16-step text contract passed in
`torture/artifacts/runs/20261012T165041591-63050/contracts` across the
additional source collations. That run retained the same two unrelated
regex and mixed-type join result differences.
The final 17-step contract, including accent-folded unique-key rejection,
passed in `torture/artifacts/runs/20261012T165406580-63639/contracts`.
That full run reported the regex difference and four existing mixed-type
join signatures; the text `NULLIF` case remained fully matched.
The later 18-step text contract passed in
`torture/artifacts/runs/20261012T170209162-66020/contracts`, including the
equivalent mixed-charset query. That full run retained the regex and one
mixed-type join result difference.
