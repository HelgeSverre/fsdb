# Current-head polar hole relation and distance audit

The pinned MySQL 8.4.11 geographic edge oracle was rerun on commit
`ee5cbb18`. It reports 12/412 line-relation mismatches, 1/189 near-hole
point-relation mismatch, and 5/189 near-hole point-distance mismatches.
All 100 seeded point-to-line distances and the four near-exterior controls
match. The earlier eight-distance count in the gap document predates later
geographic changes.

The point-relation mismatch is `POINT(88 2.99999)` against the polar
polygon with exterior `(85 0,85 4,89 4,89 0)` and hole
`(86 1,88 1,88 3,86 3)`, with WKT coordinates in latitude-longitude
order for EPSG 4326. MySQL reports outside; fsdb reports interior. Both
engines report the same point as interior when the hole is a standalone
polygon, so the divergence is in the combined polygon's hole winding
decision, not a general failure to locate the point relative to that ring.

A trial evaluated holes using the standalone exterior winding role.
It lowered distance mismatches to four, but increased line-relation
mismatches to 13 and point-relation mismatches to three; it also introduced
four mismatches in the near-polar corner grid. The trial was reverted.
The next correction should target the local Boost hole-side decision at
this polar edge, with the full oracle guarding the surrounding relations.

Run `uv run --with pymysql torture/scripts/geographic-edge-oracle.py`
after building `src/Fsdb.Cli` in Release. The oracle uses disposable
MySQL and fsdb servers and leaves no persistent database.
