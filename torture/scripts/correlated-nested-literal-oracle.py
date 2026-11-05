"""Verify nested correlated literal projections on MySQL 8.4.11."""

import pathlib
import runpy

oracle = runpy.run_path(
    str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py"))
)


def verify(client, _writer):
    client.query(
        "USE probe;CREATE TABLE outer_rows(id INT PRIMARY KEY);"
        "CREATE TABLE inner_rows(id INT PRIMARY KEY,parent_id INT);"
        "INSERT INTO outer_rows VALUES(1),(2);"
        "INSERT INTO inner_rows VALUES(1,1),(2,1),(3,2),(4,2)"
    )
    for literal in ("1", "'matched'", "1.5"):
        query = (
            "SELECT o.id,(SELECT COUNT(*) FROM inner_rows i "
            "WHERE i.parent_id=o.id AND "
            f"(SELECT {literal} WHERE (SELECT i.id+o.id "
            "WHERE i.id % 2=0) IS NOT NULL) IS NOT NULL) "
            "FROM outer_rows o ORDER BY o.id"
        )
        oracle["expect"](literal, client.query(query), "1\t1\n2\t1")


if __name__ == "__main__":
    oracle["run"](verify)
