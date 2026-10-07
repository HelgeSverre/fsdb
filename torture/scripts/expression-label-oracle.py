"""Verify expression-label source boundaries on native MySQL 8.4.11."""

import pathlib
import runpy
import subprocess
oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
queries = [
    "SELECT CONCAT('a', 'b')",
    "SELECT COALESCE(NULL, 'a')",
    "SELECT 'a' = 'b'",
    "SELECT 'a'",
    "SELECT CONCAT('it''s', 'x')",
    "SELECT CONCAT('a\\\\b', 'x')",
    "SELECT a.id + 1 FROM a",
    "SELECT a.id FROM a"
]


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT);INSERT INTO a VALUES(1)")
    expected = [
        ("CONCAT('a', 'b')", "ab"),
        ("COALESCE(NULL, 'a')", "a"),
        ("'a' = 'b'", "0"),
        ("a", "a"),
        ("CONCAT('it''s', 'x')", "it'sx"),
        (queries[5].removeprefix("SELECT "), "a" + chr(92) + "bx"),
        ("a.id + 1", "2"),
        ("id", "1"),
    ]
    def expect_result(query, label, value):
        result = subprocess.run(
            [*client.process.args, "--comments", "--column-names", "-e", "USE probe;" + query],
            capture_output=True, text=True, check=True,
        )
        oracle["expect"](query, result.stdout.rstrip("\n"), label + "\n" + value)

    for argument, code in [("NULL", 1382), ("-1", 1210), ("-0", 1210), ("1+1", 1210),
                           ("CONCAT('a','b')", 1210), ("'a' COLLATE utf8mb4_bin", 1210)]:
        query = "SELECT NAME_CONST(" + argument + ",2)"
        result = subprocess.run([*client.process.args, "-e", query], capture_output=True, text=True)
        assert result.returncode != 0, query
        assert f"ERROR {code} (HY000)" in result.stderr, (query, result.stderr)

    for query, (label, value) in zip(queries, expected, strict=True):
        expect_result(query, label, value)
    statement = "PREPARE label_check FROM 'SELECT CONCAT(?, ''b'')';SET @label_value='a';EXECUTE label_check USING @label_value"
    expect_result(statement, "CONCAT(?, 'b')", "ab")

    source_boundaries = [
        ("SELECT concat('a','b')", "concat('a','b')", "ab"),
        ("SELECT  1  +  2   ", "1  +  2", "3"),
        ("SELECT (1 + 2)", "(1 + 2)", "3"),
        ("SELECT 1 /* middle */ + 2 /* tail */", "1 /* middle */ + 2", "3"),
        ("SELECT /* leading */ 1 + 2", "1 + 2", "3"),
        ("SELECT ((1))", "1", "1"),
        ("SELECT 'a' 'b'", "a", "ab"),
        ("SELECT CAST(1 AS CHAR)", "CAST(1 AS CHAR)", "1"),
        ("SELECT CASE WHEN 1 THEN 'a' ELSE 'b' END", "CASE WHEN 1 THEN 'a' ELSE 'b' END", "a"),
        ("SET sql_mode='PIPES_AS_CONCAT'; SELECT 'a'||'b'", "'a'||'b'", "ab"),
        ('SET sql_mode=\'ANSI_QUOTES\'; SELECT "x" + 1 FROM (SELECT 1 AS x) a', '"x" + 1', "2"),
        ("SET sql_mode='HIGH_NOT_PRECEDENCE'; SELECT not 1 = 0", "not 1 = 0", "1"),
        ("SET sql_mode=''; SELECT 1 /*!80000 + 2 */", "1  + 2", "3"),
    ]
    source_boundaries.extend([
        ("SELECT NAME_CONST(1,2),_latin1'é',_binary X'41'", "1\tÃ©\t_binary X'41'", "2\tÃ©\tA"),
        ("SELECT 001,(001),1.00,+1,-1,-(1),((-1)),true,false,null",
         "001\t001\t1.00\t1\t-1\t-(1)\t((-1))\ttrue\tfalse\tNULL",
         "1\t1\t1.00\t1\t-1\t-1\t-1\t1\t0\tNULL"),
        ("SELECT 0x41,X'41',b'01',_binary'a',_utf8mb4'a',N'a'",
         "0x41\tX'41'\tb'01'\ta\ta\ta", "A\tA\t\x01\ta\ta\ta"),
        ("SELECT ' a ', 'a' COLLATE utf8mb4_bin, _utf8mb4'a' 'b', N'a' 'b'",
         "a \t'a' COLLATE utf8mb4_bin\ta\ta", " a \ta\tab\tab"),
        ('SET sql_mode=\'ANSI_QUOTES\';SELECT "x`y"+1, 1+2 FROM (SELECT 1 AS `x``y`) a',
         '"x`y"+1\t1+2', "2\t3"),
        ('SET sql_mode=\'ANSI_QUOTES\';SELECT "x""y"+1, 1+2 FROM (SELECT 1 AS `x"y`) a',
         '"x""y"+1\t1+2', "2\t3"),
        ("SELECT SUM(id)+row_number() OVER () FROM (SELECT 1 AS id) a", "SUM(id)+row_number() OVER ()", "2"),
        ("SELECT concat('a','b') AS '',concat('a','b')", "\tconcat('a','b')", "ab\tab"),
        ("SELECT * FROM (SELECT concat('a','b')) d", "concat('a','b')", "ab"),
        ("SET @counter=0;SELECT @counter := @counter + 1", "@counter := @counter + 1", "1"),
        ('SET @`HAS, COMMA`=3,@"DOUBLE-NAME"=1;SELECT @\'sp ace\' := @`HAS, COMMA` + @"DOUBLE-NAME"',
         '@\'sp ace\' := @`HAS, COMMA` + @"DOUBLE-NAME"', "4"),
    ])
    for argument, label in [
        ("001", "1"), ("1.00", "1.00"), ("1e0", "1"), ("0x41", "A"), ("X'41'", "A"),
        ("b'01'", ""), ("true", "1"), ("false", "0"), ("18446744073709551615", "18446744073709551615"),
        ("' a '", "a "), ("''", ""), ("'a' 'b'", "ab"),
    ]:
        expect_result("SELECT NAME_CONST(" + argument + ",2)", label, "2")
    for expression, label, value in [
        ("_binary X'41'", "_binary X'41'", "A"),
        ("(_binary X'41')", "(_binary X'41')", "A"),
        ("+_binary X'41'", "+_binary X'41'", "A"),
        ("_binary 0x41", "_binary 0x41", "A"),
        ("_binary b'01'", "_binary b'01'", "\x01"),
        ("_binary 0b01", "_binary 0b01", "\x01"),
        ("0b01", "0b01", "\x01"),
        ("_BINARY  X'41' /* tail */", "_BINARY  X'41'", "A"),
        ("(_binary  X'41')", "(_binary  X'41')", "A"),
        ("_binary /* middle */ X'41'", "_binary /* middle */ X'41'", "A"),
    ]:
        expect_result("SELECT " + expression, label, value)
    for hexadecimal in ["0061", "0161", "0961", "1f61", "2061", "7f61", "c2a061"]:
        label = "\u00a0a" if hexadecimal == "c2a061" else "a"
        expect_result("SELECT NAME_CONST(X'" + hexadecimal + "',2)", label, "2")
    for value, label in [("\x01a", "a"), ("\x7fa", "a"), ("\u00a0a", "\u00a0a")]:
        expect_result("SELECT '" + value + "'", label, value)
    expect_result("SELECT NAME_CONST(1,2)+1,NAME_CONST(1,2) AS chosen",
                  "NAME_CONST(1,2)+1\tchosen", "3\t2")
    client.query("CREATE VIEW labels AS SELECT concat('a','b'),1+2")
    expect_result("SELECT * FROM labels", "concat('a','b')\t1+2", "ab\t3")
    expect_result("SELECT `concat('a','b')`,`1+2` FROM labels", "concat('a','b')\t1+2", "ab\t3")
    client.query("CREATE TABLE w(id INT,v INT);INSERT INTO w VALUES(1,10),(2,20)")
    client.query("CREATE TABLE ft(title TEXT,body TEXT,FULLTEXT(title,body));INSERT INTO ft VALUES('Other','Other')")
    expect_result("SELECT LEAD(v) OVER (ORDER BY id) FROM w", "LEAD(v) OVER (ORDER BY id)", "20\nNULL")
    expect_result("SELECT LAG(v) OVER (ORDER BY id) FROM w", "LAG(v) OVER (ORDER BY id)", "NULL\n10")
    expect_result("SELECT MATCH (title,body) AGAINST ('Tutorial') FROM ft", "MATCH (title,body) AGAINST ('Tutorial')", "0")
    for query, label, value in source_boundaries:
        expect_result(query, label, value)



if __name__ == "__main__":
    oracle["run"](verify)
