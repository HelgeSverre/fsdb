"""Verify reconstructed expression-label boundaries on native MySQL 8.4.11."""

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
    for query, (label, value) in zip(queries, expected, strict=True):
        result = subprocess.run([*client.process.args, "--column-names", "-e", "USE probe;" + query], capture_output=True, text=True, check=True)
        oracle["expect"](query, result.stdout.rstrip("\n"), label + "\n" + value)
    statement = "USE probe;PREPARE label_check FROM 'SELECT CONCAT(?, ''b'')';SET @label_value='a';EXECUTE label_check USING @label_value"
    result = subprocess.run([*client.process.args, "--column-names", "-e", statement], capture_output=True, text=True, check=True)
    oracle["expect"]("prepared expression label", result.stdout.rstrip("\n"), "CONCAT(?, 'b')\nab")


if __name__ == "__main__":
    oracle["run"](verify)
