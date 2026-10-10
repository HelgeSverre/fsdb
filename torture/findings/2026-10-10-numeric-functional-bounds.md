# Constant bounds for numeric functional keys

MySQL 8.4.11 reports `ref` access through functional keys for `FLOOR(v)=FLOOR(4.5)`, `ROUND(v)=ROUND(4.4)`, `SQRT(v)=SQRT(4)`, `SIN(v)=SIN(4)`, and `LOG(v)=LOG(4)` on a DECIMAL source. Fsdb previously used the key only when the bound was a literal; a pure function call on the right made it scan.

The planner now recognizes constant numeric expressions for every supported numeric-result functional key. The same classification covers exact-integer functions, rounded/exact numeric functions, and double-result functions. The existing probe normalizer retains each key's result type, while the built-in registry check prevents an overridden function from claiming a stored projection. Equality, `IN`, and mutation predicates use this path.

The `numeric-functional-bounds` differential contract checks values and an update against MySQL; focused Expecto regressions assert physical access, membership, and override safety. Constant-expression warning multiplicity for malformed text arguments remains open as documented in [the exact-integer bound finding](2026-10-10-exact-integer-functional-bounds.md).
