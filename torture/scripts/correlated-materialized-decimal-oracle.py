"""Check materialized correlated DECIMAL expression results against MySQL 8.4."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-decimal-oracle-") as directory:
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
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x DECIMAL(12,2),i BIGINT,f DOUBLE)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,k DECIMAL(12,2))",
                        "INSERT INTO outer_bounds VALUES"
                        "(1,10.25,10,10.25),(2,999.00,999,999.00),"
                        "(3,NULL,NULL,NULL),(4,-10.25,-10,-10.25)",
                    ]:
                        cursor.execute(query)
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s)",
                        [(i, f"{i - 500}.25") for i in range(1000)] + [(1000, None)],
                    )

                    cases = [
                        ("d.k+0.00=o.x", (1, 0, 0, 1)),
                        ("o.x=d.k+0.00", (1, 0, 0, 1)),
                        ("d.k*1.00=o.x", (1, 0, 0, 1)),
                        ("d.k+0.00=o.i", (0, 0, 0, 0)),
                        ("d.k+0.00=o.f", (1, 0, 0, 1)),
                        ("d.k+0.00=o.x OR d.id=12", (2, 1, 1, 2)),
                    ]
                    for predicate, expected in cases:
                        query = (
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        cursor.execute(query)
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    print(f"MySQL {version}: {len(cases)} decimal comparison cases match")
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
