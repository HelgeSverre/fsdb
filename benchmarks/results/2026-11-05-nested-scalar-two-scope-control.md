# Nested scalar two-scope control

The existing `CorrelatedNestedScalarNoSourceFiltered` benchmark references only the immediate order row inside its source-free scalar subquery. The new `CorrelatedNestedScalarNoSourceFilteredTwoScopes` method also references the ancestor user row in the scalar projection. The existing executor correctness test covers the same binding shape. Both benchmark queries count orders for 100 users on the 10,000-user/50,000-order fixture.

On the current code, the [BenchmarkDotNet ShortRun](2026-11-05-nested-scalar-two-scope-control-raw.md) measured 6.48 ms for the one-scope query and 6.46 ms for the two-scope query on fsdb; MySQL measured 0.317 and 0.289 ms, respectively. Three measured iterations are too few to interpret the small difference between the two query shapes. Both show a roughly 20× fsdb/MySQL gap, and the second method supplies a direct control for future changes to nested outer-scope binding.
