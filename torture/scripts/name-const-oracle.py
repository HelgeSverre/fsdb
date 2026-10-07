"""Verify NAME_CONST argument syntax on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
cases = [
    ('SELECT NULL COLLATE utf8mb4_bin,missing', (1253, '42000')),
    ('SELECT NULL COLLATE utf8mb4_bin + missing', (1253, '42000')),
    ('SELECT IF(0,(SELECT NULL COLLATE utf8mb4_bin),1)', (1253, '42000')),
    ('SELECT NULL COLLATE utf8mb4_bin WHERE 0', (1253, '42000')),
    ('SELECT IF(0,NAME_CONST(NULL,2),1)', (1382, 'HY000')),
    ('SELECT missing,NULL COLLATE utf8mb4_bin', (1054, '42S22')),
    ('SELECT NULL COLLATE utf8mb4_bin FROM absent', (1146, '42S02')),
    ('SELECT NAME_CONST(NULL,2) FROM absent', (1146, '42S02')),
    ('SELECT NAME_CONST(1+1,2) FROM absent', (1210, 'HY000')),
    ('SELECT NULL COLLATE UTF8MB4_BIN', (1253, '42000')),
    ('SELECT 2 COLLATE utf8mb4_bin COLLATE latin1_bin', (1253, '42000')),
    ('CREATE FUNCTION c0() RETURNS INT DETERMINISTIC RETURN NULL COLLATE utf8mb4_bin', ''),
    ('SELECT c0()', (1253, '42000')),
    ('CREATE FUNCTION c1() RETURNS INT DETERMINISTIC RETURN NAME_CONST(NULL,2)', ''),
    ('SELECT c1()', (1382, 'HY000')),
    ('CREATE FUNCTION c2() RETURNS INT DETERMINISTIC RETURN NAME_CONST(1,NULL COLLATE utf8mb4_bin)', ''),
    ('SELECT c2()', (1253, '42000')),
    ('CREATE FUNCTION c3() RETURNS INT DETERMINISTIC RETURN NAME_CONST(NULL,NULL COLLATE utf8mb4_bin)', ''),
    ('SELECT c3()', (1253, '42000')),
    ('CREATE FUNCTION c4() RETURNS INT DETERMINISTIC RETURN NAME_CONST(1+1,NULL COLLATE utf8mb4_bin)', (1210, 'HY000')),
    ('CREATE PROCEDURE collated_proc() SELECT NULL COLLATE utf8mb4_bin', ''),
    ('CALL collated_proc()', (1253, '42000')),
    ('CREATE TABLE collated_trigger_target(v INT)', ''),
    ('CREATE TRIGGER collated_trigger BEFORE INSERT ON collated_trigger_target FOR EACH ROW SET NEW.v=NULL COLLATE utf8mb4_bin', ''),
    ('INSERT INTO collated_trigger_target VALUES(1)', (1253, '42000')),
    ('SELECT COUNT(*) AS n FROM collated_trigger_target', 'n\n0\n'),
    ("SELECT BINARY 'x'", "BINARY 'x'\nx\n"),
    ("SELECT BINARY 'a' = 'A'", "BINARY 'a' = 'A'\n0\n"),
    ("SELECT CAST('x' AS CHAR CHARACTER SET binary)", "CAST('x' AS CHAR CHARACTER SET binary)\nx\n"),
    ("SELECT 'a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci", "'a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci\na\n"),
    ("SELECT 'a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci = 'A'", "'a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci = 'A'\n1\n"),
    ("SELECT COLLATION('a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci)", "COLLATION('a' COLLATE utf8mb4_bin COLLATE utf8mb4_general_ci)\nutf8mb4_general_ci\n"),
    ("SELECT NAME_CONST(1,'x' COLLATE 'utf8mb4_bin')", '1\nx\n'),
    ("SELECT NAME_CONST(1,('x' COLLATE utf8mb4_bin) COLLATE utf8mb4_bin)", (1210, 'HY000')),
    ('SELECT NULL COLLATE utf8mb4_bin', (1253, '42000')),
    ('SELECT 2 COLLATE utf8mb4_bin', '2 COLLATE utf8mb4_bin\n2\n'),
    ('SELECT NULL COLLATE binary', (1064, '42000')),
    ("SELECT _binary'x' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT NAME_CONST(1,NULL COLLATE 'binary')", '1\nNULL\n'),
    ("PREPARE s FROM 'SET @x=NAME_CONST(1+1,2)'", (1210, 'HY000')),
    ('SET @x=NAME_CONST(NULL,2)', (1382, 'HY000')),
    ("SET sql_mode=NAME_CONST(1+1,'')", (1210, 'HY000')),
    ("SELECT X'41' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT b'01' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT NULL COLLATE 'binary'", "NULL COLLATE 'binary'\nNULL\n"),
    ('SELECT NAME_CONST(NULL,NULL COLLATE utf8mb4_bin)', (1253, '42000')),
    ('SELECT NAME_CONST(1+1,NULL COLLATE utf8mb4_bin)', (1210, 'HY000')),
    ('SELECT NAME_CONST(NULL COLLATE utf8mb4_bin,2)', (1210, 'HY000')),
    ('SELECT IF(0,NULL COLLATE utf8mb4_bin,1)', (1253, '42000')),
    ("SELECT (NULL COLLATE 'binary') COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT 'a' COLLATE 'binary' COLLATE utf8mb4_bin", (1253, '42000')),
    ('SELECT NAME_CONST(1,NULL COLLATE utf8mb4_bin COLLATE utf8mb4_bin)', (1210, 'HY000')),

    ("SELECT NAME_CONST(1,('x') COLLATE utf8mb4_bin)", "1\nx\n"),
    ("SELECT NAME_CONST(1,+(('x') COLLATE utf8mb4_bin))", "1\nx\n"),
    ("SELECT NAME_CONST(NULL,2", (1064, "42000")),
    ("SELECT NAME_CONST(1", (1064, "42000")),
    ("SET @x=NAME_CONST(1+1,2)", (1210, "HY000")),
    ('SELECT NAME_CONST(+1,2)', '1\n2\n'),
    ('SELECT NAME_CONST(--  x\n1,2)', '1\n2\n'),
    ('SELECT NAME_CONST((-0),2)', (1210, 'HY000')),
    ('SELECT NAME_CONST(-(-1),2)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,-2)', '1\n-2\n'),
    ('SELECT NAME_CONST(1,-0)', '1\n0\n'),
    ('SELECT NAME_CONST(1,1+1)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,ABS(2))', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,NULL)', '1\nNULL\n'),
    ('SELECT NAME_CONST(1,(2))', '1\n2\n'),
    ('SELECT NAME_CONST(1,+2)', '1\n2\n'),
    ("SELECT NAME_CONST(1,'x' COLLATE utf8mb4_bin)", '1\nx\n'),
    ('SELECT NAME_CONST(1,CAST(2 AS SIGNED))', (1210, 'HY000')),
    ('SELECT NAME_CONST(CAST(1 AS SIGNED),2)', (1210, 'HY000')),
    ("SELECT NAME_CONST(1,NAME_CONST('a',2))", (1210, 'HY000')),
    ("SELECT NAME_CONST('a',@x)", (1210, 'HY000')),
    ('SELECT NAME_CONST(@x,2)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,(SELECT 2))', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,TRUE)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,1e0)', '1\n1\n'),
    ("SELECT NAME_CONST(_binary X'41',2)", 'A\n2\n'),
    ("SELECT NAME_CONST(1,_binary X'41')", '1\nA\n'),
    ('SELECT NAME_CONST(1,-1e0)', '1\n-1\n'),
    ("SELECT NAME_CONST(1,-X'41')", '1\n-65\n'),
    ('SELECT NAME_CONST(1,~1)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,NOT 1)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,-(-1))', (1210, 'HY000')),
    ('SELECT NAME_CONST(NULL,1+1)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1+1,NULL)', (1210, 'HY000')),
    ("SELECT NAME_CONST('x',a) FROM (SELECT 2 a) t WHERE 0", (1210, 'HY000')),
    ('SELECT IF(0,NAME_CONST(1+1,2),3)', (1210, 'HY000')),
    ("PREPARE s FROM 'SELECT NAME_CONST(?,2)'", (1210, 'HY000')),
    ("PREPARE s FROM 'SELECT NAME_CONST(1,?)'", (1210, 'HY000')),
    ("SELECT NAME_CONST('a',2), NAME_CONST('a',2)", 'a\ta\n2\t2\n'),
    ('SELECT NAME_CONST(1)', (1582, '42000')),
    ('SELECT NAME_CONST(TRUE,2)', '1\n2\n'),
    ('SELECT NAME_CONST(1,FALSE)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,-TRUE)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,+TRUE)', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,-NULL)', '1\nNULL\n'),
    ("SELECT NAME_CONST(1,-'x')", '1\n-0\n'),
    ('SELECT NAME_CONST(1,-(-2))', (1210, 'HY000')),
    ('SELECT NAME_CONST(1,-(+2))', '1\n-2\n'),
    ("SELECT NAME_CONST(1,-('x' COLLATE utf8mb4_bin))", (1210, 'HY000')),
    ("SELECT NAME_CONST(1,(-'x') COLLATE utf8mb4_bin)", (1210, 'HY000')),
    ("SELECT NAME_CONST(1,'x' COLLATE utf8mb4_bin COLLATE utf8mb4_bin)", (1210, 'HY000')),
    ('SELECT NAME_CONST(1,NULL COLLATE utf8mb4_bin)', (1253, '42000')),
    ('SELECT NAME_CONST(1,2 COLLATE utf8mb4_bin)', '1\n2\n'),
    ("SELECT NAME_CONST(1,DATE '2020-01-01')", (1210, 'HY000')),
    ("SELECT NAME_CONST(DATE '2020-01-01',2)", '2020-01-01\n2\n'),
    ("SELECT NAME_CONST(1,'a' 'b')", '1\nab\n'),
    ('CREATE PROCEDURE bad_name() SELECT NAME_CONST(1+1,2)', (1210, 'HY000')),
    ('CREATE FUNCTION bad_name_f() RETURNS INT DETERMINISTIC RETURN NAME_CONST(1+1,2)', (1210, 'HY000')),
    ('CREATE VIEW bad_name_v AS SELECT NAME_CONST(1+1,2)', (1210, 'HY000')),
    ('SELECT NAME_CONST(NULL,TRUE)', (1210, 'HY000')),
    ('SELECT NAME_CONST(NULL,NULL)', (1382, 'HY000')),
]


def verify_cases(client, cases):
    for sql, expected in cases:
        result = subprocess.run(
            [*client.process.args, "--comments", "--column-names", "-e", "USE probe;" + sql],
            capture_output=True, text=True,
        )
        if isinstance(expected, tuple):
            error = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
            actual = (int(error[1]), error[2]) if error else None
            assert result.returncode != 0 and actual == expected, (sql, expected, result.stdout, result.stderr)
        else:
            assert result.returncode == 0, (sql, result.stderr)
            oracle["expect"](sql, result.stdout, expected)


def verify(client, _writer):
    verify_cases(client, cases)


if __name__ == "__main__":
    oracle["run"](verify)
