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

For a `utf8mb4_0900_ai_ci` source, MySQL 8.4.11 also uses a stored
`_utf8mb4'x'` key for `COALESCE(v,_latin1'X')` and
`IFNULL(v,_latin1'X')`: the fallback texts compare equal under the source
collation. A distinct fallback (`_latin1'y'`) or a binary introducer does
not use that key. Fsdb now shares the collation-checked lookup rule with
text `NULLIF` while keeping the function forms distinct. The focused
regression checks the chosen key and returned rows; the
`equivalent-text-fallback-functional-index` contract compares those rows
with native MySQL.

All four steps of that contract matched MySQL 8.4.11 in
`20261012T172417741-78278/contracts`. The full run had 149 cases and
19,088 steps; its two row differences were in the previously documented
REGEXP and mixed-type join cases. The introduced-literal case also reported
an object-cleanup failure on both targets, separate from the new contract.

A numeric comparison must also stay on the scan path. On text values `1`,
`01`, and `1x`, `COALESCE(v,_utf8mb4'x')=1` matches all three through MySQL's
text-to-number comparison; looking up only the string key `1` would silently
lose rows.

Fsdb now preserves the fallback literal expression in the functional-key
identity. Charset-introduced text literals normalize to one spelling across
schema and query parsing, while connection literals remain distinct. Physical
key projection, equality probes, uniqueness, and updates cover a `VARCHAR`
source when the fallback fits its declared width and uses the source charset.
Other text types, longer fallbacks, and broader mixed-charset expressions
remain outside this physical-key subset.

The focused Expecto regression passes, as does the 3,493-test root gate.
All ten `text-null-fallback-functional-index` differential steps match in
`20261012T113543845-12948/contracts`. That full run covers 144 cases and
19,029 steps. It reports 13 differences: the established identifier-case and
hint signatures, plus three extra signatures in the previously documented
[connection-sensitive mixed-type join case](2026-10-07-fulltext-join-bounds.md#connection-sensitive-result-on-2026-10-12).
Two earlier runs of this campaign during the same change reported ten
differences and no text-fallback mismatch.
