"""Verify HASH partition tablespace declarations on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = oracle["expect"]


def execute(client, sql, error=None):
    result = subprocess.run([*client.process.args, "-e", "USE probe;" + sql],
                            capture_output=True, text=True, check=False)
    if error is None:
        expect(sql, result.returncode, 0)
    else:
        match = re.search(r"ERROR (\d+) \((\w+)\)", result.stderr)
        expect(sql, (int(match[1]), match[2]) if match else None, error)


def expect_rendering(client, count, explicit):
    rendered = client.query("SHOW CREATE TABLE probe.h")
    expect("rendered tablespace clauses", rendered.count("TABLESPACE = `innodb_file_per_table`"), count if explicit else 0)
    expect("tablespace metadata remains NULL", client.query(
        "SELECT COUNT(*) FROM information_schema.PARTITIONS WHERE TABLE_SCHEMA='probe' "
        "AND TABLE_NAME='h' AND TABLESPACE_NAME IS NULL"), str(count))


def verify(client, _writer):
    for option, error in [
        ("TABLESPACE=innodb_file_per_table", None),
        ("TABLESPACE innodb_file_per_table", None),
        ("TABLESPACE=`innodb_file_per_table`", None),
        ("TABLESPACE='innodb_file_per_table'", (1064, "42000")),
        ("TABLESPACE=innodb_system", (1478, "HY000")),
        ("TABLESPACE=innodb_temporary", (1478, "HY000")),
        ("TABLESPACE=missing_space", (3510, "HY000")),
        ("TABLESPACE=INNODB_FILE_PER_TABLE", (3510, "HY000")),
        ("TABLESPACE=missing_space TABLESPACE=innodb_file_per_table", None),
        ("TABLESPACE=innodb_file_per_table TABLESPACE=missing_space", (3510, "HY000")),
    ]:
        client.query("DROP TABLE IF EXISTS probe.h")
        execute(client, "CREATE TABLE h(id INT) PARTITION BY HASH(id) (PARTITION a " + option + ",PARTITION b)", error)
        expect("published table", client.query(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='h'"),
            "1" if error is None else "0")
        if error is None:
            expect_rendering(client, 2, True)

    for declared in [None, "a", "b"]:
        definitions = ",".join("PARTITION " + name + (" TABLESPACE=innodb_file_per_table" if name == declared else "") for name in ["a", "b"])
        for action, count, explicit, error in [
            ("ADD PARTITION (PARTITION c)", 3, declared is not None, None),
            ("ADD PARTITION (PARTITION c TABLESPACE=innodb_file_per_table)", 3, True, None),
            ("REORGANIZE PARTITION a INTO (PARTITION d)", 2, declared == "b", None),
            ("REORGANIZE PARTITION b INTO (PARTITION d)", 2, declared == "a", None),
            ("REORGANIZE PARTITION", 1, declared == "a", None),
            ("ADD PARTITION (PARTITION c TABLESPACE=missing_space)", 2, declared is not None, (3510, "HY000")),
            ("ADD PARTITION (PARTITION a TABLESPACE=missing_space)", 2, declared is not None, (1517, "HY000")),
            ("REORGANIZE PARTITION missing INTO (PARTITION d TABLESPACE=missing_space)", 2, declared is not None, (1507, "HY000")),
        ]:
            client.query("DROP TABLE IF EXISTS probe.h;CREATE TABLE probe.h(id INT) PARTITION BY HASH(id) (" + definitions + ");INSERT INTO probe.h VALUES(0),(1),(2)")
            execute(client, "ALTER TABLE h " + action, error)
            expect_rendering(client, count, explicit)
            expect("preserved rows", client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.h"), "0,1,2")


if __name__ == "__main__":
    oracle["run"](verify)
