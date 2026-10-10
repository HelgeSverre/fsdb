# EXP functional keys

Pinned MySQL 8.4.11 accepts functional indexes on `EXP` of `DECIMAL`,
`DOUBLE`, and `VARCHAR` columns. In a four-row probe, zero, one, negative one,
and `NULL` produced double values `1`, `2.718281828459045`,
`0.36787944117144233`, and `NULL`. `EXPLAIN` selected the `EXP(n)` key for
`= 1e0`, `> 1e0`, and `GROUP BY EXP(n) ORDER BY EXP(n)`. A unique `EXP(n)`
key admitted multiple `NULL` inputs. Inserting `1000` into an indexed column
failed with error 1690 rather than storing an infinite key.

The focused Expecto regression covers physical key selection for all three
column types, range and grouping plans, uniqueness, overflow, and update
maintenance. The persistence regression checks key recovery from both WAL
replay and a snapshot.

The `exp-functional-indexes` differential contract in pinned MySQL 8.4.11 run
`20261010T142021033-86806` passed all 11 steps. The complete contracts run
retained the same nine identifier-case differences outside this contract.
