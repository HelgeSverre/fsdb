# 2026-08-19 — Probe corpus expansion: triage of 161 oracle probes

The scenario probe corpus (Harness.fs `ScenarioProbes`) grew to 161 probes
across the scalar / relational / commerce / volume scenarios. Every probe was
run against MySQL 8.4.11 and fsdb. This note records what the run found:
what was fixed in fsdb, what is ledgered in `support/known-gaps.json`, what
was a bad probe, and the correctness divergences that remain open.

## Harness change: the corpus now runs to the end

The probe loop stopped at the first non-pass, so a single probe that hung
fsdb hid the 35 probes behind it. It now runs every probe and records all of
them in `probes.json`; the *first* failure still supplies the case's
classification, evidence hash, and signature, so ledger entries are
unchanged in meaning. A case counts as "known" only when the ledger covers
**every** failing probe in it — ledgering the earliest gap in the corpus
must not mark a case green while carrying an unreviewed failure later on.

## Fixed in fsdb (were real divergences or hangs)

- **`LIKE` could hang the server.** `likeMatch`'s `%` backtrack advanced its
  resume point without ever checking it against the end of the subject, so a
  pattern whose tail can never match spun forever: `SELECT 'x' LIKE '%\%'`
  never returns. All three backtrack sites are now guarded by `mark < slen`.
  This is a remote denial of service on any query with a user-supplied LIKE
  pattern. Found by `like_wildcards_and_escape`.
- **`JOIN ... USING (c)` mislabelled its coalesced column.** A bare
  `SELECT c` over a USING join projected the rewrite's `COALESCE(c, c)` as
  the output column *name*; MySQL labels it `c`. The rewrite now pins the
  written identifier as the label. Found by `join_using_tenant_id` /
  `join_using_order_id`.
- **`FLOOR`/`CEILING` collapsed DECIMAL to BIGINT.** MySQL answers a DECIMAL
  argument with a scale-0 DECIMAL; going through `toDouble` also lost digits
  past 2^53. Found by `decimal_round_truncate_floor`.
- **`ROUND` used half-away-from-zero on DOUBLE.** Approximate values round
  half-to-even in MySQL (`ROUND(2.5e0)` is 2, not 3), and past `Math.Round`'s
  15-digit limit fsdb multiplied by the reciprocal instead of dividing by the
  power, yielding `5.5509999999999995E-17` where MySQL has `5.551E-17`.
  Found by `decimal_vs_double_rounding_modes` / `double_vs_decimal_comparison`.
- **`TRUNCATE(d, n)` dropped trailing zero scale**, answering `68632858`
  where MySQL has `68632858.00`. Found by `decimal_round_truncate_floor`.
- **`LEFT`/`RIGHT` corrupted binary data.** They went through the text view
  of a `VBytes` operand, so every non-ASCII byte came back as its multi-byte
  UTF-8 encoding: `HEX(LEFT(binary_value, 4))` returned six bytes,
  `22C2AB05C385` instead of `22AB05C5`. They now slice bytes on a binary
  operand. Found by `binary_hex_and_lengths` /
  `blob_null_and_length_distribution`.
- **JSON rendering: key order and quote escaping.** MySQL prints object keys
  in stored order (shortest first, ties lexicographic) and escapes an
  embedded quote as `\"`; fsdb kept insertion order and emitted
  `System.Text.Json`'s `"`. Found by `json_object_array_construction` /
  `json_set_insert_replace_render`.

## Bad probes (fixed or deleted)

- `datetime_component_and_truncation` aliased a column `year_month`, a
  reserved word — MySQL rejected the probe itself. Renamed to
  `year_month_part`.
- `json_unquoted_vs_quoted_compare` applied `->` / `->>` to a function
  result; MySQL's grammar only accepts a column on the left, so the probe
  errored on the oracle. Rewritten with `JSON_EXTRACT` / `JSON_UNQUOTE`.
- `vector_string_round_trip_unsupported` and
  `vector_distance_cosine_unsupported` called MySQL 9 VECTOR builtins that
  the 8.4 oracle does not have. A probe that errors on the oracle compares
  nothing; both deleted.

## Ledgered gaps (20 signatures in `support/known-gaps.json`)

