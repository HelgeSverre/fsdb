"""Check correlated text-expression range results against native MySQL 8.4.11."""

import pathlib
import subprocess
import tempfile
import time

import pymysql


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-text-range-oracle-") as directory:
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
                    for query in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x VARCHAR(20) COLLATE utf8mb4_bin)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_bin)",
                        "INSERT INTO outer_bounds VALUES(1,'word99'),(2,'zzzz'),(3,NULL)",
                    ]:
                        cursor.execute(query)
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s)",
                        [(i, f"word{i}") for i in range(1000)] + [(1000, None)],
                    )
                    cases = [
                        ("LOWER(d.k)>=o.x", (11, 0, 0)),
                        ("LOWER(d.k)>o.x", (10, 0, 0)),
                        ("o.x<=LOWER(d.k)", (11, 0, 0)),
                        ("LOWER(d.k) BETWEEN o.x AND CONCAT(o.x,'9')", (11, 0, 0)),
                        ("LOWER(d.k) COLLATE utf8mb4_0900_ai_ci >= o.x", (11, 0, 0)),
                        ("LOWER(d.k)>=o.x OR d.id=1", (12, 1, 1)),
                        ("LOWER(d.k) IN (o.x,'word10')", (2, 1, 1)),
                        ("LOWER(d.k) IN (o.x,CONCAT(o.x,'9'))", (2, 0, 0)),
                        ("LOWER(d.k) IN (o.x,NULL)", (1, 0, 0)),
                        ("LOWER(d.k) IN (o.x,o.x,NULL)", (1, 0, 0)),
                        ("LOWER(d.k) IN (o.x,'word10') OR d.id=1", (3, 2, 2)),
                    ]
                    for predicate, expected in cases:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    cursor.execute("CREATE TABLE like_bounds(id INT PRIMARY KEY,p VARCHAR(20) COLLATE utf8mb4_bin)")
                    cursor.execute("INSERT INTO like_bounds VALUES(1,'word99%'),(2,'word1%'),(3,NULL),(4,'%')")
                    like_cases = [
                        ("LOWER(d.k) LIKE o.p", (11, 111, 0, 1000)),
                        ("LOWER(d.k) LIKE o.p OR d.id=2", (12, 112, 1, 1000)),
                    ]
                    for predicate, expected in like_cases:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM like_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    cursor.execute("CREATE TABLE upper_bounds(id INT PRIMARY KEY,x VARCHAR(20) COLLATE utf8mb4_bin)")
                    cursor.execute("INSERT INTO upper_bounds VALUES(1,'word1')")
                    cursor.execute(
                        "SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
                        "WHERE LOWER(d.k)<o.x) FROM upper_bounds o"
                    )
                    assert cursor.fetchone() == (1,), "strict upper text range"
                    for query in [
                        "CREATE TABLE accent_bounds(id INT PRIMARY KEY,x VARCHAR(20) COLLATE utf8mb4_bin)",
                        "CREATE TABLE accent_rows(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_bin)",
                        "INSERT INTO accent_bounds VALUES(1,'a')",
                        "INSERT INTO accent_rows VALUES(1,'a'),(2,'Ä'),(3,'z')",
                    ]:
                        cursor.execute(query)
                    cursor.execute(
                        "SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM accent_rows) d "
                        "WHERE LOWER(d.k) COLLATE utf8mb4_0900_ai_ci BETWEEN o.x AND o.x) "
                        "FROM accent_bounds o"
                    )
                    assert cursor.fetchone() == (2,), "accent-insensitive range"
                    cursor.execute(
                        "SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM accent_rows) d "
                        "WHERE LOWER(d.k) COLLATE utf8mb4_0900_ai_ci IN (o.x,'z')) "
                        "FROM accent_bounds o"
                    )
                    assert cursor.fetchone() == (3,), "accent-insensitive IN"
                    cursor.execute(
                        "SELECT (SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM accent_rows) d "
                        "WHERE LOWER(d.k) IN (o.x,'Ä' COLLATE utf8mb4_0900_ai_ci)) "
                        "FROM accent_bounds o"
                    )
                    assert cursor.fetchone() == (2,), "mixed per-member comparison collations"
                    print(f"MySQL {version}: {len(cases)} materialized text range/IN cases, {len(like_cases)} LIKE cases, upper bound, and accent cases match")
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
