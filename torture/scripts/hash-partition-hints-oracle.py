"""Verify HASH partition row hints and node groups on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def definitions(client):
    rendered = client.query("SHOW CREATE TABLE probe.h")
    return re.findall(r"PARTITION ([abcdn]) (.*?)ENGINE = InnoDB", rendered)


def verify(client, _writer):
    for options, rendered, nodegroup in [
        ("MAX_ROWS=100 MIN_ROWS=10", "MAX_ROWS = 100 MIN_ROWS = 10 ", "default"),
        ("MAX_ROWS=0 MIN_ROWS=0", "", "default"),
        ("MIN_ROWS=20 MAX_ROWS=10", "MAX_ROWS = 10 MIN_ROWS = 20 ", "default"),
        ("MAX_ROWS=10 MAX_ROWS=20", "MAX_ROWS = 20 ", "default"),
        ("MIN_ROWS=10 MIN_ROWS=20", "MIN_ROWS = 20 ", "default"),
        ("MAX_ROWS=9223372036854775807", "MAX_ROWS = 9223372036854775807 ", "default"),
        ("MIN_ROWS=9223372036854775807", "MIN_ROWS = 9223372036854775807 ", "default"),
        ("NODEGROUP=0", "NODEGROUP = 0 ", "0"),
        ("NODEGROUP=7", "NODEGROUP = 7 ", "7"),
        ("NODEGROUP=65535", "", "default"),
        ("NODEGROUP=65536", "NODEGROUP = 0 ", "0"),
        ("NODEGROUP=4294967295", "", "default"),
        ("NODEGROUP=18446744073709551615", "", "default"),
        ("NODEGROUP=7 NODEGROUP=8", "NODEGROUP = 8 ", "8"),
        ("MAX_ROWS 10 MIN_ROWS 2 NODEGROUP 7", "NODEGROUP = 7 MAX_ROWS = 10 MIN_ROWS = 2 ", "7"),
    ]:
        client.query("DROP TABLE IF EXISTS probe.h;CREATE TABLE probe.h(id INT) "
                     "PARTITION BY HASH(id) (PARTITION a " + options + ",PARTITION b)")
        expect(options + " rendering", definitions(client), [("a", rendered), ("b", "")])
        expect(options + " metadata", client.query(
            "SELECT NODEGROUP FROM information_schema.PARTITIONS "
            "WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h' ORDER BY PARTITION_ORDINAL_POSITION"
        ), nodegroup + "\ndefault")

    for options in ["MAX_ROWS=-1", "MIN_ROWS=-1", "NODEGROUP=-1",
                    "MAX_ROWS=9223372036854775808", "MIN_ROWS=9223372036854775808",
                    "MAX_ROWS=18446744073709551615", "MAX_ROWS=18446744073709551616",
                    "NODEGROUP=18446744073709551616"]:
        client.query("DROP TABLE IF EXISTS probe.h")
        result = subprocess.run(
            [*client.process.args, "-e", "USE probe;CREATE TABLE h(id INT) "
             "PARTITION BY HASH(id) (PARTITION a " + options + ",PARTITION b)"],
            capture_output=True, text=True, check=False,
        )
        match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
        expect(options, (int(match[1]), match[2]) if match else None, (1064, "42000"))
        expect("rejection publishes no table", client.query(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"
        ), "0")

    first = "NODEGROUP = 7 MAX_ROWS = 100 MIN_ROWS = 10 "
    second = "NODEGROUP = 8 MAX_ROWS = 200 MIN_ROWS = 20 "
    for named in [False, True]:
        client.query("DROP TABLE IF EXISTS probe.h;CREATE TABLE probe.h(id INT) PARTITION BY HASH(id) "
                     "(PARTITION a MAX_ROWS=100 MIN_ROWS=10 NODEGROUP=7,"
                     "PARTITION b MAX_ROWS=200 MIN_ROWS=20 NODEGROUP=8);"
                     "INSERT INTO probe.h VALUES(0),(1),(2),(3),(4),(5)")
        actions = [
            ("ADD PARTITION (PARTITION c MAX_ROWS=300 MIN_ROWS=30 NODEGROUP=9)",
             [("a", first), ("b", second), ("c", "NODEGROUP = 9 MAX_ROWS = 300 MIN_ROWS = 30 ")]),
            ("COALESCE PARTITION 1", [("a", first), ("b", second)]),
        ]
        if named:
            actions.append(("REORGANIZE PARTITION a INTO (PARTITION d)", [("d", ""), ("b", second)]))
        actions.append(("REORGANIZE PARTITION", [("d", "")] if named else [("a", first)]))
        for action, expected in actions:
            client.query("ALTER TABLE probe.h " + action)
            expect(action, definitions(client), expected)
            expect("preserved rows", client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h"), "0,1,2,3,4,5")


if __name__ == "__main__":
    oracle["run"](verify)
