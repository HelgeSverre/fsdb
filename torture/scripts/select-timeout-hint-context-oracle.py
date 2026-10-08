"""Pin timeout-hint query contexts against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('EXPLAIN SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1;SHOW WARNINGS', 'id\tselect_type\ttable\tpartitions\ttype\tpossible_keys\tkey\tkey_len\tref\trows\tfiltered\tExtra\n1\tSIMPLE\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNULL\tNo tables used\nLevel\tCode\tMessage\nNote\t1003\t/* select#1 */ select /*+ MAX_EXECUTION_TIME(10000) */ 1 AS `1`\n'),
    ('(SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n);SHOW WARNINGS', 'n\n1\n'),
    ('WITH c AS (SELECT 1 AS n) SELECT /*+ MAX_EXECUTION_TIME(10000) */ n FROM c;SHOW WARNINGS', 'n\n1\n'),
    ('SELECT 1 AS n UNION ALL SELECT /*+ MAX_EXECUTION_TIME(10000) */ 2;SHOW WARNINGS', 'n\n1\n2\nLevel\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n UNION ALL SELECT /*+ MAX_EXECUTION_TIME(2) */ 2;SHOW WARNINGS', 'n\n1\n2\nLevel\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ('CREATE TABLE hint_scope(n INT);INSERT INTO hint_scope VALUES(1),(2)', ''),
    ('INSERT INTO hint_scope SELECT /*+ MAX_EXECUTION_TIME(1) */ 3;SHOW WARNINGS', 'Level\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ('CREATE VIEW hint_scope_v AS SELECT /*+ MAX_EXECUTION_TIME(10000) */ n FROM hint_scope;SHOW WARNINGS;SHOW CREATE VIEW hint_scope_v', 'View\tCreate View\tcharacter_set_client\tcollation_connection\nhint_scope_v\tCREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `hint_scope_v` AS select `hint_scope`.`n` AS `n` from `hint_scope`\tutf8mb4\tutf8mb4_0900_ai_ci\n'),
    ('CREATE PROCEDURE hint_scope_p() SELECT /*+ MAX_EXECUTION_TIME(1) */ n,SLEEP(0.03) FROM hint_scope;SHOW WARNINGS;CALL hint_scope_p();SHOW WARNINGS', 'Level\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\nn\tSLEEP(0.03)\n1\t0\n2\t0\n3\t0\nLevel\tCode\tMessage\nWarning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(4294967296) MAX_EXECUTION_TIME(10000) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tUnsupported MAX_EXECUTION_TIME near ') MAX_EXECUTION_TIME(10000) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(01) */ 1 AS n;SHOW WARNINGS', 'n\n1\nLevel\tCode\tMessage\nWarning\t3126\tHint MAX_EXECUTION_TIME(1) is ignored as conflicting/duplicated\n'),
    ('SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(-1) */ 1 AS n;SHOW WARNINGS', "n\n1\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near '-1) */ 1 AS n' at line 1\n"),
    ('SELECT /*+ BOGUS MAX_EXECUTION_TIME(1) */ n,SLEEP(0.03) FROM hint_scope;SHOW WARNINGS', "n\tSLEEP(0.03)\n1\t0\n2\t0\n3\t0\nLevel\tCode\tMessage\nWarning\t1064\tOptimizer hint syntax error near 'BOGUS MAX_EXECUTION_TIME(1) */ n,SLEEP(0.03) FROM hint_scope' at line 1\n"),
    ('SELECT /*+ MAX_EXECUTION_TIME(1) BOGUS */ n,SLEEP(0.03) FROM hint_scope;SHOW WARNINGS', (3024, 'HY000')),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
