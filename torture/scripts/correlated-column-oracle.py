"""Verify qualified outer-column lookup on disposable native MySQL 8.4.11."""

import pathlib
import runpy

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    client.query("USE probe;CREATE TABLE a(id INT);CREATE TABLE b(id INT);"
                 "INSERT INTO a VALUES(1),(2);INSERT INTO b VALUES(1)")
    for expression, expected in [
        ("(SELECT COUNT(*) FROM (SELECT 1 AS other) a JOIN b ON a.id=b.id)", "1\t1\n2\t0"),
        ("(SELECT a.id FROM (SELECT 1 AS other) a)", "1\t1\n2\t2"),
        ("(SELECT a.id FROM (SELECT NULL AS id) a)", "1\tNULL\n2\tNULL"),
        ("(SELECT (SELECT a.id FROM (SELECT 1 AS other) a) FROM (SELECT 2 AS other) a)", "1\t1\n2\t2"),
    ]:
        query = "SELECT a.id," + expression + " AS correlated FROM a ORDER BY a.id"
        oracle["expect"](expression, client.query(query), expected)


if __name__ == "__main__":
    oracle["run"](verify)
