"""Verify HASH partition names and reorganization on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def names(client):
    return client.query(
        "SELECT GROUP_CONCAT(PARTITION_NAME ORDER BY PARTITION_ORDINAL_POSITION) "
        "FROM information_schema.PARTITIONS "
        "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"
    )


def verify(client, _writer):
    cases = [
        ("p0 INTO (PARTITION q0)", None, "q0,p1,p2"),
        ("p0,p1 INTO (PARTITION q0,PARTITION q1)", None, "q0,q1,p2"),
        ("p1,p0 INTO (PARTITION q0,PARTITION q1)", None, "q0,q1,p2"),
        ("p0 INTO (PARTITION q0,PARTITION q1)", (1510, "HY000"), "p0,p1,p2"),
        ("p0,p1 INTO (PARTITION q0)", (1510, "HY000"), "p0,p1,p2"),
        ("p0 INTO (PARTITION p1)", (1517, "HY000"), "p0,p1,p2"),
        ("p4 INTO (PARTITION q0)", (1507, "HY000"), "p0,p1,p2"),
        ("", None, "p0"),
    ]
    for method in ["HASH(id)", "LINEAR HASH(id)"]:
        for clause, error, expected_names in cases:
            client.query(
                "DROP TABLE IF EXISTS probe.h;"
                "CREATE TABLE probe.h(id INT PRIMARY KEY) PARTITION BY "
                + method + " PARTITIONS 3;"
                "INSERT INTO probe.h VALUES(0),(1),(2),(3),(4),(5)"
            )
            sql = "ALTER TABLE h REORGANIZE PARTITION " + clause
            result = subprocess.run(
                [*client.process.args, "-e", "USE probe;" + sql],
                capture_output=True, text=True, check=False,
            )
            if error is None:
                expect(method + " " + sql, result.returncode, 0)
            else:
                match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
                actual = (int(match[1]), match[2]) if match else None
                expect(method + " " + sql, actual, error)
            expect("partition names", names(client), expected_names)
            expect("preserved rows", client.query(
                "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h"
            ), "0,1,2,3,4,5")

    client.query(
        "DROP TABLE probe.h;"
        "CREATE TABLE probe.h(id INT PRIMARY KEY) PARTITION BY HASH(id) "
        "(PARTITION First,PARTITION Second);"
        "INSERT INTO probe.h VALUES(0),(1),(2),(3),(4),(5)"
    )
    for action, expected_names in [
        ("ADD PARTITION PARTITIONS 1", "First,Second,p2"),
        ("COALESCE PARTITION 1", "First,Second"),
        ("REORGANIZE PARTITION First INTO (PARTITION Renamed)", "Renamed,Second"),
    ]:
        client.query("ALTER TABLE probe.h " + action)
        expect(action, names(client), expected_names)
    expect("case-insensitive partition selection", client.query(
        "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h PARTITION(renamed)"
    ), "0,2,4")


if __name__ == "__main__":
    oracle["run"](verify)
