"""Compare correlated ABS equality over materialized sources with MySQL 8.4."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-abs-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
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
                        "CREATE TABLE abs_bounds(id INT PRIMARY KEY,x BIGINT)",
                        "CREATE TABLE abs_rows(id INT PRIMARY KEY,k INT)",
                        "INSERT INTO abs_bounds VALUES(1,10),(2,999),(3,NULL),(4,-10),(5,2147483648)",
                    ]:
                        cursor.execute(sql)
                    cursor.executemany(
                        "INSERT INTO abs_rows VALUES(%s,%s)",
                        [(i, i - 500) for i in range(1000)] + [(1000, -2147483648)],
                    )

                    for prefix, source in [
                        ("", "(SELECT DISTINCT id,k FROM abs_rows) d"),
                        ("WITH materialized AS (SELECT DISTINCT id,k FROM abs_rows) ", "materialized d"),
                    ]:
                        for predicate, expected in [
                            ("ABS(d.k)=o.x", ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)=o.x OR d.id=1", ((1, 3), (2, 1), (3, 1), (4, 1), (5, 2))),
                        ]:
                            cursor.execute(
                                prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                                + " WHERE " + predicate + ") FROM abs_bounds o ORDER BY o.id"
                            )
                            actual = cursor.fetchall()
                            assert actual == expected, (predicate, actual, expected)
                print(f"MySQL {version}: derived and CTE ABS counts match")
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
