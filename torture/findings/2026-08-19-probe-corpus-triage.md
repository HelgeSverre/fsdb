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

## Ledgered gaps (43 signatures in `support/known-gaps.json`)

fsdb refuses or errors where MySQL succeeds. No silent wrong answer in any
of these — they are missing features, not divergences.

Parser (33):

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
- **`FIELD` / `ELT` / `EXPORT_SET`**: `field_elt_export_set`.
- **JSON_TABLE clauses outside the shipped subset** (`NESTED PATH`,
  `EXISTS PATH`, `DEFAULT ... ON EMPTY|ERROR`) — deliberate refusals, see
  `2026-08-19-json-table-gaps.md`: `json_table_nested_path_unsupported`,
  `json_table_exists_path_unsupported`,
  `json_table_default_on_empty_error_unsupported`.

Execution (10) — builtins that parse but are not registered:

- `JSON_DEPTH` (`shipping_address_contains_totals`,
  `json_valid_length_totals`, `json_extract_root_render`)
- `JSON_ARRAYAGG` (`customer_profile_arrayagg_unsupported`,
  `json_arrayagg_ordered_subset_unsupported`)
- `JSON_OBJECTAGG` (`tenant_settings_objectagg_unsupported`)
- `DAYOFYEAR` (`date_part_extraction`)
- `BIT_COUNT` (`bit_count_crc32_conv`)
- `MAKEDATE` (`calendar_week_and_makedate`)
- `CONVERT_TZ` (`convert_tz_numeric_offsets`)

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

Not covered, deliberately:

- MySQL raises 1690 when an unsigned expression leaves `[0, 2^64)`
  (`CAST(-1 AS UNSIGNED) * 2`, `CAST(1 AS UNSIGNED) - 2`). `Value`'s
  arithmetic has no error channel, so the result stays an exact `DECIMAL` —
  the same exit `VInt` overflow already takes. Marked `ponytail` on
  `Value.narrowUnsigned`.
- No range enforcement on an out-of-domain value written to an integer
  column (1264 in strict mode); `BIGINT UNSIGNED` clamps, matching the
  existing "integer columns are not range-checked" ceiling for every other
  integer type. Marked `ponytail` on `Storage.coerceValue`.
- `CAST(<double too large> AS UNSIGNED)` clamps at the *unsigned* ceiling
  where MySQL clamps at signed `BIGINT` max. Marked `ponytail` on the cast.

### 2-3. JSON comparison — fixed

`Value.compare` now pulls any comparison with a `VJson` operand into the JSON
domain: the non-JSON side converts to JSON first, then MySQL's documented type
precedence decides before content does (JSON NULL < number < string < object <
array < boolean < date < time < datetime < opaque < blob). JSON strings compare
by code unit, not the `ai_ci` collation — oracle-verified. `CAST(x AS JSON)`
now yields a JSON-*typed* value (and normalizes the document, and raises 3141
on non-JSON text) instead of routing through `coerceValue`'s text branch,
which is what let the ordering rules apply to it at all.

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
