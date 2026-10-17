"""Verify correlated text-expression range results against native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import time

import pymysql


CASES = [
    ("LOWER(d.k)<o.x", (13, 0, 1003, 0, 0)),
    ("LOWER(d.k)<=o.x", (14, 2, 1003, 0, 0)),
    ("LOWER(d.k)>o.x", (989, 1001, 0, 0, 1003)),
    ("o.x>LOWER(d.k)", (13, 0, 1003, 0, 0)),
    ("LOWER(d.k) BETWEEN o.lo AND o.hi", (3, 3, 5, 0, 0)),
    ("LOWER(d.k) COLLATE utf8mb4_0900_ai_ci BETWEEN o.lo AND o.hi", (3, 3, 5, 0, 2)),
    ("LOWER(d.k) BETWEEN o.lo AND o.hi OR d.id=999", (4, 4, 5, 1, 1)),
]


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-text-range-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = connection = None

        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=log, stderr=subprocess.STDOUT,
                )
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={socket}", f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                connection = connect(socket)
                with connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    for sql in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,"
                        "x VARCHAR(20) COLLATE utf8mb4_bin,"
                        "lo VARCHAR(20) COLLATE utf8mb4_bin,"
                        "hi VARCHAR(20) COLLATE utf8mb4_bin)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,"
                        "k VARCHAR(20) COLLATE utf8mb4_bin)",
                    ]:
                        cursor.execute(sql)
                    cursor.executemany(
                        "INSERT INTO outer_bounds VALUES(%s,%s,%s,%s)",
                        [(1, "word010", "word010", "word012"),
                         (2, "alpha", "alpha", "beta"),
                         (3, "zeta", "word995", "word999"),
                         (4, None, None, None),
                         (5, "ALPHA", "ALPHA", "ALPHA")],
                    )
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s)",
                        [(i, f"word{i:03d}") for i in range(1000)]
                        + [(1000, "ALPHA"), (1001, "alpha"),
                           (1002, "Beta"), (1003, None)],
                    )
                    for predicate, expected in CASES:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    cursor.execute(
                        "WITH materialized AS (SELECT DISTINCT id,k FROM inner_rows) "
                        "SELECT o.id,(SELECT COUNT(*) FROM materialized d "
                        "WHERE LOWER(d.k) BETWEEN o.lo AND o.hi) "
                        "FROM outer_bounds o ORDER BY o.id"
                    )
                    actual = tuple(count for _, count in cursor.fetchall())
                    assert actual == (3, 3, 5, 0, 0), ("stable CTE", actual)
                    print(f"MySQL {version}: {len(CASES)} derived ranges and one CTE range match")
            finally:
                if connection:
                    connection.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
