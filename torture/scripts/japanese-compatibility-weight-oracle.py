#!/usr/bin/env python3
"""Audit Japanese compatibility-ideograph weight classes on native MySQL 8.4.

Requires mysqld and PyMySQL. Starts an isolated, disposable server; prints
JSON with the compatibility characters that fold to NFKC and the aliases
that share a distinct compatibility weight. No existing database is touched.
"""

import collections
import json
import pathlib
import subprocess
import tempfile
import time
import unicodedata

import pymysql


COLLATION = "utf8mb4_ja_0900_as_cs_ks"


def audit(cursor):
    cursor.execute("CREATE TEMPORARY TABLE chars(code INT PRIMARY KEY, c VARCHAR(8) CHARACTER SET utf8mb4 COLLATE utf8mb4_ja_0900_as_cs_ks)")
    codes = [
        code
        for start, end in [(0x3400, 0xA000), (0xF900, 0xFB00), (0x2F800, 0x2FA20)]
        for code in range(start, end)
        if chr(code).isprintable()
    ]
    cursor.executemany("INSERT INTO chars VALUES (%s, %s)", [(code, chr(code)) for code in codes])
    cursor.execute("SELECT code, HEX(WEIGHT_STRING(c)) FROM chars")
    weights = dict(cursor.fetchall())

    equal = set()
    for code in range(0xF900, 0xFB00):
        if code not in weights:
            continue
        target = unicodedata.normalize("NFKC", chr(code))
        if target == chr(code):
            continue
        cursor.execute(f"SELECT HEX(WEIGHT_STRING(%s COLLATE {COLLATION}))", (target,))
        if cursor.fetchone()[0] == weights[code]:
            equal.add(code)

    classes = collections.defaultdict(list)
    for code, weight in weights.items():
        classes[weight].append(code)
    duplicate = {}
    supplementary = {}
    for members in classes.values():
        bmp = sorted(code for code in members if 0xF900 <= code < 0xFB00 and code not in equal)
        if not bmp:
            continue
        representative = bmp[0]
        duplicate.update((code, representative) for code in bmp[1:])
        supplementary.update(
            (code, representative) for code in members if 0x2F800 <= code < 0x2FA20
        )
    return {
        "mysql": cursor.connection.get_server_info(),
        "equal": sorted(equal),
        "duplicate": duplicate,
        "supplementary": supplementary,
    }


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-ja-weights-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = root / "mysql.sock"
        with (root / "mysql.log").open("w") as log:
            subprocess.run(
                ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}", "--innodb-redo-log-capacity=64M"],
                check=True, stdout=log, stderr=subprocess.STDOUT,
            )
            server = subprocess.Popen(
                ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                 f"--socket={socket}", f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                 "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                stdout=log, stderr=subprocess.STDOUT,
            )
            try:
                for _ in range(100):
                    try:
                        connection = pymysql.connect(unix_socket=str(socket), user="root", autocommit=True)
                        break
                    except pymysql.MySQLError:
                        time.sleep(0.1)
                else:
                    raise RuntimeError(f"MySQL did not start; inspect {root / 'mysql.log'}")
                with connection:
                    with connection.cursor() as cursor:
                        cursor.execute("CREATE DATABASE probe")
                        cursor.execute("USE probe")
                        print(json.dumps(audit(cursor), indent=2))
            finally:
                server.terminate()
                server.wait(timeout=10)


if __name__ == "__main__":
    main()
