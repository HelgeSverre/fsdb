# Identifier case policy

fsdb reports `lower_case_table_names=2`. Native MySQL 8.4.11 with that
initialization setting is the matching oracle for table-name case behavior.
The digest-pinned Linux torture server uses MySQL's default mode 0, so its
case-specific results are not comparable without accounting for the setting.

Native MySQL with mode 2 lowercases `AbSeNt` in a missing-table 1146 packet
and its `SHOW WARNINGS` row, formats a duplicate on `Odd.Table` as
`odd.table.Odd.Key`, formats a CHECK OPTION failure on `Odd.View` as
`odd.view`, and generates `child_ibfk_1` for table `Child`. It also treats
`BKA(A)` as targeting alias `a`, issuing no unresolved-name warning.
fsdb matches these audited behaviors. The mode 0 Linux wire server instead
retains the mixed-case spelling or treats the differently cased reference as
unresolved. The mode 0 contract run exposed four differences limited to this
policy: table lookup and its warning, generated foreign-key prefix spelling,
and BKA alias resolution.

The full compatibility contract run against native MySQL 8.4.11 in mode 2
passed 102 contracts and 15,567 steps with no differences. The corresponding
mode 0 run covered the same contracts and steps, with those four case-policy
differences. Both runs used the same fsdb checkout. The root gate passed 3,205
tests. fsdb currently cannot select MySQL's initialization modes 0 or 1;
adding those modes would require consistent catalog, privilege, metadata,
constraint-name, and hint-resolution semantics.
