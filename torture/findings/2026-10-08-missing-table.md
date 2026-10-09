# Missing-table identity and definition validation

Status: the audited missing-object diagnostics match MySQL 8.4.

## Native evidence

Run `python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-missing-table-native.json`.
The fixture records 30 scripts from disposable native MySQL 8.4.11 with a
64 MiB buffer pool and redo capacity. The corresponding fsdb replay matches
all rendered diagnostics, numeric error codes, and SQLSTATEs. Before the fix,
22 of these scripts differed.

## Established behavior

- A missing physical table reports 1146/42S02 with a normalized database/table
  name, including reads, writes, ALTER, TRUNCATE, SHOW metadata, DESCRIBE,
  EXPLAIN, CREATE TABLE LIKE, RENAME, prepared SELECTs, and CREATE VIEW sources.
- Ordinary references to an absent database report 1049/42000. TRUNCATE reports
  1146/42S02 even when its requested database is absent.
- DROP TABLE reports 1051/42S02 with the qualified target; DROP TABLE IF EXISTS
  on a missing table in an existing database retains note 1051.
- PREPARE and CREATE VIEW reject missing query sources instead of treating a
  failed lookup as unavailable result metadata. DUAL and registered virtual
  tables retain their metadata paths without invoking host row callbacks.
- SQL CREATE TABLE rejects an absent destination database. The separate storage
  and connection-bootstrap convenience for creating schemas remains unchanged.

`StorageError.NoSuchTable` carries database and table separately. Storage reads,
writes, schema operations, and SHOW metadata share its formatter. F# callers
matching this error now receive two fields instead of one. DROP and TRUNCATE
keep their statement-specific error classifications.

A later mixed-case wire probe on digest-pinned Linux MySQL, whose
`lower_case_table_names` is 0, retained `AbSeNt` in both the 1146 packet and
`SHOW WARNINGS`. A native MySQL 8.4.11 probe with
`lower_case_table_names=2` returned `absent` in both, matching fsdb's
advertised policy and the focused query-handler regression.

The existence checks exposed empty-database loss in temporary catalog overlays.
Hiding and restoring a shadowed table now preserve an existing database even
when it contains no permanent tables. Repeated temporary creation, reads, and
drops remain covered by the root tests and full wire suite.

The trigger warning replay now matches all 34 scripts, including its formerly
unqualified missing-table failure. Every audited trigger script is included in
the passing wire contract; no failure is enrolled in the known-gap allowlist.

## Validation and bounds

- `just check`: 3,131 tests pass; no build warnings or errors.
- Missing-object native fixture and fsdb replay: all 30 scripts match.
- Trigger warning replay: all 34 scripts match.
- Full native wire suite: 85 contracts / 11,652 steps / zero differences.
  Artifact: `torture/artifacts/runs/20261008T204626214-74341/contracts`.

This covers the recorded statement forms and diagnostic precedence. Quoted
identifiers containing dots and narrowing-ALTER truncation warnings remain open
in the [key-diagnostic evidence](2026-10-08-key-diagnostics.md).
