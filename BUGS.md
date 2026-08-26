# Application compatibility bugs

These failures are reproduced by the pinned external smoke targets. Declared
feature boundaries that have not caused an application failure remain in
`GAPS.md`.

## Functional indexes are unavailable

Rails creates a unique index over `LOWER(external_id)`. `IndexDef` currently
stores column names rather than expressions, so the declaration is rejected
before the mysql2 adapter suite starts.

Reproduce with `just smoke-apps rails`.
