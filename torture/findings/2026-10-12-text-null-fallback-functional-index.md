# Text NULL-fallback functional keys

The digest-pinned MySQL 8.4.11 oracle accepts `COALESCE(v,'x')` and
`IFNULL(v,'x')` as functional keys for a `VARCHAR(20) COLLATE utf8mb4_bin`
source. A NULL row and a row containing `x` share the key; a unique expression
key rejects the second row with 1062/23000. Both expressions have a non-null
`VARCHAR(20)` hidden result and an 82-byte key for this source.

The literal's resolved charset is part of the expression identity. MySQL's
`SHOW CREATE TABLE` renders the stored fallback as `_utf8mb4'x'`. A query using
`COALESCE(v,_utf8mb4'x')` or `IFNULL(v,_utf8mb4'x')` uses the corresponding key,
while the same query with an ordinary connection literal `'x'` scans on the
audited table. Both queries return the same rows. This distinction is about
the physical plan, not scalar equality.

A numeric comparison must also stay on the scan path. On text values `1`,
`01`, and `1x`, `COALESCE(v,_utf8mb4'x')=1` matches all three through MySQL's
text-to-number comparison; looking up only the string key `1` would silently
lose rows.

Fsdb now preserves the fallback literal expression in the functional-key
identity. Charset-introduced text literals normalize to one spelling across
schema and query parsing, while connection literals remain distinct. Physical
key projection, equality probes, uniqueness, and updates cover a `VARCHAR`
source when the fallback fits its declared width and uses the source charset.
Other text types, longer fallbacks, mixed-charset expressions, and text
`NULLIF` remain outside this physical-key subset.

The focused Expecto regression passes, as does the 3,493-test root gate.
All ten `text-null-fallback-functional-index` differential steps match in
`20261012T113543845-12948/contracts`. That full run covers 144 cases and
19,029 steps. It reports 13 differences: the established identifier-case and
hint signatures, plus three extra signatures in the previously documented
[connection-sensitive mixed-type join case](2026-10-07-fulltext-join-bounds.md#connection-sensitive-result-on-2026-10-12).
Two earlier runs of this campaign during the same change reported ten
differences and no text-fallback mismatch.
