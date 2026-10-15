"""Compare sampled EPSG 4326 line/polygon boundaries with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
The servers and data directories are disposable; only mismatches are printed.
"""

import pathlib
import shutil
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


def result(connection, line, polygon):
    shape = f"ST_GeomFromText('{line}',4326)"
    area = f"ST_GeomFromText('{polygon}',4326)"
    sql = (
        f"SELECT ST_Crosses({shape},{area}),ST_Touches({shape},{area}),"
        f"ST_Within({shape},{area}),ST_Contains({area},{shape}),"
        f"ST_Intersects({shape},{area})"
    )
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchone()


def point_result(connection, coordinates, polygon):
    point = f"ST_GeomFromText('POINT({coordinates})',4326)"
    area = f"ST_GeomFromText('{polygon}',4326)"
    sql = f"SELECT ST_Within({point},{area}),ST_Touches({point},{area}),ST_Intersects({point},{area})"
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchone()


def cases():
    polygons = [
        "POLYGON((0 0,0 4,4 4,4 0,0 0))",
        "POLYGON((0 0,0 3,3 3,3 0,0 0))",
        "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))",
    ]
    for polygon in polygons:
        for base in [0, 1, 3, 4]:
            for delta in [-0.01, -0.001, -0.00001, 0, 0.00001, 0.001, 0.01]:
                latitude = base + delta
                for first, last in [(-1, 5), (-1, 1), (1, 3), (2, 5)]:
                    yield f"LINESTRING({first} {latitude},{last} {latitude})", polygon
    yield (
        "LINESTRING(-1 0,8 0)",
        "MULTIPOLYGON(((0 0,0 3,3 3,3 0,0 0)),((5 -1,5 1,7 1,7 -1,5 -1)))",
    )
    hole = "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))"
    for longitude in (1.00001, 1.001, 2.999, 2.99999):
        yield f"LINESTRING(3 {longitude},1 {longitude})", hole
    reverse_hole = "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,1 3,3 3,3 1,1 1))"
    for longitude in (1.00001, 1.001, 2.999, 2.99999):
        yield f"LINESTRING(1 {longitude},3 {longitude})", reverse_hole

    for latitude_shift in (-40, -20, 0, 20, 40, 60, 70, 80, 85):
        exterior = [(latitude_shift, 0), (latitude_shift, 4),
                    (latitude_shift + 4, 4), (latitude_shift + 4, 0),
                    (latitude_shift, 0)]
        hole = [(latitude_shift + 1, 1), (latitude_shift + 3, 1),
                (latitude_shift + 3, 3), (latitude_shift + 1, 3),
                (latitude_shift + 1, 1)]
        rings = []
        for ring in (exterior, hole):
            rings.append("(" + ",".join(f"{latitude} {longitude}" for latitude, longitude in ring) + ")")
        shifted_polygon = "POLYGON(" + ",".join(rings) + ")"
        longitudes = (2.5, 2.9, 2.99) if latitude_shift == 0 else (1.00001, 1.001, 1.1, 2.5, 2.9, 2.99, 2.999, 2.99999)
        for longitude in longitudes:
            line = f"LINESTRING({latitude_shift + 1} {longitude},{latitude_shift + 3} {longitude})"
            yield line, shifted_polygon


