# Per-database statement-admission trial

The [shared-monitor timing](2026-11-06-statement-access-monitor-trial.md)
found substantial cumulative entry wait in `TableLocks` during the
separate-process eight-database write replay. This trial gave ordinary
single-database statements a manager per database. An atomic active count
per manager let explicit table locks, global read locks, and cross-database
statements switch to the original shared manager after active fast statements
drained. The candidate did not change the row-lock or commit paths.

The candidate built without warnings, and all 97 filtered lock tests passed.
The external 8/64/8-account workload used eight databases, sixteen writers
per database, thirty operations per writer, and seed 204. Each run verified
account balances, versions, and ledgers and committed 412 operations per
database in every phase. The unchanged baseline was built in a detached
worktree and run before and after the candidate.

| Run | 8-account wall | 64-account wall | 8-account wall | 64-account server CPU |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 16.121 s | 17.504 s | 16.144 s | 35.99 s |
| Candidate | 17.463 s | 25.380 s | 19.911 s | 45.48 s |
| Baseline replay | 20.656 s | 21.145 s | 21.441 s | 32.75 s |

An earlier current-head baseline immediately before the trial measured
15.333/17.260/15.696 seconds, so machine load drifted noticeably. The
candidate's 64-account phase was nevertheless slower and used more server
CPU than either matched baseline. This implementation provides no measured
throughput benefit and was reverted; the full suite was not run against the
discarded candidate. The experiment does not isolate the added per-database
active counter from the lock-manager bookkeeping as the cause. A further
scheduler design should profile those costs before another rewrite.

Reproduce one workload run after building `src/Fsdb.Cli` in Release:

```sh
uv run --with pymysql benchmarks/scripts/external-multidb-write.py --hot-accounts 8 64 8
```
