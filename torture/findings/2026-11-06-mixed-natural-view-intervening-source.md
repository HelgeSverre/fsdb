# Intervening source between two writable natural-joined views

The two-view MySQL 8.4.11 oracle now covers a recursive CTE followed by
`NATURAL LEFT JOIN` or `NATURAL RIGHT JOIN` to the first writable joined
view, an ordinary join to a derived read source, and `NATURAL LEFT JOIN`
to a second writable joined view. Both views expose a common `id` and
`link_id`; the derived source contributes a distinct `bridge` column.

For both directions, fsdb and MySQL report the same affected rows and
base-table values. The independent-holder check locks each physical
target in a separate transaction, verifies that the writer waits for
both, and compares the committed rebased values. The maintained oracle
now passes 63 two-view UPDATE cases and 38 contended rebases.

The follow-up checks a missing second-view row at `id=2`: only the first
physical target changes, and its writer waits and rebases. It also uses a
CTE row that misses the first view under `NATURAL RIGHT JOIN`; the first
view's preserved row updates after the same physical wait. Both cases
match MySQL's affected rows and final base-table values.

Run `uv run --with pymysql torture/scripts/two-joined-view-outer-oracle.py`
after building `src/Fsdb.Cli` in Release. These two cases extend coverage
through one intervening derived source; other join groupings, longer
chains, and unmatched arrangements remain to audit.

## Dependent JSON_TABLE and right-preserved final view

A dependent `JSON_TABLE(JSON_ARRAY(va.link_id), ...)` can now sit between the
two writable views. MySQL and fsdb agree for matched rows, a missing second
view row, and a CTE row missing the first view. A final `NATURAL RIGHT JOIN`
also preserves an unmatched second-view row after the dependent source
eliminates the left row. The equivalent intervening derived source matches.

The last shape exposed a mutation-only binding bug: fsdb's view rewrite knew
the natural key `id` belonged to the right-preserved view, but rewrote bare
`WHERE id=3` through the first view's null-extended base row. Qualified
`WHERE vb.id=3` worked. The rewrite now uses the logical merged-key owner
before mapping a bare exposed name to the first view's physical expression.
The focused regression and oracle check both the preserved update and the
null-extended `c.id` control. The MySQL 8.4.11 oracle passes 69 two-view UPDATE cases and
40 contended rebases, including a waiter on that right-preserved physical row.
