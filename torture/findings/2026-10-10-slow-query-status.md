# Slow-query status and threshold

A disposable native MySQL 8.4.11 server reported `long_query_time` as
`10.000000` by default. Session assignments accept fractional seconds and
display six decimal places; negative values clamp to zero, values above
31,536,000 clamp to that maximum, and `NULL` returns error 1232. With a zero
threshold, `Slow_queries` counts completed statements in the issuing session
and in global status. Another session starts with its own zero count. A slow
`SHOW STATUS` statement includes itself in its returned value. `FLUSH STATUS`
resets session counters but leaves the global count accumulated.

Fsdb now tracks that counter per session and globally using the same status
counter lifecycle as `Questions` and `Com_*`. The text, parsed prepared, and
decoded `LOAD DATA` execution paths record elapsed wall time against the
session threshold that was active when the statement began. Focused tests
cover the zero threshold, self-counting status reads, prepared execution,
session isolation, fractional settings, and `FLUSH STATUS`.

The counter does not yet include time spent receiving an upload or reading a
server-side load file before row decoding. MySQL's wider engine and latency
status families remain outside fsdb's registry.
