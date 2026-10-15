# Exact unique-key write locks

FSDB previously mapped every unique-key claim to one of 4,096 per-table lock
stripes using `StringComparer.Ordinal.GetHashCode`. Two different encoded keys
could therefore block one another for a whole transaction. The collision was
especially visible when a long transaction held one insert while an unrelated
autocommit insert happened to hash to the same stripe.

The regression constructs two distinct strings with the same old stripe index,
holds the first claim in one transaction, and requests the second with a zero
wait timeout. On `v0.1.0-preview.10` this raises `LockWaitTimeout`; with exact
key locks it succeeds. A second assertion confirms that the first key still
blocks another transaction. Existing
same-key wait, deadlock, savepoint, and prepared-XA tests continue to pass.

The cache holds weak references to lock objects. Active owners and waiters hold
strong references, and every 8,192 new entries trigger a sweep of dead weak
references. This keeps keys from accumulating for the life of a long-running
database. The change does not alter the 4,096 row-identity stripes.

The full suite passed 3,681 tests. In one local Release-mode synthetic run of
20,000 acquire/release pairs, distinct keys took 40.0 ms with stripes and
45.0 ms with exact locks; repeated use of one key took 21.0 and 24.1 ms. This
small isolated measurement is not a durable write-throughput result. The
broader worker-scaling gap remains open.
