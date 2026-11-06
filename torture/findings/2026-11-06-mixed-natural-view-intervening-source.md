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

A later `NATURAL LEFT JOIN` to a read-only derived source also keeps the
right-preserved writable row. The oracle checks both a matching `id=3` read
row and a missing read row; `WHERE id=3` still binds to the preserved view's
merged key and updates its physical row. The maintained value corpus now
passes 71 two-view UPDATE cases against MySQL 8.4.11.

A final `NATURAL RIGHT JOIN` to a read-only derived source also matches
MySQL for both the preserved second-view row at `id=3` and a tail-only
row at `id=1`. The latter has no writable view row after the intervening
dependent source. The maintained corpus now passes 73 two-view UPDATE
cases and 40 contended rebases against MySQL 8.4.11.

## Four writable views across a derived read source

The maintained oracle now joins a CTE to `va`, a derived read
source, `vb_unique`, `vc_unique`, and `ve_unique`. The four views write to
independent physical tables. MySQL and fsdb agree on a fully matched row,
a row missing the second and fourth views, and a final `NATURAL RIGHT JOIN`
that preserves a fourth-view row after earlier sources miss. Row counts
and all four base tables are compared after every UPDATE.

The fully matched form also waits and rebases when any one of its four
physical targets is held, and when four independent transactions hold the
targets simultaneously. The expanded native MySQL 8.4.11 oracle passes
**79 UPDATE cases and 59 contended rebases**. This extends verified view
rewrite and write scheduling to four targets; other mixed join shapes and
unmatched arrangements remain open.

## Mixed outer-join matrix and dependent source

The four-view oracle now checks all eight `NATURAL LEFT`/`NATURAL RIGHT`
combinations across the last three writable views at each of three keys:
a fully matched row, a row missing the second and fourth views, and a row
missing the first view. It also replaces the derived read source with a
dependent `JSON_TABLE(JSON_ARRAY(va.link_id), ...)` in matched, missing,
and final-right-preserved cases.

The dependent matched form waits and rebases for each of four individual
physical holders and for four independent holders together. The full
native MySQL 8.4.11 corpus now matches **106 UPDATE cases and 64 contended
rebases**, with final base-table contents checked after each operation.
Other source types, nested join groupings, and wider unmatched data remain
open.
