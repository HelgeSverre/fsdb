# Multi-table DELETE with stored views

Oracle was the digest-pinned MySQL 8.4.11 in `torture/compose.yaml`.
The Fsdb baseline was `38532f0e`.

A physical target can use a joined view as a read source. The restricted
account had DELETE and SELECT on the physical target and only
`SELECT (id)` on the view. MySQL deleted one row with the view on the
right of an explicit equality join and one with the view first in
`DELETE FROM … USING` and an `ON TRUE` join. A leading CTE that selected
from the view also reached the physical target. Revoking the sole view
grant caused error 1142 with SQLSTATE 42000. Naming the view as a target
also caused a DELETE privilege error for that account.

| Named delete target with sufficient privileges | MySQL result |
| --- | --- |
| Inner join view or a single-source layer over it | 1395, HY000 |
| View over a left outer join | 1288, HY000 |
| TEMPTABLE view | 1288, HY000 |
| CTE shadowing a stored join view | 1288, HY000 |
| CTE shadowing a physical table in single-table DELETE | 1288, HY000 |
| Mergeable single-table view with a renamed key | One physical row deleted |
| Nested single-table view plus a physical target | Two physical rows deleted |

The join-view refusal also holds with a false WHERE or an unknown column
in WHERE. The view name in 1395 is the stored name; 1288 uses the supplied
target alias.

Fsdb now assigns the DELETE grant to named targets and checks SELECT for
read sources. It reports the audited target errors before filtering the
joined rows. DELETE also retains CTEs referenced by its first source. A
CTE can serve as a read source, and naming one as a target is rejected
even when a stored table or view has the same name. The regressions pin
those errors, required and missing grants, both supported DELETE
spellings, and preservation of the base rows after a refusal.
Writable single-table view targets in multi-table DELETE still
need physical row mapping. The baseline returned 1146 when their rows
matched. MySQL's precedence when both the target and the JOIN condition
are invalid also needs separate coverage.
