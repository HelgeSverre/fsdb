"""Compare large-radius ST_DISTANCE_SPHERE results with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
Both servers and their data directories are disposable.
"""

import math
import pathlib
import random
import socket
import subprocess
import tempfile
import time

import pymysql


REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def run_statement(connection, sql):
    try:
        with connection.cursor() as cursor:
            cursor.execute(sql)
            return cursor.fetchone()[0]
    except pymysql.MySQLError as error:
        return "error", error.args


def run(connection, first, second, radius, srid=4326):
    sql = (
        "SELECT ST_DISTANCE_SPHERE("
        f"ST_GeomFromText('{first}',{srid}),"
        f"ST_GeomFromText('{second}',{srid}),{radius})"
    )
    return run_statement(connection, sql)


def main():
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")

    with tempfile.TemporaryDirectory(prefix="fsdb-sphere-radius-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (root / "mysql.log").open("w") as mysql_log, (root / "fsdb.log").open("w") as fsdb_log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                     "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                     "--innodb-redo-log-capacity=64M"],
                    stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                with mysql_connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                assert version.startswith("8.4.11"), version

                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port)],
                    stdout=fsdb_log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)

                cases = [
                    ("POINT(0 0)", "POINT(0 0)", "1e308"),
                    ("POINT(0 0)", "POINT(0 1)", "1e308"),
                    ("POINT(0 0)", "POINT(0 180)", "1e307"),
                    ("POINT(0 0)", "POINT(0 180)", "1e308"),
                    ("MULTIPOINT((0 0),(0 180))", "POINT(0 0)", "1e308"),
                    ("MULTIPOINT((0 180),(0 1))", "POINT(0 0)", "1e308"),
                    ("POINT(0 0)", "POINT(0 180)", "5.72e307"),
                    ("POINT(0 0)", "POINT(0 180)", "5.73e307"),
                    ("POINT(0 0)", "POINT(0 179.999999)", "5.72e307"),
                    ("POINT(0 0)", "POINT(0 -179.999999)", "5.72e307"),
                    ("POINT(0 0)", "MULTIPOINT((0 180),(0 1))", "1e308"),
                    ("POINT(0 0)", "POINT(0 0)", "0"),
                    ("POINT(0 0)", "POINT(0 1)", "0"),
                    ("POINT(0 0)", "POINT(0 1)", "-1"),
                    ("POINT(0 0)", "POINT(0 1)", "1e-308"),
                    ("POINT(0 0)", "POINT(0 1)", "1.7976931348623157e308"),
                    ("POINT(0 0)", "POINT(0 1)", "-1e309"),
                    ("POINT(0 0)", "POINT(0 0)", "1e309"),
                    ("POINT(0 0)", "POINT(0 1)", "1e309"),
                ]
                for first in ("POINT(0 0)", "POINT(89.999999 0)", "POINT(-89.999999 0)"):
                    for second in (
                        "POINT(0 179.999999)", "POINT(0 -179.999999)",
                        "POINT(89.999999 179.999999)", "POINT(-89.999999 -179.999999)",
                        "POINT(0 0.000001)",
                    ):
                        for radius in ("1e300", "5e307", "5.7e307", "1e308"):
                            cases.append((first, second, radius))
                random_cases = random.Random(20261018)
                for _ in range(240):
                    near_pole = random_cases.random() < 0.5
                    if near_pole:
                        pole = 1 if random_cases.random() < 0.5 else -1
                        first_latitude = pole * (90 - 10 ** random_cases.uniform(-8, -3))
                        second_latitude = pole * (90 - 10 ** random_cases.uniform(-8, -3))
                        first_longitude = random_cases.uniform(-180, 180)
                        second_longitude = ((-first_longitude + random_cases.uniform(-0.01, 0.01) + 180) % 360) - 180
                    else:
                        first_latitude = random_cases.uniform(-89.9, 89.9)
                        second_latitude = random_cases.uniform(-89.9, 89.9)
                        first_longitude = random_cases.uniform(-180, 180)
                        second_longitude = random_cases.uniform(-180, 180)
                    first = f"POINT({first_latitude:.12f} {first_longitude:.12f})"
                    second = f"POINT({second_latitude:.12f} {second_longitude:.12f})"
                    cases.append((first, second, "1e300"))
                for first, second, radius in cases:
                    expected = run(mysql_connection, first, second, radius)
                    actual = run(fsdb_connection, first, second, radius)
                    if isinstance(expected, tuple):
                        assert actual == expected, (first, second, radius, expected, actual)
                    else:
                        assert math.isclose(actual, expected, rel_tol=1e-13, abs_tol=0), (
                            first, second, radius, expected, actual
                        )
                geographic_pairs = (
                    ("POINT(89.999999 0)", "POINT(89.999999 179.999999)"),
                    ("POINT(-89.999999 0)", "POINT(-89.999999 -179.999999)"),
                    ("POINT(0 0)", "POINT(0 179.999999)"),
                )
                srid_zero_pairs = (
                    ("POINT(0 89.999999)", "POINT(179.999999 89.999999)"),
                    ("POINT(0 -89.999999)", "POINT(-179.999999 -89.999999)"),
                    ("POINT(0 0)", "POINT(179.999999 0)"),
                )
                srs_cases = (
                    [(0, first, second, "1e300") for first, second in srid_zero_pairs]
                    + [(srid, first, second, "1e300")
                       for srid in (4030, 4258, 4269)
                       for first, second in geographic_pairs]
                )
                for srid, first, second, radius in srs_cases:
                    expected = run(mysql_connection, first, second, radius, srid)
                    actual = run(fsdb_connection, first, second, radius, srid)
                    assert math.isclose(actual, expected, rel_tol=1e-13, abs_tol=0), (
                        srid, first, second, radius, expected, actual
                    )
                for sql in ("SELECT 1e309", "SELECT -1e309"):
                    expected = run_statement(mysql_connection, sql)
                    actual = run_statement(fsdb_connection, sql)
                    assert expected == actual == (
                        "error", (1367, "Illegal double '1e309' value found during parsing")
                    ), (sql, expected, actual)
                print(f"MySQL {version}: {len(cases)} WGS84 radius cases, {len(srs_cases)} other-SRS cases, "
                      "and two bare literal errors matched")
            finally:
                if fsdb_connection:
                    fsdb_connection.close()
                if mysql_connection:
                    mysql_connection.close()
                if fsdb and fsdb.poll() is None:
                    fsdb.terminate()
                    fsdb.wait(timeout=20)
                if mysql and mysql.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    main()
