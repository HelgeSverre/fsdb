# Statement-access atomic fast-path trial

The [monitor timing](2026-11-06-statement-access-monitor-trial.md) found
substantial cumulative wait to enter one store-wide `TableLocks` monitor,
with no statement-level table-availability waits in the wide-account phase.
This trial let ordinary statements run without that monitor while no
explicit table lock or global read lock was requested. An atomic active
statement count made an explicit/global lock request wait for those
statements to drain before using the existing lock manager. The original
path remained in use while such a request or lock was active.

The candidate built with zero warnings. All 95 filtered lock tests passed.
The separate-process workload verified every account balance, version, and
ledger; all three runs committed 412 operations per database in each phase.
The unchanged baseline came from a detached `48b5d156` worktree and was
run before and after the candidate with eight databases, sixteen writers
per database, thirty operations per writer, seed 204, and hot-account
phases 8/64/8.

| Run | 8-account wall | 64-account wall | 8-account wall | 64-account server CPU |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 9.380 s | 10.323 s | 9.746 s | 35.34 s |
| Candidate | 8.988 s | 10.709 s | 9.310 s | 40.30 s |
| Baseline replay | 8.835 s | 10.581 s | 9.653 s | 38.48 s |

The candidate's wall times overlap the unchanged-code range and its
wide-account phase is slower than both baselines. The trial therefore does
not establish a throughput gain. The shared atomic active count may have
replaced some monitor contention with cache-line contention, but this run
did not measure that cause. The candidate was reverted and its task-owned
baseline worktree removed. A per-database coordination design remains a
separate experiment; it must preserve explicit multi-table and global read
lock semantics.

Run `uv run --with pymysql benchmarks/scripts/external-multidb-write.py
--hot-accounts 8 64 8` after building the Release CLI to repeat one run.
