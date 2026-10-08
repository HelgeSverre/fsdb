# SELECT maximum execution time

Status: open. Native MySQL 8.4.11 is the oracle. The maintained
`select-timeout-oracle.py` uses a disposable native server with 64 MiB buffer
pool and redo capacity.

At `dcd30a06`, fsdb rejects reads and assignments of `max_execution_time`
with 1193/HY000. A two-row SELECT containing SLEEP completes normally;
there is no session SELECT deadline.

## Native contract

- Session and global values initially read as zero, disabling the timeout.
- Nonnegative integer assignments are accepted, including UINT64_MAX.
  Negative integers clamp to zero with warning 1292. Decimal, string, and
  NULL assignments fail with 1232/42000.
- With a one-millisecond setting, a two-row table SELECT containing
  SLEEP(0.1) fails with 3024/HY000: `Query execution was interrupted,
  maximum statement execution time exceeded`. A joined COUNT with SLEEP
  in its ON condition has the same error.
- SQL PREPARE does not freeze the setting: changing it before EXECUTE
  affects execution.
- UPDATE containing SLEEP remains successful under the same setting.
- Source-free SLEEP returns 1 on interruption, including when another
  constant is projected. BENCHMARK returns 0. A MAX_EXECUTION_TIME(1)
  optimizer hint interrupts the maintained BENCHMARK case with that same
  result; this result alone does not establish its timing.

The fixture asserts results and errors, not exact elapsed times. Expensive
function inputs make the table-backed cancellation cases deterministic without
requiring a tight wall-clock performance threshold.

## Implementation constraints

The existing query cancellation token serves socket disconnect and KILL.
Its exception path can abort a transaction. A SELECT deadline must retain its
own reason and statement scope rather than reuse that error path blindly.
Existing BENCHMARK work ceilings are separate resource limits and must not
be presented as MySQL's 3024 timeout.

Before closing this gap, cover broader hint contexts and grammar interactions,
native timer overflow, and broader scalar and routine timeout boundaries. Preserve the special scalar interruption
results without allowing a table-backed query to return partial success.

## Settings and lifetime coverage

Session/global reads, assignments, unsigned range, clamp warnings, DEFAULT,
and new-session inheritance are implemented through the shared bounded-integer
setting rules. The settings regression passes with the full 3,086-test suite.
Wire validation passes 64 cases and 8,365 steps with zero differences:
`torture/artifacts/runs/20261008T080034689-59636/contracts`.
The settings-only checkpoint precedes execution deadline support described below.

The expanded native fixture verifies transaction and savepoint survival after
3024, procedure-body SELECT exemption, and timeout of an outer table-backed
SELECT calling a stored function. A positive hint overrides the session value;
a zero hint falls back to it. A source-free UNION still raises 3024, while a
constant derived source containing SLEEP returns its interrupted scalar value.
The audited session-deadline distinctions are covered by the implementation
and regressions below; the audited hint precedence is covered as described below.

UINT64_MAX assignment is accepted, but a repeat native run interrupted its
readback with 3024. Settings-only readbacks use a 10,000 ms hint to separate
unsigned assignment validation from native timer overflow behavior. The latter
needs dedicated controls before choosing how to schedule very large deadlines.

## Session deadline execution

Session deadlines use a monotonic clock and a distinct interruption condition.
Engine row pipelines and stored-program loops share deadline and cancellation
checks. Deadline errors are converted to 3024 without taking the disconnect or
KILL transaction-abort path. SLEEP measures actual elapsed time instead of
assuming sub-millisecond waits consumed their requested duration.

Native controls and regressions cover physical and temporary table reads,
scalar SLEEP/BENCHMARK, UNION, transaction/savepoint preservation, procedure-body
exemption, and timer disabling when a stored function writes data. The latter
emits note 3025. Binary prepared execution observes the current session setting.

Validation: all 3,088 tests pass. The wire suite passes 65 cases and 8,381 steps
with zero differences against MySQL 8.4.11, including prepared timeouts and
transaction preservation:
`torture/artifacts/runs/20261008T081631631-99012/contracts`.

Remaining: hint contexts and grammar beyond the audited cases; native overflow
behavior for extreme unsigned durations; broader scalar-only and stored-routine
combinations. Deadline checks are cooperative, so operations
without an internal polling point are observed at the next engine check.

