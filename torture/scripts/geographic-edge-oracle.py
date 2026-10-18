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


def point_distance(connection, coordinates, polygon):
    point = f"ST_GeomFromText('POINT({coordinates})',4326)"
    area = f"ST_GeomFromText('{polygon}',4326)"
    with connection.cursor() as cursor:
        cursor.execute(f"SELECT ST_Distance({point},{area})")
        return cursor.fetchone()[0]


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
                    f"ST_AsText(ST_Intersection({shape},{area})),"
                    f"ST_Distance({shape},{area})"
                )
                print("MySQL endpoint/intersection/distance", cursor.fetchone())
            with fsdb_connection.cursor() as cursor:
                cursor.execute(f"SELECT ST_Distance({shape},{area})")
                print("fsdb distance", cursor.fetchone())
        hole = "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))"
        point_count = 0
        point_mismatches = []
        point_distance_mismatches = []
        def check_point_distance(coordinates, polygon):
            expected = point_distance(mysql_connection, coordinates, polygon)
            actual = point_distance(fsdb_connection, coordinates, polygon)
            if abs(expected - actual) >= 0.01:
                point_distance_mismatches.append((coordinates, polygon, expected, actual))
        for longitude in [1.00001, 1.001, 2.999]:
            for latitude in [1, 1.00001, 1.001, 1.01, 1.1, 1.5, 2, 2.5, 2.9, 2.999, 2.99999, 3, 3.00001]:
                point_count += 1
                coordinates = f"{latitude} {longitude}"
                expected = point_result(mysql_connection, coordinates, hole)
                actual = point_result(fsdb_connection, coordinates, hole)
                if actual != expected:
                    point_mismatches.append((latitude, longitude, expected, actual))
                check_point_distance(coordinates, hole)
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
                    check_point_distance(coordinates, polygon)
        print(f"{point_count} near-hole points; {len(point_mismatches)} point mismatches")
        print(f"{point_count} near-hole point distances; {len(point_distance_mismatches)} mismatches")
        for mismatch in point_distance_mismatches:
            print("point distance", mismatch)
        for mismatch in point_mismatches:
            print("point", mismatch)
        polar_exterior = "POLYGON((85 0,85 4,89 4,89 0,85 0))"
        polar_hole = "POLYGON((86 1,88 1,88 3,86 3,86 1))"
        polar_hole_reversed = "POLYGON((86 1,86 3,88 3,88 1,86 1))"
        polar_combined = "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,88 1,88 3,86 3,86 1))"
        polar_combined_reversed = "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,86 3,88 3,88 1,86 1))"
        with mysql_connection.cursor() as cursor:
            for label, ring in (("exterior", polar_exterior), ("hole", polar_hole),
                                ("hole reversed", polar_hole_reversed),
                                ("combined", polar_combined), ("combined reversed", polar_combined_reversed)):
                cursor.execute(f"SELECT ST_IsValid(ST_GeomFromText('{ring}',4326))")
                validity = cursor.fetchone()
                assert validity == (1,), (label, validity)
                print("polar validity", label, validity)
        polar_expected = {
            ("86 1.00001", "exterior"): (1, 0, 1),
            ("86 1.00001", "hole"): (0, 0, 0),
            ("86 1.00001", "hole reversed"): (0, 0, 0),
            ("86 1.00001", "combined"): (0, 0, 0),
            ("86 1.00001", "combined reversed"): (0, 0, 0),
            ("86 2.99999", "exterior"): (1, 0, 1),
            ("86 2.99999", "hole"): (1, 0, 1),
            ("86 2.99999", "hole reversed"): (1, 0, 1),
            ("86 2.99999", "combined"): (1, 0, 1),
            ("86 2.99999", "combined reversed"): (1, 0, 1),
        }
        for coordinates in ("86 1.00001", "86 2.99999"):
            for label, ring in (("exterior", polar_exterior), ("hole", polar_hole),
                                ("hole reversed", polar_hole_reversed),
                                ("combined", polar_combined),
                                ("combined reversed", polar_combined_reversed)):
                expected = point_result(mysql_connection, coordinates, ring)
                assert expected == polar_expected[(coordinates, label)], (coordinates, label, expected)
                mysql_distance = point_distance(mysql_connection, coordinates, ring)
                assert mysql_distance == 0.0, (coordinates, label, mysql_distance)
                print(
                    "polar ring", coordinates, label,
                    expected,
                    point_result(fsdb_connection, coordinates, ring),
                    "distance",
                    mysql_distance,
                    point_distance(fsdb_connection, coordinates, ring),
                )
        polar_grid_differences = []
        for latitude in (85.99999, 86, 86.00001, 87, 87.99999, 88, 88.00001):
            for longitude in (0.99999, 1, 1.00001, 2, 2.99999, 3, 3.00001):
                coordinates = f"{latitude} {longitude}"
                mysql_position = point_result(mysql_connection, coordinates, polar_combined)
                fsdb_position = point_result(fsdb_connection, coordinates, polar_combined)
                if mysql_position != fsdb_position:
                    polar_grid_differences.append((coordinates, mysql_position, fsdb_position))
        print("polar combined-grid differences", len(polar_grid_differences), polar_grid_differences)
        polar_corner_differences = []
        for latitude in (85.9999, 85.99999, 86, 86.00001, 86.0001):
            for offset in (0.000001, 0.000002, 0.000005, 0.00001, 0.00002, 0.00005, 0.0001):
                coordinates = f"{latitude} {1 + offset}"
                mysql_position = point_result(mysql_connection, coordinates, polar_combined)
                fsdb_position = point_result(fsdb_connection, coordinates, polar_combined)
                if mysql_position != fsdb_position:
                    polar_corner_differences.append((coordinates, mysql_position, fsdb_position))
        print("polar corner differences", len(polar_corner_differences), polar_corner_differences)
        # Resolve the near-polar corner more finely before changing the
        # geographic winding rule. The broad point corpus samples only one
        # offset on each side of this boundary.
        polar_polygon = (
            "POLYGON((85 0,85 4,89 4,89 0,85 0),"
            "(86 1,88 1,88 3,86 3,86 1))"
        )
        polar_latitudes = (85.99999, 86, 86.0000001, 86.000001, 86.00001, 86.0001, 86.001)
        polar_longitudes = (0.99999, 1, 1.0000001, 1.000001, 1.00001, 1.0001, 1.001)
        polar_mismatches = []
        for latitude in polar_latitudes:
            for longitude in polar_longitudes:
                coordinates = f"{latitude} {longitude}"
                expected = point_result(mysql_connection, coordinates, polar_polygon)
                actual = point_result(fsdb_connection, coordinates, polar_polygon)
                if actual != expected:
                    polar_mismatches.append((coordinates, expected, actual))
        print(f"{len(polar_latitudes) * len(polar_longitudes)} polar-corner points; "
              f"{len(polar_mismatches)} point mismatches")
        for mismatch in polar_mismatches:
            print("polar corner", mismatch)
        # The near-polar mismatch is not explained by classifying each ring as
        # an independent polygon: both engines agree on those two predicates.
        polar_point = "ST_GeomFromText('POINT(86 1.00001)',4326)"
        polar_components = {
            "outer": "POLYGON((85 0,85 4,89 4,89 0,85 0))",
            "hole": "POLYGON((86 1,88 1,88 3,86 3,86 1))",
            "combined": "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,88 1,88 3,86 3,86 1))",
        }
        for name, polygon in polar_components.items():
            area = f"ST_GeomFromText('{polygon}',4326)"
            sql = (
                f"SELECT ST_Within({polar_point},{area}),ST_Touches({polar_point},{area}),"
                f"ST_Distance({polar_point},ST_ExteriorRing({area}))"
            )
            values = []
            for connection in (mysql_connection, fsdb_connection):
                with connection.cursor() as cursor:
                    cursor.execute(sql)
                    values.append(cursor.fetchone())
            print("polar component", name, "MySQL", values[0], "fsdb", values[1])
        outer = [(85, 0), (85, 4), (89, 4), (89, 0), (85, 0)]
        inner = [(86, 1), (88, 1), (88, 3), (86, 3), (86, 1)]

        def polar_ring(points):
            return "(" + ",".join(f"{latitude} {longitude}" for latitude, longitude in points) + ")"

        for reverse_outer in (False, True):
            for reverse_inner in (False, True):
                shell = list(reversed(outer)) if reverse_outer else outer
                hole = list(reversed(inner)) if reverse_inner else inner
                polygon = "POLYGON(" + polar_ring(shell) + "," + polar_ring(hole) + ")"
                area = f"ST_GeomFromText('{polygon}',4326)"
                sql = (
                    f"SELECT ST_Within({polar_point},{area}),"
                    f"ST_Touches({polar_point},{area}),ST_Intersects({polar_point},{area}),"
                    f"ST_Distance({polar_point},{area})"
                )
                values = []
                for connection in (mysql_connection, fsdb_connection):
                    with connection.cursor() as cursor:
                        cursor.execute(sql)
                        values.append(cursor.fetchone())
                with mysql_connection.cursor() as cursor:
                    cursor.execute(f"SELECT ST_IsValid({area})")
                    valid = cursor.fetchone()[0]
                print("polar orientation", reverse_outer, reverse_inner, "MySQL valid", valid,
                      "MySQL", values[0], "fsdb", values[1])
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
