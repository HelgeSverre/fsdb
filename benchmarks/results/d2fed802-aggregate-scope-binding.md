# Aggregate scope binding allocation

Implementation based on `d2fed802`, with enclosing aggregate ownership and
shared grouping validation. The cleanup skips binding for expressions without
subqueries and preserves expression identity when no substitution is needed.

The ordering-alias probe uses 10,000 input rows, LIMIT 100, five warmups,
and nine alternating batches of five verified executions. Embedded in-memory
Debug engine, .NET SDK 10.0.401, Darwin 24.6 arm64, eight logical processors,
and a 4 GiB GC heap cap. No concurrent fsdb builds, tests, or profiles.

Correlated allocation before cleanup was 250,728,024 bytes/query; afterward it
is 241,045,904 bytes/query, a reduction of 9,682,120 bytes (3.9%). Relative to
the previously committed prepared-binding result of 240,883,344 bytes/query,
the ownership checks add 162,560 bytes (0.07%). Ordinary controls add 48 bytes.

Timing is slower in the later run, including both ordinary controls, with
substantial within-run variation. These samples do not establish a speedup or
isolate the latency cost of aggregate binding. This is an fsdb allocation probe,
not a MySQL latency or durable-write comparison.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/order-aliases.fsx
```

## Before cleanup

```text
source input=10000 output=100 median-ms=55.809 median-bytes=60237398 samples-ms=[57.46272; 60.85548; 60.5044; 53.39466; 54.67094; 55.80856; 62.11038; 53.1499;
 53.98306]
alias input=10000 output=100 median-ms=53.364 median-bytes=60237534 samples-ms=[58.84382; 60.8199; 53.36412; 52.66466; 52.846; 57.1553; 66.77436; 52.7837;
 52.231]
correlated-alias input=10000 output=100 median-ms=196.895 median-bytes=250728024 samples-ms=[197.20772; 191.60726; 215.0629; 235.5074; 185.8479; 219.58464; 196.8953;
 185.87314; 184.40194]
```

## After cleanup

```text
source input=10000 output=100 median-ms=63.993 median-bytes=60234766 samples-ms=[63.99342; 60.73938; 63.48994; 58.3435; 60.00104; 71.22454; 69.63216; 112.47146;
 90.05224]
alias input=10000 output=100 median-ms=69.453 median-bytes=60234902 samples-ms=[59.91056; 69.7392; 61.2913; 69.45296; 59.94696; 63.0499; 74.53666; 95.34684;
 86.22306]
correlated-alias input=10000 output=100 median-ms=228.986 median-bytes=241045904 samples-ms=[211.34524; 283.776; 217.7594; 209.54496; 228.9859; 213.48958; 330.24404;
 378.43264; 321.56074]
```