## Timeout hint diagnostics

`select-timeout-hints-oracle.py` pins native warning behavior. Signed, decimal,
quoted, NULL, empty, and multiple-argument spellings warn with 1064 and retain
successful query execution. Syntax diagnostics identify the offending source
suffix and line. The first accepted hint wins; later duplicates warn with 3126,
including when the first value is zero. Nested SELECT and UPDATE hints warn
with 3125, while misplaced hint comments are ignored. Prepared statements emit
these warnings at preparation and do not repeat them on EXECUTE.

The native fixture accepts 4,294,967,295 and warns for 4,294,967,296. An exploratory
UINT64_MAX hint interrupted its own scalar readback; that unstable overflow case
is excluded from the deterministic diagnostic fixture and remains open.

The existing comment scanner exposes hint body offsets, owning and statement
keywords, and parenthesis depth. Its body-only interface retains existing
behavior. The scanner regression covers nesting, quoted parentheses, misplaced
comments, and statement batches. All 3,089 tests pass, and the existing wire
suite remains at 65 cases / 8,381 steps / zero differences:
`torture/artifacts/runs/20261008T082458417-8827/contracts`.
The scanner checkpoint precedes hint interpretation and diagnostics described below.

## Audited hint execution and preparation

Positive top-level SELECT timeout hints override the session setting; zero falls
back to the session value. Malformed and duplicate hints produce the native
warnings pinned above. Nested SELECT and UPDATE hints are ignored with 3125;
misplaced hint comments remain inert. Accepted hints are reapplied on SQL and
binary prepared execution, while their diagnostics are emitted at preparation.

The implementation uses the shared comment scanner's source locations and scope
metadata. No second SQL comment scanner is introduced. The original geometry
SET_VAR interpretation remains on its existing path.

Validation: 3,090 tests pass. The full wire suite passes 66 cases and 8,428 steps
with zero differences, including exact malformed/duplicate warning text,
binary preparation warning lifetime, hinted prepared timeouts, positive
precedence, and zero fallback:
`torture/artifacts/runs/20261008T083317596-15973/contracts`.
Broader hint scope and grammar combinations and extreme-duration behavior remain
open; this does not establish complete optimizer-hint compatibility.

## Leading SELECT ownership

`select-timeout-hint-context-oracle.py` pins query-block ownership. Parenthesized
standalone SELECTs and the outer SELECT following a CTE accept the hint. Later
UNION members warn with 3125 even when the first member already has a timeout;
they do not warn as duplicate statement hints. EXPLAIN SELECT accepts its hint.
The scanner tracks the leading SELECT at the statement's nesting level, and
timeout interpretation uses that ownership rather than absolute parenthesis
depth. Existing nested-query rejection remains covered.

All 3,091 tests pass. Wire validation covers the parenthesized, CTE, and UNION
cases: 66 cases / 8,436 steps / zero differences, at
`torture/artifacts/runs/20261008T084405153-28484/contracts`.
The EXPLAIN regression checks removal of the incorrect 3125 warning; it does not
claim parity for MySQL's rewritten-query note 1003.

The context fixture also pins routine declaration/loading warnings, now implemented
below. An unknown hint before MAX_EXECUTION_TIME stops hint parsing, whereas a valid
timeout before that unknown token still applies. These grammar/lifetime cases
remain open along with extreme-duration behavior.

## View definitions

CREATE VIEW, ALTER VIEW, and CREATE OR REPLACE VIEW silently discard valid
MAX_EXECUTION_TIME hints, including duplicates. Malformed syntax still emits
1064. The parsed statement identifies view definitions without duplicating the
DDL grammar in a text matcher.

The extended native context fixture passes on MySQL 8.4.11. All 3,092 tests pass;
wire validation passes 66 cases / 8,445 steps with zero differences:
`torture/artifacts/runs/20261008T085734595-33719/contracts`.

An additional native routine probe shows that a second CALL on the same
connection does not repeat the timeout-hint warning. Routine diagnostic lifetime
and mixed-hint grammar remain open.

## Stored-routine diagnostic lifetime

Status: audited routine-loading lifetime implemented; see final validation below.

`select-timeout-routine-hints-oracle.py` pins seven scripts on native MySQL
8.4.11 with a disposable 64 MiB buffer pool and redo capacity. Scripts use
separate connections against one schema and compare exact output, including
warning absence. The fixture rejects stderr even when the client uses `--force`.

