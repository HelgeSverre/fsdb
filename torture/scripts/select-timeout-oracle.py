"""Pin SELECT timeout settings and interruption on native MySQL 8.4.11."""
import pathlib
import runpy
import re
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SET max_execution_time=1;SELECT SLEEP(0.1),1', 'SLEEP(0.1)\t1\n1\t1\n'),
    ('CREATE TABLE timeout_rows(n INT);INSERT INTO timeout_rows VALUES(1),(2);SET max_execution_time=1;SELECT n,SLEEP(0.1) FROM timeout_rows', (3024, 'HY000')),
    ('SET max_execution_time=1;SELECT COUNT(*) FROM timeout_rows a JOIN timeout_rows b ON SLEEP(0.1)=0', (3024, 'HY000')),
    ('SET max_execution_time=18446744073709551615;SELECT /*+ MAX_EXECUTION_TIME(10000) */ @@max_execution_time;SHOW WARNINGS', '@@max_execution_time\n18446744073709551615\n'),
    ("SET max_execution_time=1;PREPARE s FROM 'SELECT n,SLEEP(0.1) FROM timeout_rows';EXECUTE s", (3024, 'HY000')),
    ("SET max_execution_time=0;PREPARE s FROM 'SELECT n,SLEEP(0.1) FROM timeout_rows';SET max_execution_time=1;EXECUTE s", (3024, 'HY000')),
    ('SET max_execution_time=1;UPDATE timeout_rows SET n=n+SLEEP(0.05);SELECT n FROM timeout_rows ORDER BY n', 'n\n1\n2\n'),
    ('SELECT @@session.max_execution_time,@@global.max_execution_time', '@@session.max_execution_time\t@@global.max_execution_time\n0\t0\n'),
    ('SET max_execution_time=1;SELECT @@max_execution_time', '@@max_execution_time\n1\n'),
    ('SET max_execution_time=-1;SHOW WARNINGS;SELECT @@max_execution_time', "Level\tCode\tMessage\nWarning\t1292\tTruncated incorrect max_execution_time value: '-1'\n@@max_execution_time\n0\n"),
    ('SET max_execution_time=1.5', (1232, '42000')),
    ("SET max_execution_time='2'", (1232, '42000')),
    ('SET max_execution_time=NULL', (1232, '42000')),
    ('SET max_execution_time=4294967296;SHOW WARNINGS;SELECT @@max_execution_time', '@@max_execution_time\n4294967296\n'),
    ('SET max_execution_time=1;SELECT SLEEP(0.1) AS slept;SHOW WARNINGS', 'slept\n1\n'),
    ("SET max_execution_time=1;SELECT BENCHMARK(10000000,SHA2('abc',256)) AS n", 'n\n0\n'),
    ("SET max_execution_time=0;SELECT /*+ MAX_EXECUTION_TIME(1) */ BENCHMARK(10000000,SHA2('abc',256)) AS n", 'n\n0\n'),
]

lifetime_cases = [
    ("CREATE TEMPORARY TABLE temp_t(n INT);INSERT INTO temp_t VALUES(1),(2);SET max_execution_time=1;SELECT n,SLEEP(0.1) FROM temp_t", "", [(3024, "HY000")]),
    ("CREATE TABLE write_source(n INT);INSERT INTO write_source VALUES(1),(2);CREATE TABLE write_log(n INT);\nDELIMITER //\nCREATE FUNCTION write_timeout() RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN INSERT INTO write_log VALUES(1); RETURN SLEEP(0.03); END//\nDELIMITER ;\nSET max_execution_time=1;SELECT n,write_timeout() AS slept FROM write_source;SHOW WARNINGS;SET max_execution_time=0;SELECT COUNT(*) FROM write_log", "n\tslept\n1\t0\n2\t0\nLevel\tCode\tMessage\nNote\t3025\tSelect is not a read only statement, disabling timer\nCOUNT(*)\n2\n", []),

    ('CREATE TABLE t(n INT);INSERT INTO t VALUES(1),(2)', '', []),
    ('SET GLOBAL max_execution_time=12;SELECT @@session.max_execution_time,@@global.max_execution_time;SET max_execution_time=DEFAULT;SELECT @@max_execution_time;SET GLOBAL max_execution_time=DEFAULT', '@@session.max_execution_time\t@@global.max_execution_time\n0\t12\n@@max_execution_time\n12\n', []),
    ('CREATE PROCEDURE p() SELECT n,SLEEP(0.03) FROM t;SET max_execution_time=1;CALL p()', 'n\tSLEEP(0.03)\n1\t0\n2\t0\n', []),
    ('CREATE FUNCTION f() RETURNS INT DETERMINISTIC RETURN SLEEP(0.03);SET max_execution_time=1;SELECT n,f() FROM t', '', [(3024, 'HY000')]),
    ('SET max_execution_time=1;SELECT /*+ MAX_EXECUTION_TIME(0) */ n,SLEEP(0.03) FROM t', '', [(3024, 'HY000')]),
    ('SET max_execution_time=1;SELECT /*+ MAX_EXECUTION_TIME(200) */ n,SLEEP(0.03) FROM t', 'n\tSLEEP(0.03)\n1\t0\n2\t0\n', []),
    ('SET max_execution_time=0;SELECT /*+ MAX_EXECUTION_TIME(1) */ n,SLEEP(0.03) FROM t', '', [(3024, 'HY000')]),
    ('SET max_execution_time=1;SELECT SLEEP(0.03) FROM (SELECT 1 AS n) d', 'SLEEP(0.03)\n1\n', []),
    ('SET max_execution_time=1;SELECT SLEEP(0.03) UNION ALL SELECT 1', '', [(3024, 'HY000')]),
    ('SET max_execution_time=1;START TRANSACTION;INSERT INTO t VALUES(3);SAVEPOINT s;SELECT n,SLEEP(0.03) FROM t;SET max_execution_time=0;SELECT COUNT(*) FROM t;ROLLBACK TO s;COMMIT;SELECT COUNT(*) FROM t', 'COUNT(*)\n3\nCOUNT(*)\n3\n', [(3024, 'HY000')]),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)
    for sql, expected, expected_errors in lifetime_cases:
        result = subprocess.run(
            [*client.process.args, "--force", "--comments", "--column-names"],
            input="USE probe;" + sql + ";\n", capture_output=True, text=True, timeout=30,
        )
        errors = [(int(code), state) for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)]
        assert result.returncode == 0 and errors == expected_errors, (sql, result.stderr)
        arguments["oracle"]["expect"](sql, result.stdout, expected)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
