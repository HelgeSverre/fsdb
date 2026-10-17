"""Compare correlated ABS equality over indexed and materialized sources with MySQL 8.4."""

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
                        "CREATE TABLE abs_rows(id INT PRIMARY KEY,k INT,KEY ix_k(k))",
                        "INSERT INTO abs_bounds VALUES(1,10),(2,999),(3,NULL),(4,-10),(5,2147483648)",
                    ]:
                        cursor.execute(sql)
                    cursor.executemany(
                        "INSERT INTO abs_rows VALUES(%s,%s)",
                        [(i, i - 500) for i in range(1000)] + [(1000, -2147483648)],
                    )

                    for prefix, source in [
                        ("", "abs_rows d"),
                        ("", "(SELECT id,k FROM abs_rows) d"),
                        ("WITH passthrough AS (SELECT id,k FROM abs_rows) ", "passthrough d"),
                        ("", "(SELECT DISTINCT id,k FROM abs_rows) d"),
                        ("WITH materialized AS (SELECT DISTINCT id,k FROM abs_rows) ", "materialized d"),
                    ]:
                        for predicate, expected in [
                            ("ABS(d.k)=o.x", ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("o.x=ABS(d.k)", ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)=o.x OR d.id=1", ((1, 3), (2, 1), (3, 1), (4, 1), (5, 2))),
                            ("ABS(d.k)+0 IN (o.x,o.x+1)", ((1, 4), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0 IN (o.x,NULL)", ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0 IN (o.x,o.x)", ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0 IN (o.x,NULL) OR d.id=1", ((1, 3), (2, 1), (3, 1), (4, 1), (5, 2))),
                            ("ABS(d.k)+0 IN (CAST(o.x AS DECIMAL(20,0)),NULL)",
                             ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0=CAST(o.x AS DECIMAL(20,0))",
                             ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0 IN (CAST(o.x AS DECIMAL(20,0))+0.5,NULL)",
                             ((1, 0), (2, 0), (3, 0), (4, 0), (5, 0))),
                            ("ABS(d.k)+0 IN (CAST(o.x AS DECIMAL(20,0)),CAST(o.x AS DECIMAL(20,0))+0.5)",
                             ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0=CAST(o.x AS DOUBLE)",
                             ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                            ("ABS(d.k)+0 IN (CAST(o.x AS DOUBLE),NULL)",
                             ((1, 2), (2, 0), (3, 0), (4, 0), (5, 1))),
                        ]:
                            cursor.execute(
                                prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                                + " WHERE " + predicate + ") FROM abs_bounds o ORDER BY o.id"
                            )
                            actual = cursor.fetchall()
                            assert actual == expected, (predicate, actual, expected)

                    cursor.execute("CREATE TABLE bit_bounds(id INT PRIMARY KEY,x BIGINT)")
                    cursor.execute("INSERT INTO bit_bounds VALUES(1,1),(2,2),(3,63),(4,64),(5,NULL)")
                    cursor.execute("INSERT INTO abs_rows VALUES(1001,NULL)")
                    for prefix, source in [
                        ("", "abs_rows d"),
                        ("", "(SELECT id,k FROM abs_rows) d"),
                        ("", "(SELECT DISTINCT id,k FROM abs_rows) d"),
                        ("WITH materialized AS (SELECT DISTINCT id,k FROM abs_rows) ", "materialized d"),
                    ]:
                        for predicate, expected in [
                            ("BIT_COUNT(d.k)=o.x", ((1, 9), (2, 36), (3, 9), (4, 1), (5, 0))),
                            ("o.x=BIT_COUNT(d.k)", ((1, 9), (2, 36), (3, 9), (4, 1), (5, 0))),
                            ("BIT_COUNT(d.k)=o.x OR d.id=1", ((1, 10), (2, 37), (3, 10), (4, 2), (5, 1))),
                            ("BIT_COUNT(d.k) IN (o.x,NULL)", ((1, 9), (2, 36), (3, 9), (4, 1), (5, 0))),
                        ]:
                            cursor.execute(
                                prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                                + " WHERE " + predicate + ") FROM bit_bounds o ORDER BY o.id"
                            )
                            actual = cursor.fetchall()
                            assert actual == expected, (source, predicate, actual, expected)

                    cursor.execute("CREATE TABLE coalesce_bounds(id INT PRIMARY KEY,x BIGINT)")
                    cursor.execute(
                        "INSERT INTO coalesce_bounds VALUES"
                        "(1,0),(2,1),(3,-1),(4,-2147483648),(5,NULL)"
                    )
                    for prefix, source in [
                        ("", "abs_rows d"),
                        ("", "(SELECT id,k FROM abs_rows) d"),
                        ("", "(SELECT DISTINCT id,k FROM abs_rows) d"),
                        ("WITH materialized AS (SELECT DISTINCT id,k FROM abs_rows) ", "materialized d"),
                    ]:
                        for predicate, expected in [
                            ("COALESCE(d.k,0)=o.x", ((1, 2), (2, 1), (3, 1), (4, 1), (5, 0))),
                            ("o.x=COALESCE(d.k,0)", ((1, 2), (2, 1), (3, 1), (4, 1), (5, 0))),
                            ("IFNULL(d.k,0)=o.x", ((1, 2), (2, 1), (3, 1), (4, 1), (5, 0))),
                            ("COALESCE(d.k,0)=o.x OR d.id=1",
                             ((1, 3), (2, 2), (3, 2), (4, 2), (5, 1))),
                            ("COALESCE(d.k,0) IN (o.x,NULL)",
                             ((1, 2), (2, 1), (3, 1), (4, 1), (5, 0))),
                        ]:
                            cursor.execute(
                                prefix + "SELECT o.id,(SELECT COUNT(*) FROM " + source
                                + " WHERE " + predicate + ") FROM coalesce_bounds o ORDER BY o.id"
                            )
                            actual = cursor.fetchall()
                            assert actual == expected, (source, predicate, actual, expected)

                    for index, (column_type, minimum, maximum) in enumerate([
                        ("TINYINT", -128, 127),
                        ("SMALLINT", -32768, 32767),
                        ("MEDIUMINT", -8388608, 8388607),
                        ("INT", -2147483648, 2147483647),
                    ]):
                        table = f"abs_type_{index}"
                        cursor.execute(f"CREATE TABLE {table}(id INT PRIMARY KEY,k {column_type},KEY ix_k(k))")
                        cursor.execute(f"INSERT INTO {table} VALUES(1,{minimum}),(2,{maximum})")
                        cursor.execute("CREATE TABLE IF NOT EXISTS abs_type_bounds(id INT PRIMARY KEY,x BIGINT)")
                        first_bound = 10 + 3 * index
                        cursor.execute(
                            "INSERT INTO abs_type_bounds VALUES(%s,%s),(%s,%s),(%s,%s)",
                            (first_bound, -minimum, first_bound + 1, maximum,
                             first_bound + 2, -minimum + 1),
                        )
                        cursor.execute(
                            f"SELECT o.id,(SELECT COUNT(*) FROM {table} d WHERE ABS(d.k)=o.x) "
                            "FROM abs_type_bounds o WHERE o.id BETWEEN %s AND %s ORDER BY o.id",
                            (first_bound, first_bound + 2),
                        )
                        assert cursor.fetchall() == (
                            (first_bound, 1), (first_bound + 1, 1), (first_bound + 2, 0)
                        ), column_type
                    cursor.execute("CREATE TABLE wide_unsigned_rows(id INT PRIMARY KEY,k BIGINT UNSIGNED)")
                    cursor.execute("INSERT INTO wide_unsigned_rows VALUES"
                                   "(1,9223372036854775807),(2,9223372036854775808),"
                                   "(3,18446744073709551615)")
                    cursor.execute("CREATE TABLE wide_decimal_bounds(id INT PRIMARY KEY,x DECIMAL(21,1))")
                    cursor.execute("INSERT INTO wide_decimal_bounds VALUES"
                                   "(1,9223372036854775807.0),(2,9223372036854775808.0),"
                                   "(3,18446744073709551615.0),(4,9223372036854775808.5),"
                                   "(5,NULL),(6,18446744073709551616.0)")
                    for predicate in ("COALESCE(d.k,0)=o.x", "COALESCE(d.k,0) IN (o.x,o.x+0.5)"):
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM wide_unsigned_rows) d "
                            f"WHERE {predicate}) FROM wide_decimal_bounds o ORDER BY o.id"
                        )
                        assert cursor.fetchall() == ((1, 1), (2, 1), (3, 1), (4, 0), (5, 0), (6, 0)), predicate
                    cursor.execute(
                        "SELECT o.id,(SELECT COUNT(*) FROM "
                        "(SELECT DISTINCT id,k FROM wide_unsigned_rows) d "
                        "WHERE COALESCE(d.k,0)=CAST(o.x AS DOUBLE)) "
                        "FROM wide_decimal_bounds o ORDER BY o.id"
                    )
                    assert cursor.fetchall() == ((1, 2), (2, 2), (3, 1), (4, 2), (5, 0), (6, 1))
                print(f"MySQL {version}: indexed, pass-through, materialized, signed-boundary, and wide unsigned integer expression cases match")
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
