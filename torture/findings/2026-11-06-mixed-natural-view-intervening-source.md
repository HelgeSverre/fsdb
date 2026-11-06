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
