# Unqualified joined UPDATE claims its unique column owner

MySQL 8.4.11 resolves an unqualified `SET` column against all joined sources.
With `a(id,n)` and `c(id,flag)`, a transaction updating `a.n` made a concurrent
`UPDATE a JOIN c ON c.id=a.id SET n=n+1 WHERE a.id=1` wait; both commits
succeeded and `a.n` increased twice. Likewise, when the first transaction
updated `c.flag`, the joined statement `SET flag=flag+1` waited and committed,
leaving `c.flag` increased twice.

FSDB already resolved these unique column owners during execution, but its
transaction write-target discovery previously skipped any joined UPDATE with
an unqualified assignment. The waiting statement returned one affected row,
then failed its commit with error 1205. Discovery now checks the physical
source schemas and claims the sole table containing each bare assignment
column. An Expecto regression covers ownership by both the leading and joined
table. If ownership is ambiguous, the conservative claim path does not infer
a target.

## LATERAL and JSON_TABLE read sources

Native MySQL 8.4.11 also waits and commits when the joined read source is
`LATERAL (SELECT 1 x)` or `JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$'))`
and the bare assignment `SET n=n+1` uniquely belongs to the physical target.
In both disposable-server transaction probes, the second statement remained
blocked until the first commit, affected one row, and left `n=2` after both
commits.

FSDB previously affected one row but failed the second commit with 1205.
Target discovery aborted when it could not describe the read-only source.
It now obtains projected columns from a lateral source and declared columns
from `JSON_TABLE`. Those names rule out
ambiguous bare assignments; a uniquely owned physical column can then claim
its row before the snapshot is rebased. A focused concurrent regression pins
the wait, both commit results, and the final row for each source kind.
