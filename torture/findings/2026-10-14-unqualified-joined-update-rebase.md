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
table. If a source is not a physical table or ownership is ambiguous, this
conservative claim path does not infer a target.
