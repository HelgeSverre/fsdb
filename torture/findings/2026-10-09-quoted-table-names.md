# Quoted table identity and associated wire contracts

Status: audited dotted table-name counterexample closed.

## Native evidence

The [native fixture](2026-10-09-quoted-table-names-native.json) records 15 scripts
on disposable MySQL 8.4.11 with a 64 MiB buffer pool and redo capacity. The
[fsdb replay](2026-10-09-quoted-table-names-current.json) matches every script.
Reproduce the native fixture with:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-09-quoted-table-names-native.json
```

Quoted dots remain literal identifier characters; unquoted dots separate the
database and table. Backticks escaped by doubling survive resolution. The
matrix covers qualified and unqualified DDL/DML, LIKE and SELECT table creation,
renames within and between databases, view writes and CHECK OPTION diagnostics,
foreign-key references, SHOW/DESCRIBE, ANSI_QUOTES, and missing-table errors.

The parser retains necessary component quoting in statement target strings.
A shared formatter and splitter preserve it through catalog resolution and
internally generated view-write and rename targets. Ordinary table references
continue to carry database and table separately. CREATE TABLE checks the actual
catalog key rather than lowercasing only the lookup; CHECK OPTION diagnostics
use the normalized view name.

This matrix does not establish general database-name case folding or every
stored-program identifier context.

## Wire metadata and affected rows

The full wire comparison exposed two additional differences that rendered-row
replay does not detect. SHOW INDEX used text metadata for every field when no
rows were returned. CREATE DATABASE returned zero affected rows.

The [native metadata capture](2026-10-09-quoted-table-metadata-native.txt) records
SHOW INDEX types, lengths, flags, and collations, plus CREATE DATABASE counts:

```sh
python3 torture/scripts/show-index-metadata-oracle.py
```

SHOW INDEX now declares its numeric and NULL fields independently of rows.
Its text fields can retain BINARY_FLAG alongside a text collation; the shared
wire encoder respects the explicit collation and encodes text as UTF-8 unless
the producer explicitly supplies raw bytes. Protocol regressions exercise
non-ASCII payloads through both text and binary rows. Source-origin completeness
and every session-charset variant are outside this audit.

CREATE DATABASE reports one affected row on creation and on an existing
IF NOT EXISTS target. The latter also retains native note 1007.

## Validation

- Quoted-target regression fails before the implementation.
- Full native wire comparison exposes the missing SHOW INDEX types and database
  affected counts before their fixes, then the flag/collation discrepancy.
- `just check`: 3,162 tests pass; no build warnings or errors.
- Expanded quoted-name replay: all 15 scripts match.
- Original duplicate-key replay: all 16 scripts match.
- Full native wire suite: 94 contracts, 14,125 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T230516003-85155/contracts`.
No mismatch is enrolled in the known-gap allowlist.
