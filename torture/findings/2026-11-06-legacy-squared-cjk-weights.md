# Legacy squared CJK weights, U+3300–U+33FF

The pinned MySQL 8.4.11 oracle found exact `WEIGHT_STRING` byte differences
for all 256 characters in U+3300–U+33FF under `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, and `utf8mb3_unicode_ci` before this change.

Range-limited NFKC expansion through the existing legacy primary-weight
path matched 239 characters per collation. The remaining 17 have explicit
version-specific sequences, including squared units with Greek or special
symbols. The weight path now expands the 239 regular forms and emits the
17 audited exceptions directly. Text comparison and full-text matching use
their existing collation paths.

After rebuilding the Release CLI, the oracle found **0/3,072 exact-byte
mismatches**: 256 singleton, 256 adjacent-pair, 256 ideograph-pair, and
256 `AS CHAR(4)` checks in each of three collations. The focused
`legacy WEIGHT_STRING follows MySQL primary weights` regression test passed
representative regular and exceptional forms.

This closes exact primary-weight bytes for the audited U+3200–U+33FF block
under these three collations. Other CJK blocks, mixed-script compositions,
and broader full-text query shapes remain open.
