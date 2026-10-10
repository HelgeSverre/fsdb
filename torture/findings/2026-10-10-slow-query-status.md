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
`LOAD DATA` execution paths record elapsed wall time against the
session threshold that was active when the statement began. The wire path
starts the timer before a local upload or server-side file read. Focused tests
cover the zero threshold, self-counting status reads, prepared execution,
session isolation, fractional settings, `FLUSH STATUS`, and load time before
row decoding.

A separate native MySQL 8.4.11 probe set `long_query_time=0.1`, then loaded
one row from a local FIFO whose writer waited 0.4 seconds after the client
opened it. The subsequent session status reported `Slow_queries=1`; the
table contained the row. This confirms that the local upload interval belongs
to the statement's slow-query time.

Uploads rejected before row decoding do not yet update the counter. MySQL's
wider engine and latency status families remain outside fsdb's registry.
