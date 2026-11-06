# Current-head CJK ngram rerun

The [halfwidth voicing-mark finding](2026-10-17-general-ci-halfwidth-marks.md)
recorded 292, 134, 86, 20, and 18 `utf8mb4_general_ci` search differences
at token sizes 1–5 after its original fix. Those figures describe that
earlier revision, not the current head. Later collation and full-text changes
had not been reflected in that finding's summary.

Against native MySQL 8.4.11, the maintained two-column corpus now has zero
membership or relevance differences in 387 searches at each startup token
size from 1 through 10: 3,870 comparisons. This includes the earlier
zero-width joiner, variation-selector, and nonbreaking-space controls.

The [Nüshu range corpus](2026-10-18-nushu-ngram-audit.md) also has zero
membership or relevance differences under `utf8mb4_0900_ai_ci` at sizes 2
and 3, with 2,376 searches at each size. Its size-1 lane and the other
collations and sizes listed in the earlier reports already matched.

Commands on the current head:

```sh
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 1B170:1B300 --collation utf8mb4_0900_ai_ci 2 3
```

Each command starts disposable native MySQL and fsdb servers and compares
matching row IDs and relevance in natural-language, Boolean, and query
expansion modes. These results establish parity for those corpora and token
sizes. MeCab, unsampled CJK ranges, and other query shapes remain open.
