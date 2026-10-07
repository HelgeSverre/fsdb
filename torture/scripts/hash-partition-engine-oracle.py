"""Verify partition engine defaults and error precedence on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def execute(client, sql, expected_error=None, mode="NO_ENGINE_SUBSTITUTION"):
    result = subprocess.run(
        [*client.process.args, "-e", f"USE probe;SET sql_mode='{mode}';" + sql + ";SHOW WARNINGS"],
        capture_output=True, text=True, check=False,
    )
    if expected_error is None:
        expect(sql, result.returncode, 0)
    else:
        match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
        actual = (int(match[1]), match[2]) if match else None
        expect(sql, actual, expected_error)
    return result.stdout.strip()


def verify(client, _writer):
    mixed = (1497, "HY000")
    unsupported = (1178, "42000")
    unknown = (1286, "42000")
    for table_engine in [None, "InnoDB", "MyISAM"]:
        for first, second in [
            (None, None), ("InnoDB", None), (None, "InnoDB"),
            ("InnoDB", "InnoDB"), ("MyISAM", "MyISAM"),
            ("InnoDB", "MyISAM"), ("unknown_engine", "InnoDB"),
        ]:
            client.query("DROP TABLE IF EXISTS probe.h")
            table_option = "" if table_engine is None else " ENGINE=" + table_engine
            definitions = []
            for name, engine in [("a", first), ("b", second)]:
                definitions.append("PARTITION " + name + ("" if engine is None else " ENGINE=" + engine))
            sql = ("CREATE TABLE h(id INT)" + table_option
                   + " PARTITION BY HASH(id) (" + ",".join(definitions) + ")")
            if first == "unknown_engine":
                error = unknown
            elif table_engine is None:
                if first != second:
                    error = mixed
                elif first in [None, "InnoDB"]:
                    error = None
                else:
                    error = unsupported
            elif any(engine is not None and engine != table_engine for engine in [first, second]):
                error = mixed
            else:
                error = None if table_engine == "InnoDB" else unsupported
            execute(client, sql, error)
            expect("published table", client.query(
                "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"
            ), "1" if error is None else "0")

    for table_option in ["", " ENGINE=InnoDB"]:
        client.query("DROP TABLE IF EXISTS probe.h")
        sql = ("CREATE TABLE h(id INT)" + table_option
               + " PARTITION BY HASH(id) (PARTITION a ENGINE=unknown_engine,PARTITION b)")
        expect("substituted engine warning", execute(client, sql, mode=""),
               "Warning\t1286\tUnknown storage engine 'unknown_engine'")
        expect("substitution publishes both partitions", client.query(
            "SELECT GROUP_CONCAT(PARTITION_NAME ORDER BY PARTITION_ORDINAL_POSITION) "
            "FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"
        ), "a,b")

    for action, error, expected_names in [
        ("ADD PARTITION (PARTITION c ENGINE=InnoDB)", None, "p0,p1,c"),
        ("ADD PARTITION (PARTITION c ENGINE=InnoDB,PARTITION d)", None, "p0,p1,c,d"),
        ("ADD PARTITION (PARTITION c ENGINE=MyISAM)", mixed, "p0,p1"),
        ("ADD PARTITION (PARTITION p0 ENGINE=MyISAM)", (1517, "HY000"), "p0,p1"),
        ("ADD PARTITION (PARTITION p0 ENGINE=unknown_engine)", unknown, "p0,p1"),
        ("REORGANIZE PARTITION p0 INTO (PARTITION c ENGINE=InnoDB)", None, "c,p1"),
        ("REORGANIZE PARTITION p0 INTO (PARTITION c ENGINE=MyISAM)", mixed, "p0,p1"),
        ("REORGANIZE PARTITION missing INTO (PARTITION c ENGINE=MyISAM)", (1507, "HY000"), "p0,p1"),
    ]:
        client.query(
            "DROP TABLE IF EXISTS probe.h;CREATE TABLE probe.h(id INT) ENGINE=InnoDB "
            "PARTITION BY HASH(id) PARTITIONS 2;INSERT INTO probe.h VALUES(0),(1),(2),(3),(4),(5)"
        )
        execute(client, "ALTER TABLE h " + action, error)
        expect("partition names", client.query(
            "SELECT GROUP_CONCAT(PARTITION_NAME ORDER BY PARTITION_ORDINAL_POSITION) "
            "FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"
        ), expected_names)
        expect("preserved rows", client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h"), "0,1,2,3,4,5")


if __name__ == "__main__":
    oracle["run"](verify)
