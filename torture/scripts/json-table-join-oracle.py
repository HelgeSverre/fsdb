"""Verify JSON_TABLE natural and right joins on disposable native MySQL 8.4.11."""

import pathlib
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT PRIMARY KEY,n INT);INSERT INTO a VALUES(1,0),(2,0)")
    for query, expected in [
        ("SELECT id FROM a NATURAL JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(id INT PATH '$')) j ORDER BY id", "1\n2"),
        ("SELECT id FROM a NATURAL LEFT JOIN JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j ORDER BY id", "1\n2"),
        ("SELECT a.id,j.id FROM a RIGHT JOIN JSON_TABLE('[1,3]','$[*]' COLUMNS(id INT PATH '$')) j ON a.id=j.id ORDER BY j.id", "1\t1\nNULL\t3"),
        ("SELECT id FROM a NATURAL RIGHT JOIN JSON_TABLE('[1,3]','$[*]' COLUMNS(id INT PATH '$')) j ORDER BY id", "1\n3"),
    ]:
        oracle["expect"](query, client.query(query), expected)
    query = "SELECT * FROM a RIGHT JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(id INT PATH '$')) j ON 1"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True, check=False)
    oracle["expect"]("right dependency diagnostic", "ERROR 1109 (42S02)" in result.stderr, True)
    for statement in [
        "UPDATE a NATURAL JOIN JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j SET a.n=7",
        "UPDATE a RIGHT JOIN JSON_TABLE('[2,3]','$[*]' COLUMNS(id INT PATH '$')) j ON a.id=j.id SET a.n=8",
    ]:
        oracle["expect"](statement, client.query(statement + ";SELECT ROW_COUNT()"), "1")
    oracle["expect"]("stored targets", client.query("SELECT * FROM a ORDER BY id"), "1\t7\n2\t8")


if __name__ == "__main__":
    oracle["run"](verify)
