"""Check nullable correlated materialized-source counts on MySQL 8.4.11."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-null-") as directory:
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
                        "CREATE TABLE outer_rows(id INT PRIMARY KEY,a INT)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,a INT,k INT,KEY ix_a(a),KEY ix_k(k))",
                        "INSERT INTO outer_rows VALUES(1,1),(2,2),(3,3)",
                    ]:
                        cursor.execute(sql)
                    values = ",".join(
                        f"({id},{1 if id <= 500 else 2},{'NULL' if id % 100 == 0 else id})"
                        for id in range(1, 1001)
                    )
                    cursor.execute("INSERT INTO inner_rows VALUES " + values)
                    for prefix, source in [
                        ("", "inner_rows d"),
                        ("", "(SELECT id,a,k FROM inner_rows) d"),
                        ("WITH passed AS (SELECT id,a,k FROM inner_rows) ", "passed d"),
                        ("", "(SELECT id+0 AS id,a+0 AS a,k+0 AS k FROM inner_rows LIMIT 1000) d"),
                        ("WITH materialized AS (SELECT id+0 AS id,a+0 AS a,k+0 AS k FROM inner_rows LIMIT 1000) ", "materialized d"),
                    ]:
                        cursor.execute(
                            prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE d.k IS NULL AND d.a=o.a) FROM outer_rows o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 5), (2, 5), (3, 0))
                        cursor.execute(
                            prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                            + " WHERE d.k IS NULL OR d.id=o.id) FROM outer_rows o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 11), (2, 11), (3, 11))
                    cursor.execute(
                        "SELECT o.id,(SELECT COUNT(*) FROM inner_rows d IGNORE INDEX (ix_k)"
                        " WHERE d.k IS NULL AND d.a=o.a) FROM outer_rows o ORDER BY o.id"
                    )
                    assert cursor.fetchall() == ((1, 5), (2, 5), (3, 0))
                    cursor.execute(
                        "CREATE TABLE leading_null_inner(id INT PRIMARY KEY,a INT,k INT,"
                        "UNIQUE KEY uq_k(k),KEY ix_ka(k,a))"
                    )
                    cursor.execute("INSERT INTO leading_null_inner VALUES " + values)
                    for hint in ["IGNORE INDEX (ix_ka)", "IGNORE INDEX (uq_k)",
                                 "IGNORE INDEX (uq_k,ix_ka)"]:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM leading_null_inner d " + hint
                            + " WHERE d.k IS NULL AND d.a=o.a) FROM outer_rows o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 5), (2, 5), (3, 0))
                    cursor.execute(
                        "CREATE TABLE later_null_inner(id INT PRIMARY KEY,a INT,k INT,KEY ix_ak(a,k))"
                    )
                    cursor.execute("INSERT INTO later_null_inner VALUES " + values)
                    for hint in ["", "IGNORE INDEX (ix_ak)"]:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM later_null_inner d " + hint
                            + " WHERE d.a=o.a AND d.k IS NULL) FROM outer_rows o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 5), (2, 5), (3, 0))
                print(f"MySQL {version}: physical, materialized, unique, and composite NULL counts (5, 5, 0); OR counts (11, 11, 11)")
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
