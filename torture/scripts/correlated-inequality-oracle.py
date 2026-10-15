"""Check correlated numeric inequality and NULL-bound results on MySQL 8.4.11."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-inequality-") as directory:
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
                        "CREATE TABLE bounds(id INT PRIMARY KEY,k INT)",
                        "CREATE TABLE nums(id INT PRIMARY KEY,k INT,KEY ix_k(k))",
                        "INSERT INTO bounds VALUES(1,1),(2,NULL),(3,51)",
                    ]:
                        cursor.execute(sql)
                    values = ",".join(
                        f"({id},{1 if id <= 950 else id - 949})" for id in range(1, 1001)
                    )
                    cursor.execute("INSERT INTO nums VALUES " + values)
                    cases = [
                        ("d.k <> o.k", (50, 0, 999)),
                        ("NOT (d.k = o.k)", (50, 0, 999)),
                        ("d.k <> o.k OR d.id=500", (51, 1, 999)),
                    ]
                    for source in [
                        "nums d", "(SELECT id,k FROM nums) d",
                        "(SELECT id+0 AS id,k+0 AS k FROM nums LIMIT 1000) d",
                    ]:
                        for predicate, expected in cases:
                            cursor.execute(
                                "SELECT (SELECT COUNT(*) FROM " + source + " WHERE " + predicate
                                + ") FROM bounds o ORDER BY o.id"
                            )
                            actual = tuple(row[0] for row in cursor.fetchall())
                            assert actual == expected, (source, predicate, expected, actual)
                print(f"MySQL {version}: correlated inequality counts 50/0/999 and OR counts 51/1/999")
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
