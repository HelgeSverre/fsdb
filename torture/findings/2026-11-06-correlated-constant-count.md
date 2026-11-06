# Correlated counts of non-NULL constants

The joined correlated count path precomputes one grouped count per exact numeric
outer key. It previously recognized only `COUNT(*)`, even though a non-NULL
constant counts the same joined rows. A 200-extra-outer-row control now keeps
`COUNT(1)` below 400 examined rows rather than rescanning the joined source
for every outer row. The same guarded path accepts `COUNT(0)` and an ordinary
string literal. `COUNT(NULL)` remains on ordinary aggregate evaluation and
returns zero.

Pinned MySQL 8.4.11 returned these counts for three outer ids with two, one,
and zero matching inner rows:

| Outer id | `COUNT(*)` | `COUNT(1)` | `COUNT(0)` | `COUNT('x')` | `COUNT(NULL)` |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 2 | 2 | 2 | 2 | 0 |
| 2 | 1 | 1 | 1 | 1 | 0 |
| 3 | 0 | 0 | 0 | 0 | 0 |

The optimization applies only to unmodified built-in `COUNT` with a literal
that is known non-NULL. Expression arguments and charset introducers retain
ordinary evaluation, since moving or skipping their evaluation can affect
warnings and errors. This is a planner improvement for one common spelling;
general correlated aggregate planning remains open.
