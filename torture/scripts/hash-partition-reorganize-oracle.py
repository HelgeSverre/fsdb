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
        ("p0,p2 INTO (PARTITION a,PARTITION b)", (1519, "HY000"), "p0,p1,p2"),
        ("p0,p0 INTO (PARTITION a,PARTITION b)", (1507, "HY000"), "p0,p1,p2"),
        ("p0,p1 INTO (PARTITION a,PARTITION A)", (1517, "HY000"), "p0,p1,p2"),
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
    client.query("ALTER TABLE probe.h ADD PARTITION (PARTITION Third)")
    expect("explicit-name addition", names(client), "Renamed,Second,Third")
    client.query("ALTER TABLE probe.h REORGANIZE PARTITION")
    expect("no-list retains first name", names(client), "Renamed")
    expect("no-list retains all rows", client.query(
        "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h PARTITION(renamed)"
    ), "0,1,2,3,4,5")


    verify_additions(client)
    verify_comments(client)


def verify_additions(client):
    for method in ["HASH", "LINEAR HASH"]:
        cases = [
            ("(PARTITION Third)", None, "p0,p1,Third", "2,5" if method == "HASH" else "2"),
            ("(PARTITION Third,PARTITION Fourth)", None, "p0,p1,Third,Fourth", "2"),
            ("(PARTITION p0)", (1517, "HY000"), "p0,p1", None),
            ("(PARTITION Third,PARTITION third)", (1517, "HY000"), "p0,p1", None),
            ("PARTITIONS 1 (PARTITION Third)", (1064, "42000"), "p0,p1", None),
            ("PARTITIONS 2 (PARTITION Third)", (1064, "42000"), "p0,p1", None),
        ]
        for suffix, error, expected_names, selected_rows in cases:
            client.query(
                "DROP TABLE probe.h;CREATE TABLE probe.h(id INT PRIMARY KEY) "
                f"PARTITION BY {method}(id) PARTITIONS 2;"
                "INSERT INTO probe.h VALUES(0),(1),(2),(3),(4),(5)"
            )
            sql = "ALTER TABLE h ADD PARTITION " + suffix
            result = subprocess.run(
                [*client.process.args, "-e", "USE probe;" + sql + ";SELECT ROW_COUNT()"],
                capture_output=True, text=True, check=False,
            )
            if error is None:
                expect(method + " " + sql, result.returncode, 0)
                expect("affected rows", result.stdout.strip(), "0")
                expect("redistributed rows", client.query(
                    "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h PARTITION(third)"
                ), selected_rows)
            else:
                match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
                actual = (int(match[1]), match[2]) if match else None
                expect(method + " " + sql, actual, error)
            expect("partition names after addition", names(client), expected_names)
            expect("rows after addition", client.query(
                "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h"
            ), "0,1,2,3,4,5")


def verify_comments(client):
    def comments():
        return client.query(
            "SELECT PARTITION_NAME,PARTITION_COMMENT,NODEGROUP "
            "FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA='probe' "
            "AND TABLE_NAME='h' ORDER BY PARTITION_ORDINAL_POSITION"
        )

    client.query(
        "DROP TABLE probe.h;CREATE TABLE probe.h(id INT) PARTITION BY HASH(id) "
        "(PARTITION First COMMENT 'alpha',PARTITION Second COMMENT='beta');"
        "ALTER TABLE probe.h ADD PARTITION (PARTITION Third COMMENT 'gamma')"
    )
    expect("added comments", comments(), "First\talpha\tdefault\nSecond\tbeta\tdefault\nThird\tgamma\tdefault")
    client.query("ALTER TABLE probe.h COALESCE PARTITION 1")
    expect("coalesced comments", comments(), "First\talpha\tdefault\nSecond\tbeta\tdefault")
    client.query("ALTER TABLE probe.h REORGANIZE PARTITION First INTO (PARTITION Renamed)")
    expect("replacement clears omitted comment", comments(), "Renamed\t\tdefault\nSecond\tbeta\tdefault")
    client.query(
        "ALTER TABLE probe.h REORGANIZE PARTITION Renamed INTO "
        "(PARTITION Renamed COMMENT 'old' COMMENT 'can''t lose this');"
        "ALTER TABLE probe.h REORGANIZE PARTITION"
    )
    expect("no-list preserves last comment", comments(), "Renamed\tcan't lose this\tdefault")
    for character in ["a", "é"]:
        for length in [1024, 1025]:
            value = character * length
            sql = "ALTER TABLE h ADD PARTITION (PARTITION boundary COMMENT '" + value + "')"
            result = subprocess.run(
                [*client.process.args, "-e", "USE probe;" + sql],
                capture_output=True, text=True, check=False,
            )
            if length == 1024:
                expect("comment character boundary", result.returncode, 0)
                expect("stored comment length", client.query(
                    "SELECT CHAR_LENGTH(PARTITION_COMMENT),LENGTH(PARTITION_COMMENT) "
                    "FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA='probe' "
                    "AND TABLE_NAME='h' AND PARTITION_NAME='boundary'"
                ), f"1024\t{len(value.encode('utf-8'))}")
                client.query("ALTER TABLE probe.h COALESCE PARTITION 1")
            else:
                match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
                actual = (int(match[1]), match[2]) if match else None
                expect("overlong partition comment", actual, (1793, "HY000"))
            expect("surviving comment", comments(), "Renamed\tcan't lose this\tdefault")


if __name__ == "__main__":
    oracle["run"](verify)