Wave W3 closed the parser gaps listed below under INTERSECT/EXCEPT,
VALUES ROW, XOR, EXTRACT/TIMESTAMPDIFF/typed temporal literals, and the
JSON_TABLE EXISTS PATH / DEFAULT clauses — see "Closed: the parser
additions (wave W3)" at the end. The ledger listing that follows is the
pre-W3 state, kept for the record; what remains is CTEs, window frames and
the wider window set, LATERAL, ROLLUP/GROUPING, and JSON_TABLE's NESTED
PATH. Every one of the 20 surviving signatures is an `fsdb_probe_parser_gap`
— a refusal, never a wrong answer.

Adding INTERSECT/EXCEPT to the grammar changed FParsec's expected-token
list, which is part of a parse gap's evidence detail and therefore part of
its signature, so every surviving signature was regenerated from the run
artifacts rather than edited in place.

## Ledgered gaps before wave W3 (32 signatures)

fsdb refuses or errors where MySQL succeeds. No silent wrong answer in any
of these — they are missing features, not divergences.

Parser (32) — the whole ledger, now that the execution gaps are closed:

- **Common table expressions** — `WITH` / `WITH RECURSIVE` do not parse at
  all: `cte_bucket_summary`, `cte_chained_reference`,
  `cte_window_dense_rank_per_tenant`, `cte_ntile_decile_bounds`,
  `recursive_integer_series`, `recursive_payment_hierarchy_depth`,
  `recursive_series_joined_to_table`.
- **Window frames and the wider window-function set** — no `ROWS`/`RANGE`
  frame clause, no `LAG`/`LEAD` offset/default arguments, no named `WINDOW`,
  no `FIRST_VALUE`/`LAST_VALUE`/`NTH_VALUE`/`CUME_DIST`, no aggregate under
  `OVER`: `window_rows_frame_trailing`, `window_range_frame_symmetric`,
  `window_running_total_decimal`, `window_over_grouped_rows`,
  `lag_lead_offset_defaults`, `first_last_nth_value_named_window`,
  `percent_rank_cume_dist_ntile`.
- **`LATERAL`** derived tables: `lateral_correlated_aggregate`,
  `lateral_top_one_per_tenant`, `lateral_top_line_item_per_order`.
- **`INTERSECT` / `EXCEPT`** (plain and `ALL`): `intersect_set_operation`,
  `except_set_operation`, `intersect_all_multiset_intersection`,
  `except_all_multiset_difference`.
- **`GROUP BY ... WITH ROLLUP`** and `GROUPING()`:
  `rollup_with_grouping_flag`, `rollup_two_level_with_expression_key`.
- **`VALUES ROW(...)` table constructor**: `values_table_constructor`,
  `values_joined_to_table`.
- **`XOR`**: `null_three_valued_logic`, `null_case_and_between_semantics`.
- **`EXTRACT(unit FROM ...)`, `TIMESTAMPDIFF`, typed `DATE '...'`
  literals**: `datetime_component_and_truncation`,
  `interval_arithmetic_varieties`.
- **JSON_TABLE clauses outside the shipped subset** (`NESTED PATH`,
  `EXISTS PATH`, `DEFAULT ... ON EMPTY|ERROR`) — deliberate refusals, see
  `2026-08-19-json-table-gaps.md`: `json_table_nested_path_unsupported`,
  `json_table_exists_path_unsupported`,
  `json_table_default_on_empty_error_unsupported`.

Execution (0). Every builtin listed here on 2026-08-19 now ships — see
"Closed: the missing builtins (wave W2)" below.

## Open real divergences (NOT ledgered)

None. The six that were open here — the whole "wrong answer, not a refusal"
class in this corpus — were fixed on 2026-08-19; the section below records
what they were and what the fixes do not cover. Every probe in the corpus now
either matches MySQL 8.4.11 exactly or fails against a ledgered signature.

## Closed: the six wrong-answer divergences

Probes `unsigned_cast_wraparound`, `json_arrow_first_element`,
`json_set_insert_replace_render`, `json_scalar_coercion_compare`,
`json_unquoted_vs_quoted_compare`, and `json_cross_type_ordering` all pass.
They were the only unledgered failures in the corpus, so `known-gaps.json`
neither shrank nor grew: it stays at 43 signatures, with none stale.

