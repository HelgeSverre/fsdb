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
    for join in [
        "JOIN LATERAL (SELECT a.id AS id) d USING(id)",
        "LEFT JOIN LATERAL (SELECT a.id AS id WHERE a.id=1) d USING(id)",
        "NATURAL JOIN LATERAL (SELECT a.id AS id) d",
        "NATURAL LEFT JOIN LATERAL (SELECT a.id AS id WHERE a.id=1) d",
    ]:
        query = "SELECT * FROM a " + join + " ORDER BY id"
        oracle["expect"](query, client.query(query), "1\n2")
    for query, expected in [
        ("SELECT a.id,d.id FROM a RIGHT JOIN LATERAL (SELECT 1 AS id UNION ALL SELECT 3) d ON a.id=d.id ORDER BY d.id", "1\t1\nNULL\t3"),
        ("SELECT * FROM a RIGHT JOIN LATERAL (SELECT 1 AS id UNION ALL SELECT 3) d USING(id) ORDER BY id", "1\n3"),
        ("SELECT * FROM a NATURAL RIGHT JOIN LATERAL (SELECT 1 AS id UNION ALL SELECT 3) d ORDER BY id", "1\n3"),
        ("SELECT a.id,(SELECT d.v FROM (SELECT 1 AS n) b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) FROM a ORDER BY a.id", "1\t1\n2\t2"),
    ]:
        oracle["expect"](query, client.query(query), expected)
    query = "SELECT * FROM a RIGHT JOIN LATERAL (SELECT a.id AS id) d ON 1"
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True, check=False)
    oracle["expect"]("right lateral dependency", "ERROR 1054 (42S22)" in result.stderr, True)
    query = "SELECT a.id,d.v FROM a JOIN ((SELECT 1 AS id) b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) ON 1 ORDER BY a.id"
    oracle["expect"]("preceding group sibling", client.query(query), "1\t1\n2\t2")
    for query, valid in [
        ("SELECT * FROM a RIGHT JOIN LATERAL (SELECT a.id AS id) d ON 1", False),
        ("SELECT a.id,d.v FROM a JOIN (b RIGHT JOIN LATERAL (SELECT b.id AS v) d ON 1) ON 1", False),
        ("SELECT a.id,d.v FROM a JOIN (b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) ON 1", True),
        ("SELECT a.id,(SELECT d.v FROM b RIGHT JOIN LATERAL (SELECT a.id AS v) d ON 1) FROM a", True),
    ]:
        statement = "PREPARE right_lateral FROM '" + query + "'"
        result = subprocess.run([*client.process.args, "-e", "USE probe;" + statement], capture_output=True, text=True, check=False)
        oracle["expect"](statement, result.returncode == 0, valid)
        if not valid:
            oracle["expect"]("prepared dependency diagnostic", "ERROR 1054 (42S22)" in result.stderr, True)
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

    client.query("CREATE TABLE text_values(s VARCHAR(10) COLLATE utf8mb4_bin);INSERT INTO text_values VALUES('A')")
    for source, count in [
        ("a JOIN LATERAL (SELECT text_values.s FROM text_values WHERE a.id=1) d ON 1", 1),
        ("text_values JOIN LATERAL (SELECT text_values.s) d ON 1", 1),
        ("a JOIN LATERAL (SELECT text_values.s FROM text_values WHERE a.id=1 UNION ALL SELECT text_values.s FROM text_values WHERE a.id=1) d ON 1", 2),
    ]:
        query = "SELECT d.s='a',COLLATION(d.s) FROM " + source
        oracle["expect"](query, client.query(query), "\n".join(["0\tutf8mb4_bin"] * count))


if __name__ == "__main__":
    oracle["run"](verify)
