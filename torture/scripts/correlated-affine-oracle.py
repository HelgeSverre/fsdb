"""Check correlated affine equality over stable sources on MySQL 8.4.11."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-correlated-affine-") as directory:
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
                        "CREATE TABLE bound(id INT PRIMARY KEY,x BIGINT)",
                        "CREATE TABLE vals(id INT PRIMARY KEY,k INT,KEY ix_k(k))",
                        "INSERT INTO bound VALUES(1,10),(2,1000),(3,NULL),(4,-10),"
                        "(5,2147483648),(6,-2147483647),(7,2147483649)",
                        "INSERT INTO vals VALUES(1,9),(2,11),(3,-11),(4,-9),"
                        "(5,-2147483648),(6,2147483647),(7,-2147483647),"
                        "(8,8),(9,-12)",
                    ]:
                        cursor.execute(sql)

                    cases = [
                        ("d.k+1", (1, 0, 0, 1, 1, 1, 0)),
                        ("d.k+2", (1, 0, 0, 1, 0, 0, 1)),
                        ("d.k+-1", (1, 0, 0, 1, 0, 0, 0)),
                        ("d.k-1", (1, 0, 0, 1, 0, 0, 0)),
                        ("1+d.k", (1, 0, 0, 1, 1, 1, 0)),
                        ("1-d.k", (1, 0, 0, 1, 1, 0, 1)),
                        ("-1-d.k", (1, 0, 0, 1, 0, 0, 0)),
                    ]
                    for prefix, source in [
                        ("", "vals d"),
                        ("", "(SELECT id,k FROM vals) d"),
                        ("WITH passthrough AS (SELECT id,k FROM vals) ", "passthrough d"),
                        ("", "(SELECT DISTINCT id,k FROM vals) d"),
                        ("WITH materialized AS (SELECT DISTINCT id,k FROM vals) ", "materialized d"),
                    ]:
                        for expression, expected in cases:
                            cursor.execute(
                                prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                                + " WHERE " + expression + "=o.x) FROM bound o ORDER BY o.id"
                            )
                            actual = tuple(count for _, count in cursor.fetchall())
                            assert actual == expected, (source, expression, actual, expected)
                    for index, (column_type, minimum, maximum) in enumerate([
                        ("TINYINT", -128, 127),
                        ("SMALLINT", -32768, 32767),
                        ("MEDIUMINT", -8388608, 8388607),
                        ("INT", -2147483648, 2147483647),
                    ]):
                        table = f"vals_type_{index}"
                        first_id = 10 + 4 * index
                        cursor.execute(f"CREATE TABLE {table}(id INT PRIMARY KEY,k {column_type},KEY ix_k(k))")
                        cursor.execute(f"INSERT INTO {table} VALUES(1,{minimum}),(2,{maximum})")
                        cursor.execute(
                            "INSERT INTO bound VALUES"
                            f"({first_id},{minimum + 1}),({first_id + 1},{minimum}),"
                            f"({first_id + 2},{maximum + 1}),({first_id + 3},{maximum + 2})"
                        )
                        cursor.execute(
                            f"SELECT o.id,(SELECT COUNT(*) FROM {table} d WHERE d.k+1=o.x) "
                            f"FROM bound o WHERE o.id BETWEEN {first_id} AND {first_id + 3} ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == (1, 0, 1, 0), (column_type, actual)
                print(f"MySQL {version}: physical, pass-through, and materialized affine counts match")
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
