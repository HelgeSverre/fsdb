"""Pin ordered optimizer-hint parsing against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('SELECT /*+ BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"),
 ('SELECT /*+ BOGUS MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS MAX_EXECUTION_TIME(10000) */ 1 AS n' at line 1\n"),
 ('SELECT /*+ BOGUS() MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS() MAX_EXECUTION_TIME(10000) */ 1 AS n' at line "
  '1\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000), MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near ', MAX_EXECUTION_TIME(2) */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000)MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA(t) MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BKA() MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ BKA MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'MAX_EXECUTION_TIME(10000) */ 1 AS n' at line 1\n"),
 ('SELECT /*+ QB_NAME(q) MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ QB_NAME() MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near ') MAX_EXECUTION_TIME(10000) */ 1 AS n' at line 1\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=3) MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ BOGUS SET_VAR(max_points_in_geometry=3) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS SET_VAR(max_points_in_geometry=3) */ 1 AS n' at "
  'line 1\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=3) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=3) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ max_execution_time(10000) bogus */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'bogus */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) 123 */ 1 AS n;SHOW WARNINGS',
  "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '123 */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) /* x */ */ 1 AS n;SHOW WARNINGS', (1064, '42000')),
 ('SELECT /*+ JOIN_FIXED_ORDER() MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
 ('SELECT /*+ NO_INDEX(t) MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS',
  'n\n1\nLevel\tCode\tMessage\nWarning\t3128\tUnresolved name `t`@`select#1` for NO_INDEX hint\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(1.5) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near '1.5) BOGUS */ 1 AS n' at line 1\n"),
 ('SELECT /*+ MAX_EXECUTION_TIME(4294967296) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tUnsupported MAX_EXECUTION_TIME near ') BOGUS */ 1 AS n' at line 1\n"
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"),
 ('SELECT /*+ BOGUS SET_VAR(max_points_in_geometry=3) */ @@max_points_in_geometry AS n;SHOW WARNINGS',
  'n\n'
  '65536\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS SET_VAR(max_points_in_geometry=3) */ "
  "@@max_points_in_geometry AS n' at line 1\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=3) BOGUS */ @@max_points_in_geometry AS n;SHOW WARNINGS',
  'n\n'
  '3\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ @@max_points_in_geometry AS n' at line 1\n")]


cases += [('SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=2) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"),
 ('SELECT /*+ BKA(t) BOGUS */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near 'BOGUS */ 1 AS n' at line 1\n"
  'Warning\t3128\tUnresolved name `t`@`select#1` for BKA hint\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=2) MAX_EXECUTION_TIME(3) */ 1 AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(3) is ignored as conflicting/duplicated\n'
  "Warning\t1292\tTruncated incorrect max_points_in_geometry value: '2'\n"),
 ('SELECT /*+ SET_VAR(max_points_in_geometry=3) MAX_EXECUTION_TIME(-1) SET_VAR(no_such_variable=1) */ 1 AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  "Warning\t1064\tOptimizer hint syntax error near '-1) SET_VAR(no_such_variable=1) */ 1 AS n' at line 1\n")]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