- CREATE PROCEDURE and CREATE FUNCTION emit 3125 for timeout hints in their
  bodies. The first invocation on a connection emits the warning again;
  subsequent invocations do not.
- An unreachable IF branch still contributes a warning at declaration and
  first invocation. Diagnostics belong to routine loading, not statement
  execution within the selected branch.
- A new connection gets its own first-invocation warning.
- ALTER PROCEDURE restores the first-invocation warning. Creating or dropping
  a different procedure also restores it. Creating an unrelated table does not.
- Dropping and recreating the hinted procedure produces declaration and
  first-invocation warnings for the replacement body.
- Dynamic PREPARE inside a procedure emits its duplicate-hint warning each
  time PREPARE runs; the enclosing routine declaration does not warn for the
  hint text inside that string literal.

The unchanged fsdb runtime reproduces the mismatch: declaration emits 3125,
while both the first and second CALL leave SHOW WARNINGS empty. A correction
must preserve connection-local lifetime and routine-DDL invalidation; emitting
warnings for every nested SELECT or remembering each routine forever would
contradict the native fixture. Stored-function parsing already has a global
parsed-definition cache, whose lifetime cannot substitute for connection-local
diagnostics.

Validation: the maintained native fixture passes in full. No runtime code or
known-gap allowlist changes accompany this evidence.

## Routine lifetime implementation in progress

The connection-local implementation passes the first-load regression and all
3,093 root tests. It tracks immutable procedure/function catalog row roots,
invalidates loaded identities on catalog publication, and distinguishes stored
routine hint scope from standalone/prepared execution. Unreachable branches,
repeat calls, new connections, and CREATE/DROP invalidation pass wire comparison.

The expanded wire contract remains failing and is not allowlisted:
`torture/artifacts/runs/20261008T092239668-44055/contracts` reports 67 cases,
8,479 steps, and five differences:

- ALTER PROCEDURE is rejected with 1064, so the following CALL also lacks the
  renewed loading warning.
- The first dynamic-procedure call returns an empty inner SHOW WARNINGS result
  instead of the PREPARE warning. The second call happens to see the warning
  retained from the previous CALL.
- After both dynamic calls, fsdb exposes the preparation warning to the caller,
  whereas native MySQL clears it after the subsequent EXECUTE.

These results require ALTER routine support and correction of stored-program
current-diagnostic visibility/lifetime. The runtime patch remains uncommitted
until the expanded contract passes; the native fixture remains authoritative.

## Current diagnostic area inside procedures

`routine-diagnostics-oracle.py` pins native clearing and preservation behavior.
A later SELECT, user-variable SET, or local-variable SET clears previous routine
warnings. SHOW WARNINGS preserves them and reads the current routine diagnostic
area. A warning from the final statement remains visible after CALL. These
cases invalidate the former call-wide accumulation of every generated warning.

The implementation now publishes the final current diagnostic area, and inner
SHOW WARNINGS receives that area rather than stale connection diagnostics. The
same diagnostic-preservation predicate is shared with top-level execution. An
older regression expecting SIGNAL's warning to survive a subsequent local SET
is corrected from the native result.

All 3,094 tests pass. The expanded wire suite now reports only the unsupported
ALTER PROCEDURE and its missing invalidation warning: 67 cases / 8,479 steps /
two differences, at
`torture/artifacts/runs/20261008T093332098-46339/contracts`.
Both dynamic PREPARE calls and their inner/outer diagnostics match MySQL.
The runtime patch remains uncommitted pending ALTER support.

`alter-routine-oracle.py` pins the next implementation requirements: procedure
and function comments, security and data-access characteristics, SHOW CREATE
rendering, last duplicate COMMENT winning, rejection of determinism changes,
1305/42000 for missing procedures, acceptance of an empty ALTER, and retention
of the original SQL mode. Both new maintained native fixtures pass on 8.4.11.

## Audited routine lifecycle and ALTER support

The routine-loading and current-diagnostic cases above now pass. Connections
remember loaded hinted routines until procedure/function catalog rows change.
This covers first invocation, repeat invocation, new connections, and CREATE,
DROP, and ALTER invalidation. Dynamic PREPARE uses standalone hint scope and
its own diagnostics; subsequent execution clears those warnings.

