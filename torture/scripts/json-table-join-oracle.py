"""Verify JSON_TABLE natural and right joins on disposable native MySQL 8.4.11."""

import pathlib
import re
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

    client.query("CREATE TABLE b(id INT);CREATE TABLE t(id INT,j JSON);INSERT INTO t VALUES(1,'[1]')")
    for query, expected in [
        ("SELECT jt.x FROM t,JSON_TABLE(nope.j,'$[*]' COLUMNS(x INT PATH '$')) jt", (1054, "42S22", "Unknown column 'nope.j' in 'a table function argument'")),
        ("SELECT jt.x FROM t,JSON_TABLE(COALESCE(nope.j,t.j),'$[*]' COLUMNS(x INT PATH '$')) jt", (1054, "42S22", "Unknown column 'nope.j' in 'a table function argument'")),
        ("SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(a.missing),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", (1054, "42S22", "Unknown column 'a.missing' in 'a table function argument'")),
        ("SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(b.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1 JOIN b ON 1", (1054, "42S22", "Unknown column 'b.id' in 'a table function argument'")),
        ("SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(x.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", (1054, "42S22", "Unknown column 'x.id' in 'a table function argument'")),
        ("SELECT * FROM a RIGHT JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", (1109, "42S02", "Unknown table 'a' in a table function argument")),
        ("SELECT * FROM JSON_TABLE(JSON_ARRAY(b.id),'$[*]' COLUMNS(v INT PATH '$')) j JOIN b ON 1", (1109, "42S02", "Unknown table 'b' in a table function argument")),
        ("SELECT * FROM a JOIN b ON 1 JOIN JSON_TABLE(JSON_ARRAY(id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", (1052, "23000", "Column 'id' in a table function argument is ambiguous")),
        ("SELECT * FROM a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) j ON 1", None),
    ]:
        for statement in [query, "PREPARE json_source FROM '" + query.replace("'", "''") + "'"]:
            result = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
            error = re.search(r"ERROR (\d+) \((\w+)\).*?: (.*)", result.stderr)
            actual = (int(error[1]), error[2], error[3]) if error else None
            if expected is None:
                oracle["expect"]("valid preceding reference", result.returncode, 0)
            oracle["expect"](statement, actual, expected)


if __name__ == "__main__":
    oracle["run"](verify)
