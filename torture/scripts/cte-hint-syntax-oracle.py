"""Pin syntax warnings from repeated native CTE parsing; fsdb parity remains open."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1;SHOW WARNINGS',
  'n\tn\n'
  '1\t1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1' "
  'at line 1\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n)' at line 1\n")]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