### 1. Unsigned 64-bit integers — fixed

`Value` gained a `VUInt of uint64` case, threaded through `toText`, the
tagged-text (`toWire`) and binary (`encodeValue`, tag `0x09`) codecs,
`mysqlTypeOf`, comparison, arithmetic promotion, `Storage.coerceValue`'s
`BIGINT UNSIGNED` column branch, the unique-index key encoder, and the wire
protocol (`Protocol` translates `Value.TypeLongLongUnsigned` into LONGLONG
plus `UNSIGNED_FLAG` in the column definition, and parses the binary-protocol
row value as `uint64`). `CAST(x AS UNSIGNED)` gets its own executor branch
because a cast *wraps* into the domain where a column clamps. An integer
literal past `BIGINT`'s signed range now parses as `VUInt` rather than
collapsing to a double.

Follow-up (same campaign, later commit): the first two "not covered"
ceilings below were themselves silent wrong answers, so they are now closed
rather than deferred.

- Unsigned arithmetic outside `[0, 2^64)` raises 1690 like MySQL
  (`CAST(1 AS UNSIGNED) - 2`). `Value.narrowUnsigned` throws
  `Value.UnsignedOutOfRange` — arithmetic still has no `Result` channel —
  and `Executor`'s `BinOp` arm (plus a `QueryHandler` net for the paths that
  reach `Value.add` some other way) turns it into the ERR packet. MySQL's
  message names the offending expression; this one doesn't.
- A `BIGINT UNSIGNED` column range-checks its writes: 1264 in strict mode,
  clamped otherwise. The signed integer types still clamp unconditionally —
  that inherited ceiling stays, marked `ponytail` on `Storage.coerceValue`.