ALTER PROCEDURE and ALTER FUNCTION use one parsed characteristic record and
one catalog update path. Comments, SQL SECURITY, data-access characteristics,
and alteration timestamps persist; routine bodies, determinism, and creation
SQL mode remain unchanged. Empty alterations are accepted, duplicate comments
use the last value, and unsupported determinism changes are rejected. ALTER
ROUTINE privileges and ordinary DDL implicit-commit handling apply.

SHOW CREATE renders characteristic lines in native order. Routine status and
information_schema expose altered metadata. Native ENUM descriptors for
SQL_DATA_ACCESS/SECURITY_TYPE and the SQL_MODE SET descriptor are retained,
including MySQL's obsolete SET positions. Wire rendering checks use an explicit
identical definer because the two test servers bootstrap different root hosts.

Validation: all 3,095 root tests pass, including WAL and snapshot recovery of
altered characteristics. The full native wire suite passes 68 cases / 8,496
steps with zero differences:
`torture/artifacts/runs/20261008T095356620-47527/contracts`.
The maintained ALTER and current-diagnostic native fixtures also pass.

Remaining timeout work includes mixed/unknown-hint grammar, extreme unsigned
durations, and broader scalar/routine execution combinations. The historical
failing checkpoints above are resolved for their enrolled cases, not evidence
that all optimizer-hint behavior is implemented.

## Mixed optimizer-hint parsing

Status: ordered syntax and audited SELECT target resolution implemented; broader hint contexts and optimizer effects remain open.

`mixed-optimizer-hints-oracle.py` passes against native MySQL 8.4.11. Unknown
names, bare numbers, and commas between hints produce syntax warning 1064 and
stop processing later hints in that comment. Previously accepted hints remain
active. Adjacent hints without separating whitespace are accepted. Overflowing
MAX_EXECUTION_TIME produces its unsupported-value warning but permits a later
syntax warning; malformed numeric syntax stops the remainder instead.

This affects SET_VAR as well as timeouts. Before the ordered parser, fsdb
`BOGUS SET_VAR(max_points_in_geometry=3)` incorrectly changed the visible limit
to 3 without a warning; native MySQL keeps 65,536 and warns. A valid SET_VAR
before BOGUS stays active on native MySQL, with the unknown-token warning.
Comma-separated timeout hints produced fsdb's duplicate warning,
and an unknown token after a valid timeout produced no warning.

Valid interleaved BKA(), QB_NAME(q), JOIN_FIXED_ORDER(), and SET_VAR controls
prevent treating every non-timeout name as a syntax error. BKA(t) and NO_INDEX(t)
with no matching table warn with 3128. Missing BKA parentheses and an empty
QB_NAME have distinct syntax-warning offsets. These cases require shared
ordered hint parsing before the timeout and geometry interpreters apply values.

An exploratory RESOURCE_GROUP case reported platform-unsupported warning 3658
on the native macOS server. It is excluded from the portable parsing fixture
because that warning depends on the server platform.

The shared parser returns accepted hints and syntax diagnostics separately.
Timeout and geometry interpreters consume that accepted prefix, so unknown
syntax cannot silently enable a later SET_VAR. Syntax diagnostics precede
execution diagnostics, including duplicate timeout and geometry-clamp warnings.
The executor no longer scans or validates hint argument syntax independently.

The SELECT target-resolution implementation below covers the maintained
BKA(t) and NO_INDEX(t) unresolved-name cases. Physical optimizer effects and
broader statement contexts remain incomplete.

Validation: all 3,096 root tests pass. The existing native wire suite passes
68 cases / 8,496 steps with zero differences:
`torture/artifacts/runs/20261008T101635784-59333/contracts`.
The focused regression covers prefix retention, rejected suffixes, warning
order, and accepted interleaved hint syntax. No known-gap allowlist was changed.


## Table and query-block hint resolution

Status: audited SELECT name resolution implemented; broader contexts and optimizer effects remain open.

`table-hint-resolution-oracle.py` pins native MySQL 8.4.11 diagnostics for
physical tables, aliases, derived sources, named query blocks, nested queries,
CTEs, UNION branches, indexes, duplicate hints, views, and SQL PREPARE. Run it
with `python3 torture/scripts/table-hint-resolution-oracle.py`. The fixture
creates its own empty table in the disposable oracle database.

