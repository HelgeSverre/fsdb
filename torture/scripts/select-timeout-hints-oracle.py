"""Verify timeout-hint diagnostics against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SELECT /*+ MAX_EXECUTION_TIME(0) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(01) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(-1) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '-1) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(+1) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '+1) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(1.5) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '1.5) */ 1 AS n' at line 1\n"),
    ("SELECT /*+ MAX_EXECUTION_TIME('1') */ 1 AS n;SHOW WARNINGS", "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near ''1') */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(NULL) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near 'NULL) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME() */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near ') */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(1,2) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near ',2) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(4294967295) */ 1 AS n;SHOW WARNINGS', 'n\n1\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(4294967296) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tUnsupported MAX_EXECUTION_TIME near ') */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS', 'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(0) MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS', 'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(1.5) MAX_EXECUTION_TIME(2) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '1.5) MAX_EXECUTION_TIME(2) */ 1 AS n' at line 1\n"),
    ('SELECT (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS n;SHOW WARNINGS', 'n\n1\nLevel\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ('SELECT 1 /*+ MAX_EXECUTION_TIME(1) */ AS n;SHOW WARNINGS', 'n\n1\n'),
    ('CREATE TABLE hint_t(n INT);INSERT INTO hint_t VALUES(1),(2)', ''),
    ('UPDATE /*+ MAX_EXECUTION_TIME(1) */ hint_t SET n=n;SHOW WARNINGS', 'Level\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ("PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n';SHOW WARNINGS;EXECUTE s;SHOW WARNINGS", 'Level\tCode\tMessage\nWarning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\nn\n1\n'),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