- Unary minus folds into a numeric *literal* (MySQL's own lexing), so
  `-9223372036854775808` is BIGINT's signed minimum rather than `0 -` an
  unsigned literal, which the 1690 rule above would now refuse.
- `CAST(<double too large> AS UNSIGNED)` clamps at signed `BIGINT` max like
  MySQL; a negative value below signed `BIGINT` raises 1690.

### 2-3. JSON comparison — fixed

`Value.compare` now pulls any comparison with a `VJson` operand into the JSON
domain: the non-JSON side converts to JSON first, then MySQL's documented type
precedence decides before content does (JSON NULL < number < string < object <
array < boolean < date < time < datetime < opaque < blob). JSON strings compare
by code unit, not the `ai_ci` collation — oracle-verified. `CAST(x AS JSON)`
now yields a JSON-*typed* value (and normalizes the document, and raises 3141
on non-JSON text) instead of routing through `coerceValue`'s text branch,
which is what let the ordering rules apply to it at all.

Follow-up: `CAST` was not the only source of JSON-typed values. A `JSON`
*column* (`Storage.coerceValue`) and a `JSON_TABLE` `COLUMNS(... JSON PATH
...)` column both handed back `VString`, so their comparisons fell out of
the JSON domain again — `json_col = CAST('"a"' AS JSON)` was 0 and `ORDER BY
json_col` sorted rendered text. Both now yield `VJson`. A SQL *string*
operand still converts to the JSON string it spells, not a parsed document
(oracle: `CAST('"a"' AS JSON) = '"a"'` is 0, `= 'a'` is 1).

TIME and OPAQUE ranks are unreachable placeholders — no `Value` case produces
them, and fsdb has no BIT type.

### 4. Index path on a non-array — fixed

`Functions.navigateJson` treats a non-array as a one-element array for
`$[0]`/`$[-1]`, and misses on any other index.

### 5. Supplementary-plane characters in JSON output — fixed

`Functions.jsonQuote` replaces `System.Text.Json`'s encoders for JSON string
literals: it escapes exactly what MySQL escapes (`"`, `\\`, `\b \f \n \r \t`,
other C0 controls as `\u00xx`) and emits everything else — DEL, `<&>/`, and
astral-plane characters alike — literally. `UnsafeRelaxedJsonEscaping` and
`JavaScriptEncoder.Create UnicodeRanges.All` were both verified to escape
surrogate pairs regardless.

## Closed: the missing builtins (wave W2)

`JSON_DEPTH`, `JSON_ARRAYAGG`, `JSON_OBJECTAGG`, `DAYOFYEAR`, `BIT_COUNT`,
`MAKEDATE`, `CONVERT_TZ` and `EXPORT_SET` are implemented; `CONV` was
already registered but answered NULL where MySQL truncates at the first
invalid digit (`CONV('12abc', 10, 10)` is `12`, not NULL) and had no signed
output for a negative `to_base`. Every expected value was read off the
8.4.11 oracle before the code was written, and each has Expecto coverage in
`ExecutorTests`' "builtins pinned to the 8.4 oracle" list.

Two things beyond the builtins themselves had to move:

- **`a MOD b`** — the word spelling of `%` — was not an infix operator in
  the parser, which is what actually blocked `field_elt_export_set`
  (`ELT(1 + (id MOD 3), ...)`); `FIELD`/`ELT`/`EXPORT_SET` were mis-filed as
  a builtin gap. It now parses like `DIV`, with the same word-boundary guard
  so a column named `mode_id` is still a column.
- **Multi-argument and NULL-preserving aggregates.** `Executor.evalAggregate`
  drops NULL rows and passes one value per row to a registered `Aggregate`.
  `JSON_ARRAYAGG` must keep NULLs (`[1, null]`), and `JSON_OBJECTAGG` takes
  two arguments per row, so both fold directly in `evalAggregate` the way
  `GROUP_CONCAT` already did, via `Functions.jsonArrayAggregate` /
  `jsonObjectAggregate`. The generic registry path is unchanged.

`CONVERT_TZ` accepts numeric `[+-]HH:MM` offsets (range ±14:00) and answers
NULL for anything else, including named zones and `SYSTEM`. That matches the
oracle exactly as configured: it has no `mysql.time_zone*` rows loaded, so
`CONVERT_TZ(t, 'UTC', 'America/New_York')` is NULL there too. `SYSTEM` is
the one spelling where the oracle answers and fsdb does not — it resolves to
the *server's* local zone. The engine now resolves that spelling through the
process-local `TimeZoneInfo`; named zones still require loaded time-zone data.

Ledger effect: 43 signatures down to 32. Eleven belonged to the probes above
and are gone; the twelfth removal is `rollup_two_level_with_expression_key`,
whose parse error moved from the `MOD` on column 28 to `WITH ROLLUP` on
column 121 — still an unimplemented feature (ROLLUP/GROUPING), re-ledgered
under its new signature.

## Closed: the parser additions (wave W3)

All oracle-pinned against MySQL 8.4.11 before the code was written.

- **`XOR`** — a new `Ast.Op` case, three-valued (either operand unknown
  makes the answer unknown, unlike `OR`), sitting between `OR` and `AND` in
  the precedence chain (`1 XOR 1 OR 1` is 1, `1 XOR 1 AND 0` is 1).
- **`EXTRACT(unit FROM expr)`** — its own parser atom (the separator is
  `FROM`, not a comma), plus `Functions.extractFn`. Composite units
  concatenate their components as digits with each lower one zero-padded:
  `DAY_SECOND` is 4050607, `DAY_MICROSECOND` 4050607123456.
- **Typed temporal literals** `DATE '...'` / `TIME '...'` / `TIMESTAMP
  '...'` — constant-folded in the parser, because MySQL *rejects* a
  malformed one (1525) where `DATE('...')` answers NULL. fsdb refuses too,
  but as a 1064 parse error: `Parser.parse` has no error-code channel.
  `TIMESTAMP '2020-01-01'` (no time part) is rejected, matching the oracle.
- **`VALUES ROW(...), ROW(...)` as a table** — desugared in the parser into
  the `UNION ALL` of one-row `SELECT`s it is equivalent to, so there is no
  new `FromItem` case and no new executor path. Without a column list the
  columns are named `column_0`, `column_1`, ... like MySQL's.
- **`INTERSECT` / `EXCEPT`, plain and `ALL`** — `Ast`'s union `rest` list
  now carries a `SetOp` instead of an is-`ALL` bool. `ALL` is real multiset
  arithmetic (INTERSECT ALL takes the lesser multiplicity, EXCEPT ALL
  subtracts), pinned on `[1,1,2,3]` against `[1,2,2]`. INTERSECT binds
  tighter than UNION/EXCEPT, so `runUnionStmt` collapses the INTERSECT runs
  before folding the rest left to right. A parenthesized set-operation group
  splices into the enclosing branch list only when nothing follows it —
  `(A UNION B) INTERSECT C` would flatten into the wrong grouping, so the
  grammar refuses it rather than answering wrongly.
- **JSON_TABLE `EXISTS PATH` and `DEFAULT ... ON EMPTY|ERROR`** — a matched
  JSON *null* is NULL and takes neither branch; a missing path takes ON
  EMPTY; an uncoercible value ('5x' or an array into INT) takes ON ERROR.
  `ERROR ON EMPTY|ERROR` (raise rather than substitute) is still refused.

Two things the corpus caught only once the parse gaps were gone, both
silent wrong answers rather than refusals:

- **`TIMESTAMPADD` parsed but was never registered**, so it answered 1305.
  It is now `DATE_ADD` with the arguments reordered.
- **Composite `INTERVAL` units did not exist.** `INTERVAL '1:30'
  HOUR_MINUTE` fell through `tryParseIntervalArg`'s single-number parse and
  degenerated into *numeric* subtraction (`1996`), and `INTERVAL '2-3'
  YEAR_MONTH` into NULL. All eleven composite units now normalize to a
  single simple unit (months, seconds, or microseconds) before
  `addInterval` sees them, with MySQL's right-aligned "a short value left
  out the leftmost components" rule and its treatment of a trailing
  microsecond component as a decimal *fraction* ('1.5' SECOND_MICROSECOND
  is 1.5 s). A value with *more* components than the unit names is NULL,
  not silently truncated — '1:2:3' HOUR_MINUTE and '1-2-3' YEAR_MONTH are
  both NULL in 8.4.11. `addInterval` also stopped returning the input unchanged for an
  unrecognized unit — a silent no-op — and answers NULL instead.

Ledger effect: 32 signatures down to 20.

## Closed: the last 20 (wave W4)

Everything the pre-W4 ledger excused now runs, so `support/known-gaps.json`
holds an empty signature array. The suite passes at seed 1 across all four
scenarios with nothing to excuse, which is the point of emptying it: a
regression in any of these fails loudly instead of matching a stale
signature.

- **Common table expressions** — `WITH` and `WITH RECURSIVE`.
- **Window frames and the wider window set** — `ROWS`/`RANGE` frames, named
  `WINDOW`, `LAG`/`LEAD` offset and default arguments,
  `FIRST_VALUE`/`LAST_VALUE`/`NTH_VALUE`/`CUME_DIST`, aggregates under
  `OVER`.
- **`WITH ROLLUP` and `GROUPING()`**.
- **`LATERAL` derived tables**, in both the comma-join and `JOIN ... ON TRUE`
  spellings.
- **JSON_TABLE `NESTED PATH`** — including sibling NESTED PATHs (which do
  *not* cross-join), two-level nesting, and `FOR ORDINALITY` inside a nested
  block, all with MySQL's outer-join padding of the unmatched siblings.

One divergence the new ROLLUP probes exposed, now fixed: `WITH ROLLUP`
materializes each grouped column into a nullable temporary that loses its
ENUM type, so MySQL sorts an ENUM group key *lexically* under ROLLUP where a
plain `GROUP BY` sorts it by declaration ordinal. Chasing that turned up a
second, unrelated one — an ENUM in numeric context (`status + 0`,
`CAST(status AS UNSIGNED)`, `SUM`/`AVG`/`STDDEV`/`VAR`/`BIT_*`) reads its
declaration ordinal, typed DOUBLE, where fsdb read the label as 0.
`MIN`/`MAX`/`COUNT`/`GROUP_CONCAT` keep the label. Three probes
(`enum_numeric_context`, `enum_aggregates_split_numeric_and_label`,
`enum_group_by_sorts_by_ordinal`) pin all of it.

Known non-gap: `suite --seed 7` classifies the scalar scenario
`oracle_rejected` — the generator emits a `unsigned_tiny + signed_tiny` sum
that MySQL 8.4.11 itself refuses with 1690. That is a generator bug, not an
fsdb divergence; a rejected oracle compares nothing.
