# Greek-script regular-expression properties

MySQL 8.4.11 accepts `\p{Greek}`, `\p{Grek}`, `\p{IsGreek}`,
`\p{sc=Grek}`, and `\p{Script=Greek}`. Fsdb previously returned 3685 for
these names. Mapping them to .NET's Greek Unicode block would be incorrect:
MySQL excludes Coptic characters in that block, includes Greek Extended and
supplementary Greek symbols, and matches complete Unicode scalars inside
bracket expressions.

The native [`regexp-greek-script-oracle.py`](../scripts/regexp-greek-script-oracle.py)
enumerates every Unicode scalar through MySQL's ICU `REGEXP_REPLACE` in
case-sensitive and case-insensitive modes. It excludes surrogate code units
and hashes the matching code points as little-endian 32-bit integers. The
case-sensitive set has 518 scalars in 36 ranges. Case-insensitive matching
adds only U+00B5 MICRO SIGN and U+0345 COMBINING GREEK YPOGEGRAMMENI, for
520 scalars. The maintained
[`regexp-greek-fsdb-scan.fsx`](../scripts/regexp-greek-fsdb-scan.fsx)
checks the same full-set counts and hashes against Fsdb's compiled regex.

`Regexp` now translates the audited aliases to a scalar-aware atom built
from the MySQL-derived BMP ranges and two supplementary surrogate groups.
The selected match mode adds the two case-folded characters only when
case-insensitive matching applies. Positive and negated properties, brackets,
and result positions use the existing scalar-aware normalization path.

The focused regression failed with 3685 before the change and covers Greek,
Greek Extended, supplementary symbols, Coptic exclusion, aliases, brackets,
negation, and case folding. Both new differential steps pass on the pinned
MySQL 8.4.11 image. The contract run at
`20261012T060347941-21478/contracts` has 140 cases and 18,964 steps;
the same ten previously documented differences remain. Other ICU scripts
and Script_Extensions aliases remain outside this implementation.