The observed contract includes:

- Table targets resolve against the exposed alias. `BKA(t)` is unresolved for
  `FROM t AS a`; `BKA(a)` and `BKA(A)` resolve on this native macOS server.
  A derived-table alias is also a valid target.
- Missing table targets warn with 3128 and a table/query-block label. Missing
  explicit query blocks warn with 3127. Named blocks resolve across nested
  queries, and matching is case-insensitive in the audited cases; warning text
  retains the declared name's spelling.
- A CTE's SELECT appears first in the SQL but the outer query is `select#1` and
  the CTE is `select#2`. UNION branches have distinct query-block numbers.
  Query-block depth alone cannot identify the owning SELECT.
- Diagnostic order differs from lexical order: in the audited outer SELECT
  with a scalar subquery and a derived table, unresolved-name warnings appear
  for the outer query, derived table, then scalar subquery. Their numbers remain
  1, 3, and 2 respectively.
- Duplicate or opposing BKA hints warn with 3126 before unresolved-name
  warnings. Duplication is tracked per target, so a repeated target does not
  discard another target in the same hint. Repeated missing explicit block
  names within one hint produce only one missing-block warning.
- Index hints preserve individual index targets. An absent index warns with
  its index name; an absent table with an index target produces both table and
  index warnings. Parsed index hints retain their index names independently
  from table hints.
- SQL PREPARE emits unresolved-name warnings during preparation, without
  repeating them on EXECUTE. CREATE VIEW discards the valid hints in the
  audited case. A missing physical table still returns 1146/42S02.

The expanded mixed-hint fixture also pins syntax warnings before duplicate and
SET_VAR diagnostics, and those diagnostics before unresolved-table warnings.
Valid hints before a syntax error remain effective; hints after it are ignored.

Validation: the maintained table-resolution fixture passes all 36 native cases,
and the expanded mixed-hint native fixture passes. A replay against fsdb
`e7685266` found 28 differing cases; these are principally absent diagnostics,
not proof that optimizer effects match. Passing alias and view controls guard
against a resolver that indiscriminately warns about every table hint. The
native processes use disposable 64 MiB buffer pools and redo capacity. No
runtime changes or known-gap allowlist additions accompany this evidence.


### Audited SELECT target resolution

Hints retain their owning SELECT's source position and their own token position.
Optional parser capture associates those positions with parsed query blocks;
ordinary parses do not collect query-block records. Version-comment expansion
and SQL-mode rewriting preserve the associations. Query-block numbering follows
parse order, including nested WITH bodies, while unresolved-name diagnostics
visit source queries before scalar subqueries.

The resolver uses exposed aliases and catalog index names. Block-wide hints use
an explicit absent table target; missing named blocks use an error value rather
than a sentinel. Target deduplication retains native warning spelling, including
index lists and empty BKA arguments. Rejected second QB_NAME declarations do
not register an alias; an explicit name matching the current block resolves
through the registered query-block names. Timeout and query-block context diagnostics
merge in contextualization order with SET_VAR assignment diagnostics, before
value validation and unresolved-name diagnostics. See the
[SET_VAR audit](2026-10-08-hint-families.md#set_var-contextualization-and-execution)
for nested assignment precedence and preparation lifetimes.

SQL PREPARE and binary-protocol preparation emit target-resolution warnings;
execution does not repeat them. Valid view-definition hints remain discarded.
The mixed-hint wire fixture also exposed a stray comment terminator being
interpreted as arithmetic; the parser now rejects that audited malformed input
with 1064/42000.

Validation: all 3,098 root tests pass. All 52 maintained target-resolution native
cases pass, and the full wire suite passes 70 cases / 8,665 steps with zero
differences, including all maintained mixed-hint cases and both preparation
protocols:
`torture/artifacts/runs/20261008T105730829-83074/contracts`.
No known-gap allowlist was changed.

The [hint-family audit](2026-10-08-hint-families.md) extends this coverage to
family conflicts and nested contextualization order. Broader hint combinations,
DML/stored-program resolution, view-expansion interactions, and physical
optimizer effects remain open. The common-path performance comparison is in
`benchmarks/results/d014676b-hint-resolution.md`; concurrent host activity makes
its elapsed timings unsuitable for a throughput claim.
