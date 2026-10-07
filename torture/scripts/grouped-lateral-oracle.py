"""Verify grouped dependent sources on disposable native MySQL 8.4.11."""

import pathlib
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT);CREATE TABLE b(id INT);"
                 "INSERT INTO a VALUES(1),(2);INSERT INTO b VALUES(1),(3)")
    for source, first_row in [
        ("b JOIN LATERAL (SELECT b.id+1 AS v) d ON 1", "1\t1\t2"),
        ("b JOIN LATERAL (SELECT a.id+b.id AS v) d ON 1", "1\t1\t2"),
        ("b LEFT JOIN LATERAL (SELECT a.id+b.id AS v WHERE 0) d ON 1", "1\t1\tNULL"),
        ("b JOIN JSON_TABLE(JSON_ARRAY(b.id),'$[*]' COLUMNS(v INT PATH '$')) d ON 1", "1\t1\t1"),
        ("b JOIN JSON_TABLE(JSON_ARRAY(a.id+b.id),'$[*]' COLUMNS(v INT PATH '$')) d ON 1", "1\t1\t2"),
    ]:
        query = "SELECT a.id,b.id,d.v FROM a LEFT JOIN (" + source + ") ON a.id=b.id ORDER BY a.id"
        oracle["expect"](source, client.query(query), first_row + "\n2\tNULL\tNULL")
    for source in [
        "LATERAL (SELECT a.id AS v) d JOIN b ON d.v=b.id",
        "JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) d JOIN b ON d.v=b.id",
    ]:
        query = "SELECT a.id,b.id,d.v FROM a LEFT JOIN (" + source + ") ON 1 ORDER BY a.id"
        oracle["expect"](source, client.query(query), "1\t1\t1\n2\tNULL\tNULL")
    for body, condition, first_value in [
        ("SELECT a.id AS v", "0", "NULL"),
        ("SELECT a.id AS v WHERE 0", "0", "NULL"),
        ("SELECT a.id AS v", "d.v=1", "1"),
        ("SELECT a.id AS v", "NULL", "NULL"),
    ]:
        query = "SELECT a.id,d.v FROM a LEFT JOIN LATERAL (" + body + ") d ON " + condition + " ORDER BY a.id"
        oracle["expect"](query, client.query(query), "1\t" + first_value + "\n2\tNULL")
    query = "SELECT a.id FROM a LEFT JOIN (b JOIN LATERAL (SELECT a.id+b.id AS v) d ON d.v=a.id) ON a.id=b.id"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True, check=False)
    oracle["expect"]("ordinary ON scope", "ERROR 1054 (42S22)" in result.stderr, True)


if __name__ == "__main__":
    oracle["run"](verify)
