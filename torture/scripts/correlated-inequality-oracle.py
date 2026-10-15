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
                    for sql in [
                        "CREATE TABLE decimal_bounds(id INT PRIMARY KEY,k DECIMAL(10,2))",
                        "CREATE TABLE decimals(id INT PRIMARY KEY,k DECIMAL(10,2),KEY ix_k(k))",
                        "INSERT INTO decimal_bounds VALUES(1,1.25),(2,NULL),(3,51.25)",
                    ]:
                        cursor.execute(sql)
                    values = ",".join(
                        f"({id},{1 if id <= 950 else id - 949}.25)" for id in range(1, 1001)
                    )
                    cursor.execute("INSERT INTO decimals VALUES " + values)
                    for source in [
                        "decimals d", "(SELECT id,k FROM decimals) d",
                        "(SELECT id+0 AS id,k+0 AS k FROM decimals LIMIT 1000) d",
                    ]:
                        for predicate, expected in cases:
                            cursor.execute(
                                "SELECT (SELECT COUNT(*) FROM " + source + " WHERE " + predicate
                                + ") FROM decimal_bounds o ORDER BY o.id"
                            )
                            actual = tuple(row[0] for row in cursor.fetchall())
                            assert actual == expected, (source, predicate, expected, actual)
                    for sql in [
                        "CREATE TABLE integer_bounds(id INT PRIMARY KEY,k INT)",
                        "CREATE TABLE decimal_integer_rows(id INT PRIMARY KEY,k DECIMAL(10,2),KEY ix_k(k))",
                        "CREATE TABLE decimal_integer_bounds(id INT PRIMARY KEY,k DECIMAL(10,2))",
                        "INSERT INTO integer_bounds VALUES(1,1),(2,NULL),(3,51)",
                        "INSERT INTO decimal_integer_bounds VALUES(1,1.00),(2,NULL),(3,51.00)",
                    ]:
                        cursor.execute(sql)
                    values = ",".join(
                        f"({id},{1 if id <= 950 else id - 949}.00)" for id in range(1, 1001)
                    )
                    cursor.execute("INSERT INTO decimal_integer_rows VALUES " + values)
                    for source, bounds in [
                        ("decimal_integer_rows d", "integer_bounds"),
                        ("(SELECT id,k FROM decimal_integer_rows) d", "integer_bounds"),
                        ("(SELECT id+0 AS id,k+0 AS k FROM decimal_integer_rows LIMIT 1000) d", "integer_bounds"),
                        ("nums d", "decimal_integer_bounds"),
                        ("(SELECT id,k FROM nums) d", "decimal_integer_bounds"),
                        ("(SELECT id+0 AS id,k+0 AS k FROM nums LIMIT 1000) d", "decimal_integer_bounds"),
                    ]:
                        for predicate, expected in cases:
                            cursor.execute(
                                "SELECT (SELECT COUNT(*) FROM " + source + " WHERE " + predicate
                                + ") FROM " + bounds + " o ORDER BY o.id"
                            )
                            actual = tuple(row[0] for row in cursor.fetchall())
                            assert actual == expected, (source, bounds, predicate, expected, actual)
                    cursor.execute("CREATE TABLE fractional_bounds(id INT PRIMARY KEY,k DECIMAL(10,2))")
                    cursor.execute("INSERT INTO fractional_bounds VALUES(1,1.50),(2,NULL),(3,51.50)")
                    cursor.execute("CREATE TABLE double_bounds(id INT PRIMARY KEY,k DOUBLE)")
                    cursor.execute("INSERT INTO double_bounds VALUES(1,1.50),(2,NULL),(3,51.50)")
                    fractional_cases = [
                        ("d.k > o.k", (50, 0, 0)),
                        ("d.k >= o.k", (50, 0, 0)),
                        ("d.k < o.k", (950, 0, 1000)),
                        ("d.k <= o.k", (950, 0, 1000)),
                        ("d.k > o.k OR d.id=500", (51, 1, 1)),
                    ]
                    for source in [
                        "nums d", "(SELECT id,k FROM nums) d",
                        "(SELECT id+0 AS id,k+0 AS k FROM nums LIMIT 1000) d",
                    ]:
                        for bounds in ["fractional_bounds", "double_bounds"]:
                            for predicate, expected in fractional_cases:
                                cursor.execute(
                                    "SELECT (SELECT COUNT(*) FROM " + source + " WHERE " + predicate
                                    + ") FROM " + bounds + " o ORDER BY o.id"
                                )
                                actual = tuple(row[0] for row in cursor.fetchall())
                                assert actual == expected, (source, bounds, predicate, expected, actual)
                    for sql in [
                        "CREATE TABLE double_edge_rows(k INT,KEY ix_k(k))",
                        "CREATE TABLE double_edge_bounds(id INT PRIMARY KEY,k DOUBLE)",
                        "INSERT INTO double_edge_rows VALUES(-2147483648),(-2),(-1),(0),(1),(2147483647)",
                        "INSERT INTO double_edge_bounds VALUES(1,-1.5),(2,2147483646.5),(3,2147483647.5)",
                    ]:
                        cursor.execute(sql)
                    for predicate, expected in [
                        ("d.k > o.k", (4, 1, 0)),
                        ("d.k < o.k", (2, 5, 6)),
                    ]:
                        cursor.execute(
                            "SELECT (SELECT COUNT(*) FROM double_edge_rows d WHERE " + predicate
                            + ") FROM double_edge_bounds o ORDER BY o.id"
                        )
                        actual = tuple(row[0] for row in cursor.fetchall())
                        assert actual == expected, (predicate, expected, actual)
                    for sql in [
                        "CREATE TABLE big_double_rows(id INT PRIMARY KEY,k BIGINT,KEY ix_k(k))",
                        "CREATE TABLE big_double_bounds(id INT PRIMARY KEY,k DOUBLE)",
                        "INSERT INTO big_double_rows VALUES(1,-9223372036854775808),(2,-9007199254740993),(3,-9007199254740992),(4,-1),(5,0),(6,9007199254740991),(7,9007199254740992),(8,9007199254740993),(9,9223372036854775807)",
                        "INSERT INTO big_double_bounds VALUES(1,9007199254740992e0),(2,-9007199254740992e0),(3,1.5)",
                    ]:
                        cursor.execute(sql)
                    for predicate, expected in [
                        ("d.k > o.k", (1, 6, 4)),
                        ("d.k < o.k", (6, 1, 5)),
                    ]:
                        for hint in ["", " IGNORE INDEX (ix_k)"]:
                            cursor.execute(
                                "SELECT (SELECT COUNT(*) FROM big_double_rows d" + hint
                                + " WHERE " + predicate + ") FROM big_double_bounds o ORDER BY o.id"
                            )
                            actual = tuple(row[0] for row in cursor.fetchall())
                            assert actual == expected, (predicate, hint, expected, actual)
                    for hint in ["", " IGNORE INDEX (ix_k)"]:
                        cursor.execute(
                            "SELECT id FROM big_double_rows" + hint
                            + " WHERE k > 9007199254740992e0 ORDER BY id"
                        )
                        actual = tuple(row[0] for row in cursor.fetchall())
                        assert actual == (8, 9), (hint, actual)
                    cursor.execute("CREATE TABLE big_double_many(id INT PRIMARY KEY,k BIGINT,KEY ix_k(k))")
                    cursor.execute("CREATE TABLE big_double_selective(k DOUBLE)")
                    cursor.execute("INSERT INTO big_double_selective VALUES(9007199254740992e0)")
                    values = ",".join(
                        f"({id},{9007199254740992 + (0 if id <= 950 else id - 950)})"
                        for id in range(1, 1001)
                    )
                    cursor.execute("INSERT INTO big_double_many VALUES " + values)
                    for source in [
                        "big_double_many d", "big_double_many d IGNORE INDEX (ix_k)",
                        "(SELECT id,k FROM big_double_many) d",
                        "(SELECT id+0 AS id,k+0 AS k FROM big_double_many LIMIT 1000) d",
                    ]:
                        cursor.execute(
                            "SELECT (SELECT COUNT(*) FROM " + source
                            + " WHERE d.k > o.k) FROM big_double_selective o"
                        )
                        actual = cursor.fetchone()[0]
                        assert actual == 49, (source, actual)
                        cursor.execute(
                            "SELECT (SELECT COUNT(*) FROM " + source
                            + " WHERE d.k > o.k OR d.id=500) FROM big_double_selective o"
                        )
                        actual = cursor.fetchone()[0]
                        assert actual == 50, (source, actual)
                    cursor.execute("SELECT COUNT(*) FROM big_double_many WHERE k > 9007199254740992e0")
                    assert cursor.fetchone()[0] == 50
                    for sql in [
                        "CREATE TABLE unsigned_double_rows(id INT PRIMARY KEY,k BIGINT UNSIGNED,KEY ix_k(k))",
                        "INSERT INTO unsigned_double_rows VALUES(1,9007199254740992),(2,9007199254740993),(3,18446744073709551615)",
                    ]:
                        cursor.execute(sql)
                    for hint in ["", " IGNORE INDEX (ix_k)"]:
                        cursor.execute(
                            "SELECT (SELECT COUNT(*) FROM unsigned_double_rows d" + hint
                            + " WHERE d.k > o.k) FROM big_double_selective o"
                        )
                        assert cursor.fetchone()[0] == 1, hint
                        cursor.execute(
                            "SELECT id FROM unsigned_double_rows" + hint
                            + " WHERE k > 9007199254740992e0 ORDER BY id"
                        )
                        assert tuple(row[0] for row in cursor.fetchall()) == (2, 3), hint
                print(f"MySQL {version}: integer, DECIMAL, DOUBLE, and mixed numeric range cases passed")
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
