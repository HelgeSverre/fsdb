"""Verify LATERAL mutation sources on disposable native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT PRIMARY KEY,n INT);INSERT INTO a VALUES(1,0),(2,0),(3,0)")
    for statement, affected in [
        ("UPDATE a JOIN LATERAL (SELECT 1 AS id) d ON a.id=d.id SET a.n=7", "1"),
        ("UPDATE a JOIN LATERAL (SELECT a.id AS id) d ON a.id=d.id SET a.n=8", "3"),
        ("UPDATE a LEFT JOIN LATERAL (SELECT a.id AS id WHERE 0) d ON 0 SET a.n=7", "3"),
        ("UPDATE a NATURAL JOIN LATERAL (SELECT a.id AS id) d SET a.n=a.id*2", "3"),
        ("UPDATE a RIGHT JOIN LATERAL (SELECT 2 AS id UNION ALL SELECT 4) d ON a.id=d.id SET a.n=9", "1"),
        ("UPDATE a JOIN LATERAL (SELECT a.id AS id UNION ALL SELECT a.id) d ON 1 SET a.n=a.n+1", "3"),
    ]:
        oracle["expect"](statement, client.query(statement + ";SELECT ROW_COUNT()"), affected)
    oracle["expect"]("targets updated once", client.query("SELECT * FROM a ORDER BY id"), "1\t3\n2\t10\n3\t7")

    client.query("CREATE VIEW v AS SELECT DISTINCT id FROM a")
    for statement, target, operation in [
        ("UPDATE a JOIN LATERAL (SELECT a.id AS id) d ON 1 SET d.id=7", "d", "UPDATE"),
        ("DELETE d FROM a JOIN LATERAL (SELECT a.id AS id) d ON 1", "d", "DELETE"),
        ("UPDATE a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) jt ON 1 SET jt.x=7", "jt", "UPDATE"),
        ("DELETE jt FROM a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) jt ON 1", "jt", "DELETE"),
        ("UPDATE v SET id=7", "v", "UPDATE"),
        ("DELETE FROM v", "v", "DELETE"),
    ]:
        result = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
        error = re.search(r"ERROR (\d+) \((\w+)\).*?: (.*)", result.stderr)
        actual = (int(error[1]), error[2], error[3]) if error else None
        oracle["expect"](statement, actual, (1288, "HY000", f"The target table {target} of the {operation} is not updatable"))

    for statement, affected in [
        ("DELETE a FROM a JOIN LATERAL (SELECT 3 AS id) d ON a.id=d.id", "1"),
        ("DELETE a FROM a JOIN LATERAL (SELECT a.id AS id) d ON a.id=d.id", "2"),
    ]:
        oracle["expect"](statement, client.query(statement + ";SELECT ROW_COUNT()"), affected)
    oracle["expect"]("targets deleted", client.query("SELECT * FROM a"), "")
    verify_grouped(client)


def verify_grouped(client):
    client.query("DROP TABLE a;CREATE TABLE a(id INT PRIMARY KEY,n INT);CREATE TABLE b(id INT PRIMARY KEY,n INT);INSERT INTO a VALUES(1,0),(2,0);INSERT INTO b VALUES(1,0),(3,0)")
    for source, value in [
        ("b JOIN LATERAL (SELECT a.id+b.id AS v) d ON 1", "2"),
        ("b JOIN JSON_TABLE(JSON_ARRAY(a.id+b.id),'$[*]' COLUMNS(v INT PATH '$')) d ON 1", "2"),
        ("LATERAL (SELECT a.id AS v) d JOIN b ON d.v=b.id", "1"),
        ("JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) d JOIN b ON d.v=b.id", "1"),
        ("b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON b.id=d.v", "1"),
        ("b RIGHT JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(v INT PATH '$')) d ON b.id=d.v", "1"),
    ]:
        client.query("UPDATE a SET n=0")
        statement = "UPDATE a LEFT JOIN (" + source + ") ON a.id=b.id SET a.n=COALESCE(d.v,9)"
        oracle["expect"](statement, client.query(statement + ";SELECT ROW_COUNT();SELECT * FROM a ORDER BY id"), "2\n1\t" + value + "\n2\t9")

    statement = "UPDATE a JOIN (b JOIN LATERAL (SELECT a.id+b.id AS v) d ON 1) ON a.id=b.id SET b.n=d.v"
    oracle["expect"]("inner physical target", client.query(statement + ";SELECT ROW_COUNT();SELECT * FROM b ORDER BY id"), "1\n1\t2\n3\t0")
    statement = "UPDATE a JOIN (b JOIN LATERAL (SELECT a.id+b.id AS v) d ON d.v=a.id) ON a.id=b.id SET a.n=1"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
    error = re.search(r"ERROR (\d+) \((\w+)\).*?: (.*)", result.stderr)
    actual = (int(error[1]), error[2], error[3]) if error else None
    oracle["expect"]("inner ON scope", actual, (1054, "42S22", "Unknown column 'a.id' in 'on clause'"))
    statement = "DELETE a FROM a JOIN (b JOIN LATERAL (SELECT a.id+b.id AS v) d ON 1) ON a.id=b.id"
    oracle["expect"]("dependent delete", client.query(statement + ";SELECT ROW_COUNT();SELECT * FROM a ORDER BY id"), "1\n2\t9")


if __name__ == "__main__":
    oracle["run"](verify)
