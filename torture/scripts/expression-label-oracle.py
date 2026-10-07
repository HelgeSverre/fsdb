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
    for query, label, value in source_boundaries:
        expect_result(query, label, value)



if __name__ == "__main__":
    oracle["run"](verify)
