# CJK radical ngrams under the legacy Unicode collation

The maintained [MySQL 8.4.11 differential oracle](../scripts/ngram-multicol-oracle.py)
now has a `--legacy-radicals` lane. It places printable characters from the
CJK Radicals Supplement (U+2E80–U+2EFF) and Kangxi Radicals (U+2F00–U+2FDF),
plus their distinct NFKC equivalents, in one `utf8mb4_unicode_ci` table.
Each document embeds one character between a repeated `中` prefix and `文`;
queries search every corresponding prefix in natural, Boolean, and query
expansion modes. A representative search for Kangxi radical one (`⼀`) must
also find the ordinary Han one (`一`), so an empty matching corpus cannot pass.

The native MySQL and fsdb servers matched membership and relevance for
1,635 searches at each ngram token size from 1 through 10. No mismatch was
found in this corpus. The result extends the audited CJK coverage; it does
not establish exact legacy `WEIGHT_STRING` bytes, every UCA 4.0 comparison,
or MeCab behavior.
