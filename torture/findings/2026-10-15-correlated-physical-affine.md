# Correlated affine equality over indexed physical sources

The inverse-key probe for `d.k + literal = o.x` and related signed-integer
forms now reaches physical indexes and pass-through derived or CTE sources.
The predicate still runs as a final filter. Complete OR branches combine the
affine lookup with other usable branches without scanning the physical table.
The inversion helper is shared with the stable materialized-source path.

An inverse outside the signed column's declared range is an empty candidate
set. Treating it as a failed index lookup previously caused a full scan for
an outer bound just above the `INT` maximum. The column-type bounds cover
signed `TINYINT`, `SMALLINT`, `MEDIUMINT`, and `INT`; other types keep their
existing path.

The [MySQL 8.4.11 oracle](../scripts/correlated-affine-oracle.py) checks direct,
pass-through derived, pass-through CTE, materialized derived, and stable CTE
sources against NULL and signed INT boundary values. The focused residual
regression fails on the previous main after 8,032 evaluations for the first
physical query and passes with the indexed probe. It also checks a complete
OR and confirms the earlier materialized regression still passes.
