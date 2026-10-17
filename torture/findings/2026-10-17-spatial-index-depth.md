# Spatial index depth under chosen geometry bounds

The in-memory MBR index was a treap ordered by each geometry's minimum X
coordinate and row ID. Its priority came from a fixed hash of the row ID. A
writer who could predict IDs could assign minimum X coordinates in priority
order, producing a chain regardless of insertion order. A focused regression
constructs 1,024 such rows; the previous index reached depth 1,024.

The index now uses AVL balancing. The same regression checks that construction
and deletion keep depth at most 32, that spatial lookup still finds retained
rows, and that an earlier immutable root remains unchanged. The existing
exhaustive bounds-scan comparison covers `MBRINTERSECTS`, `MBRWITHIN`, and
`MBRCONTAINS` candidate correctness. `just check` passed 3,707 tests.

This is an internal resource-bound fix, not a MySQL spatial semantics claim.
The geographic topology differences recorded elsewhere remain open.