def main():
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")
    root = pathlib.Path(tempfile.mkdtemp(prefix="fsdb-spatial-edge-"))
    data = root / "mysql-data"
    data.mkdir()
    mysql_socket = str(root / "mysql.sock")
    mysql = fsdb = mysql_connection = fsdb_connection = None
    try:
        with (root / "mysql.log").open("w") as log:
            subprocess.run(
                ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                 "--innodb-redo-log-capacity=64M"],
                check=True, stdout=log, stderr=subprocess.STDOUT,
            )
            mysql = subprocess.Popen(
                ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                 f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                 "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                 "--innodb-redo-log-capacity=64M"],
                stdout=log, stderr=subprocess.STDOUT,
            )
            mysql_connection = connect(unix_socket=mysql_socket)
        with mysql_connection.cursor() as cursor:
            cursor.execute("SELECT VERSION()")
            version = cursor.fetchone()[0]
        if not version.startswith("8.4.11"):
            raise RuntimeError(f"Expected MySQL 8.4.11, found {version}")

        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        with (root / "fsdb.log").open("w") as log:
            fsdb = subprocess.Popen(
                ["dotnet", str(FSDB), "--data-dir", str(root / "fsdb-data"),
                 "--port", str(port)],
                stdout=log, stderr=subprocess.STDOUT,
            )
            fsdb_connection = connect(host="127.0.0.1", port=port)

        count = 0
        mismatches = []
        for line, polygon in cases():
            count += 1
            expected = result(mysql_connection, line, polygon)
            actual = result(fsdb_connection, line, polygon)
            if actual != expected:
                mismatches.append((line, polygon, expected, actual))
        print(f"{count} cases; {len(mismatches)} mismatches")
        for mismatch in mismatches:
            print(mismatch)
            line, polygon, _, _ = mismatch
            shape = f"ST_GeomFromText('{line}',4326)"
            area = f"ST_GeomFromText('{polygon}',4326)"
            with mysql_connection.cursor() as cursor:
                cursor.execute(
                    f"SELECT ST_Within(ST_StartPoint({shape}),{area}),"
                    f"ST_Within(ST_EndPoint({shape}),{area}),"
                    f"ST_AsText(ST_Intersection({shape},{area}))"
                )
                print("MySQL intersection", cursor.fetchone())
        hole = "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))"
        point_count = 0
        point_mismatches = []
        for longitude in [1.00001, 1.001, 2.999]:
            for latitude in [1, 1.00001, 1.001, 1.01, 1.1, 1.5, 2, 2.5, 2.9, 2.999, 2.99999, 3, 3.00001]:
                point_count += 1
                coordinates = f"{latitude} {longitude}"
                expected = point_result(mysql_connection, coordinates, hole)
                actual = point_result(fsdb_connection, coordinates, hole)
                if actual != expected:
                    point_mismatches.append((latitude, longitude, expected, actual))
        for latitude_shift in (40, 60, 70, 80, 85):
            polygon = (
                f"POLYGON(({latitude_shift} 0,{latitude_shift} 4,"
                f"{latitude_shift + 4} 4,{latitude_shift + 4} 0,{latitude_shift} 0),"
                f"({latitude_shift + 1} 1,{latitude_shift + 3} 1,"
                f"{latitude_shift + 3} 3,{latitude_shift + 1} 3,{latitude_shift + 1} 1))"
            )
            for latitude in (latitude_shift + 1, latitude_shift + 1.00001,
                             latitude_shift + 2, latitude_shift + 2.99999,
                             latitude_shift + 3):
                for longitude in (0.99999, 1, 1.00001, 2.99999, 3, 3.00001):
                    point_count += 1
                    coordinates = f"{latitude} {longitude}"
                    expected = point_result(mysql_connection, coordinates, polygon)
                    actual = point_result(fsdb_connection, coordinates, polygon)
                    if actual != expected:
                        point_mismatches.append((latitude, longitude, expected, actual))
        print(f"{point_count} near-hole points; {len(point_mismatches)} point mismatches")
        for mismatch in point_mismatches:
            print("point", mismatch)
        exterior = "POLYGON((0 0,0 4,4 4,4 0,0 0))"
        edge_probes = [
            ("0 -0.00000000001", "near west edge"),
            ("2 4.00000000001", "near east edge"),
            ("-0.00000000001 2", "near south edge"),
            ("4.00000000001 2", "near north edge"),
        ]
        edge_mismatches = []
        for coordinates, label in edge_probes:
            expected = point_result(mysql_connection, coordinates, exterior)
            actual = point_result(fsdb_connection, coordinates, exterior)
            if actual != expected:
                edge_mismatches.append((label, coordinates, expected, actual))
        print(f"{len(edge_probes)} near-exterior points; {len(edge_mismatches)} point mismatches")
        for mismatch in edge_mismatches:
            print("exterior point", mismatch)
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
                ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}",
                 "-uroot", "shutdown"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            mysql.wait(timeout=20)
        shutil.rmtree(root)


if __name__ == "__main__":
    main()
