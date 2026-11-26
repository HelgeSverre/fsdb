"""Compare geographic line/polygon boundaries with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
The servers and data directories are disposable; only mismatches are printed.
"""

import argparse
import itertools
import pathlib
import random
import re
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


def result(connection, line, polygon, srid=4326):
    shape = f"ST_GeomFromText('{line}',{srid})"
    area = f"ST_GeomFromText('{polygon}',{srid})"
    sql = (
        f"SELECT ST_Crosses({shape},{area}),ST_Touches({shape},{area}),"
        f"ST_Within({shape},{area}),ST_Contains({area},{shape}),"
        f"ST_Intersects({shape},{area})"
    )
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchone()


def overlay_text(connection, operation, left, right, srid):
    first = f"ST_GeomFromText('{left}',{srid})"
    second = f"ST_GeomFromText('{right}',{srid})"
    with connection.cursor() as cursor:
        try:
            cursor.execute(f"SELECT ST_AsText(ST_{operation}({first},{second}))")
            return cursor.fetchone()[0]
        except pymysql.err.Error as error:
            return ("error", error.args[0])


COORDINATE = re.compile(r"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?")


def same_overlay(expected, actual):
    if not isinstance(expected, str) or not isinstance(actual, str):
        return expected == actual
    if COORDINATE.sub("#", expected) != COORDINATE.sub("#", actual):
        return False
    return all(
        abs(float(left) - float(right)) <= 1e-8
        for left, right in zip(
            COORDINATE.findall(expected), COORDINATE.findall(actual), strict=True
        )
    )


def check_line_area_overlays(mysql, fsdb, line_cases, srid):
    cases = list(line_cases)
    failures = 0
    for operation in ("Intersection", "Difference", "Union", "SymDifference"):
        mismatches = []
        for line, polygon in cases:
            for left, right in ((line, polygon), (polygon, line)):
                expected = overlay_text(mysql, operation, left, right, srid)
                actual = overlay_text(fsdb, operation, left, right, srid)
                if not same_overlay(expected, actual):
                    mismatches.append((left, right, expected, actual))
        failures += len(mismatches)
        print(f"ST_{operation}: {len(cases) * 2} results; {len(mismatches)} mismatches")
        for mismatch in mismatches:
            print(mismatch)
    return int(failures != 0)


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


def hole_line_distance(connection, coordinates, polygon):
    hole = polygon.split("),(", 1)[1].removesuffix("))")
    point = f"ST_GeomFromText('POINT({coordinates})',4326)"
    line = f"ST_GeomFromText('LINESTRING({hole})',4326)"
    with connection.cursor() as cursor:
        cursor.execute(f"SELECT ST_Distance({point},{line})")
        return cursor.fetchone()[0]


def hole_segment_distances(connection, coordinates, polygon):
    hole = polygon.split("),(", 1)[1].removesuffix("))")
    vertices = hole.split(",")
    point = f"ST_GeomFromText('POINT({coordinates})',4326)"
    expressions = [
        f"ST_Distance({point},ST_GeomFromText('LINESTRING({start},{end})',4326))"
        for start, end in itertools.pairwise(vertices)
    ]
    with connection.cursor() as cursor:
        cursor.execute("SELECT " + ",".join(expressions))
        return cursor.fetchone()


def point_line_distance(connection, coordinates, line):
    point = f"ST_GeomFromText('POINT({coordinates})',4326)"
    line = f"ST_GeomFromText('{line}',4326)"
    with connection.cursor() as cursor:
        cursor.execute(f"SELECT ST_Distance({point},{line})")
        return cursor.fetchone()[0]


def point_pair_distance(connection, first, second):
    point = f"ST_GeomFromText('POINT({first})',4326)"
    other = f"ST_GeomFromText('POINT({second})',4326)"
    with connection.cursor() as cursor:
        cursor.execute(f"SELECT ST_Distance({point},{other})")
        return cursor.fetchone()[0]


def near_edge_newton_control(connection, distance):
    line = "ST_GeomFromText('LINESTRING(3 1,3 3)',4326)"
    point = "ST_GeomFromText('POINT(3 1.00001)',4326)"
    projected = f"ST_PointAtDistance({line},{distance})"
    with connection.cursor() as cursor:
        cursor.execute(
            f"SELECT ST_X({projected}),ST_Y({projected}),ST_Distance({point},{projected})"
        )
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
    for latitude_shift in (0, 40, 70, 85):
        polygon = (
            f"POLYGON(({latitude_shift} 0,{latitude_shift} 4,"
            f"{latitude_shift + 4} 4,{latitude_shift + 4} 0,{latitude_shift} 0))"
        )
        for delta in (0.01, 0.1, 1.0):
            for excess in (0.000001, 0.0001, 0.01):
                near = delta + excess
                line = f"LINESTRING({latitude_shift - delta} {near},{latitude_shift + near} {-delta})"
                yield line, polygon
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
        exterior = [
            (latitude_shift, 0),
            (latitude_shift, 4),
            (latitude_shift + 4, 4),
            (latitude_shift + 4, 0),
            (latitude_shift, 0),
        ]
        hole = [
            (latitude_shift + 1, 1),
            (latitude_shift + 3, 1),
            (latitude_shift + 3, 3),
            (latitude_shift + 1, 3),
            (latitude_shift + 1, 1),
        ]
        rings = []
        for ring in (exterior, hole):
            rings.append(
                "("
                + ",".join(f"{latitude} {longitude}" for latitude, longitude in ring)
                + ")"
            )
        shifted_polygon = "POLYGON(" + ",".join(rings) + ")"
        longitudes = (
            (2.5, 2.9, 2.99)
            if latitude_shift == 0
            else (1.00001, 1.001, 1.1, 2.5, 2.9, 2.99, 2.999, 2.99999)
        )
        for longitude in longitudes:
            line = f"LINESTRING({latitude_shift + 1} {longitude},{latitude_shift + 3} {longitude})"
            yield line, shifted_polygon


def reverse_line(line):
    points = line.removeprefix("LINESTRING(").removesuffix(")")
    return "LINESTRING(" + ",".join(reversed(points.split(","))) + ")"


def seeded_relation_cases(seed=148731, polygon_count=500):
    def coordinates(origin, size, vertices):
        latitude, longitude = origin
        height, width = size
        return (
            "("
            + ",".join(
                f"{latitude + height * x:.14f} {longitude + width * y:.14f}"
                for x, y in vertices
            )
            + ")"
        )

    rng = random.Random(seed)
    for _ in range(polygon_count):
        origin = rng.uniform(-76, 76), rng.uniform(-174, 174)
        size = rng.uniform(0.04, 3.5), rng.uniform(0.04, 3.5)
        outer = coordinates(origin, size, ((0, 0), (0, 1), (1, 1), (1, 0), (0, 0)))
        inner = coordinates(
            origin,
            size,
            ((0.25, 0.25), (0.75, 0.25), (0.75, 0.75), (0.25, 0.75), (0.25, 0.25)),
        )
        polygon = f"POLYGON({outer},{inner})"

        random_segment = [
            (rng.uniform(-0.8, 1.8), rng.uniform(-0.8, 1.8)) for _ in range(2)
        ]
        yield f"LINESTRING{coordinates(origin, size, random_segment)}", polygon

        epsilon = rng.choice([-1, 1]) * rng.choice([1e-7, 1e-5, 1e-3, 0.03])
        longitude_fraction = rng.choice([0.25, 0.75, 0.5]) + epsilon
        near_hole = [(0.25, longitude_fraction), (0.75, longitude_fraction)]
        if rng.randrange(2):
            near_hole.reverse()
        yield f"LINESTRING{coordinates(origin, size, near_hole)}", polygon


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--trace-first-line-mismatch",
        action="store_true",
        help="compare point predicates near the first mismatching line endpoint",
    )
    parser.add_argument(
        "--compare-overlays",
        action="store_true",
        help="show both engines' intersection geometry for mismatching lines",
    )
    parser.add_argument(
        "--check-overlays",
        action="store_true",
        help="compare all four overlays in both operand orders, using a 1e-8 degree tolerance",
    )
    parser.add_argument(
        "--reverse-lines",
        action="store_true",
        help="reverse each source line before checking its directed relations",
    )
    parser.add_argument(
        "--seeded-relations",
        action="store_true",
        help="compare seeded lines against polygons with holes",
    )
    parser.add_argument(
        "--seed", type=int, default=148731, help="seed for the line relation fixture"
    )
    parser.add_argument(
        "--seeded-polygons",
        type=int,
        default=500,
        help="number of polygons in the seeded fixture (two lines per polygon)",
    )
    parser.add_argument(
        "--srid",
        type=int,
        default=4326,
        help="SRID for the line relation cases (point controls use EPSG 4326)",
    )
    args = parser.parse_args()
    if args.seeded_polygons < 1:
        parser.error("--seeded-polygons must be positive")
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
                [
                    "mysqld",
                    "--no-defaults",
                    "--initialize-insecure",
                    f"--datadir={data}",
                    "--innodb-redo-log-capacity=64M",
                ],
                check=True,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            mysql = subprocess.Popen(
                [
                    "mysqld",
                    "--no-defaults",
                    f"--datadir={data}",
                    "--skip-networking",
                    f"--socket={mysql_socket}",
                    f"--pid-file={root / 'mysql.pid'}",
                    "--mysqlx=0",
                    "--innodb-buffer-pool-size=64M",
                    "--innodb-redo-log-capacity=64M",
                ],
                stdout=log,
                stderr=subprocess.STDOUT,
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
                [
                    "dotnet",
                    str(FSDB),
                    "--data-dir",
                    str(root / "fsdb-data"),
                    "--port",
                    str(port),
                ],
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            fsdb_connection = connect(host="127.0.0.1", port=port)

        count = 0
        mismatches = []
        line_cases = (
            seeded_relation_cases(args.seed, args.seeded_polygons)
            if args.seeded_relations
            else cases()
        )
        if args.check_overlays:
            if args.reverse_lines:
                line_cases = (
                    (reverse_line(line), polygon) for line, polygon in line_cases
                )
            return check_line_area_overlays(
                mysql_connection, fsdb_connection, line_cases, args.srid
            )
        for line, polygon in line_cases:
            if args.reverse_lines:
                line = reverse_line(line)
            count += 1
            expected = result(mysql_connection, line, polygon, args.srid)
            actual = result(fsdb_connection, line, polygon, args.srid)
            if actual != expected:
                mismatches.append((line, polygon, expected, actual))
        print(f"{count} cases; {len(mismatches)} mismatches")
        for mismatch in mismatches:
            print(mismatch)
            line, polygon, _, _ = mismatch
            shape = f"ST_GeomFromText('{line}',{args.srid})"
            area = f"ST_GeomFromText('{polygon}',{args.srid})"
            if args.trace_first_line_mismatch and mismatch == mismatches[0]:
                for label, connection in (
                    ("MySQL", mysql_connection),
                    ("fsdb", fsdb_connection),
                ):
                    for fraction in (0, 1e-9, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 0.001):
                        point = f"ST_LineInterpolatePoint({shape},{fraction})"
                        with connection.cursor() as cursor:
                            cursor.execute(
                                f"SELECT ST_AsText({point}),ST_Within({point},{area}),"
                                f"ST_Touches({point},{area})"
                            )
                            print("endpoint trace", label, fraction, cursor.fetchone())
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
                if args.compare_overlays:
                    try:
                        cursor.execute(
                            f"SELECT ST_AsText(ST_Intersection({shape},{area}))"
                        )
                        print("fsdb intersection", cursor.fetchone())
                    except pymysql.MySQLError as error:
                        print("fsdb intersection error", error.args)
        hole = "POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))"
        point_count = 0
        point_mismatches = []
        point_distance_mismatches = []

        def check_point_distance(coordinates, polygon):
            expected = point_distance(mysql_connection, coordinates, polygon)
            actual = point_distance(fsdb_connection, coordinates, polygon)
            if abs(expected - actual) >= 0.01:
                point_distance_mismatches.append(
                    (coordinates, polygon, expected, actual)
                )

        for longitude in [1.00001, 1.001, 2.999]:
            for latitude in [
                1,
                1.00001,
                1.001,
                1.01,
                1.1,
                1.5,
                2,
                2.5,
                2.9,
                2.999,
                2.99999,
                3,
                3.00001,
            ]:
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
            for latitude in (
                latitude_shift + 1,
                latitude_shift + 1.00001,
                latitude_shift + 2,
                latitude_shift + 2.99999,
                latitude_shift + 3,
            ):
                for longitude in (0.99999, 1, 1.00001, 2.99999, 3, 3.00001):
                    point_count += 1
                    coordinates = f"{latitude} {longitude}"
                    expected = point_result(mysql_connection, coordinates, polygon)
                    actual = point_result(fsdb_connection, coordinates, polygon)
                    if actual != expected:
                        point_mismatches.append((latitude, longitude, expected, actual))
                    check_point_distance(coordinates, polygon)
        print(
            f"{point_count} near-hole points; {len(point_mismatches)} point mismatches"
        )
        print(
            f"{point_count} near-hole point distances; {len(point_distance_mismatches)} mismatches"
        )
        matching_hole_distances = 0
        matching_nearest_segments = 0
        for mismatch in point_distance_mismatches:
            print("point distance", mismatch)
            coordinates, polygon, expected, actual = mismatch
            mysql_hole_distance = hole_line_distance(
                mysql_connection, coordinates, polygon
            )
            fsdb_hole_distance = hole_line_distance(
                fsdb_connection, coordinates, polygon
            )
            if (
                abs(expected - mysql_hole_distance) < 1e-9
                and abs(actual - fsdb_hole_distance) < 1e-9
            ):
                matching_hole_distances += 1
            print(
                "hole line distance",
                coordinates,
                mysql_hole_distance,
                fsdb_hole_distance,
            )
            mysql_segments = hole_segment_distances(
                mysql_connection, coordinates, polygon
            )
            fsdb_segments = hole_segment_distances(
                fsdb_connection, coordinates, polygon
            )
            mysql_nearest = mysql_segments.index(min(mysql_segments))
            fsdb_nearest = fsdb_segments.index(min(fsdb_segments))
            matching_nearest_segments += mysql_nearest == fsdb_nearest
            print("nearest hole segment", coordinates, mysql_nearest, fsdb_nearest)
            if mysql_nearest != fsdb_nearest:
                print("segment distances", mysql_segments, fsdb_segments)
            hole_vertices = polygon.split("),(", 1)[1].removesuffix("))").split(",")
            for vertex in hole_vertices[:-1]:
                mysql_vertex = point_pair_distance(
                    mysql_connection, coordinates, vertex
                )
                fsdb_vertex = point_pair_distance(fsdb_connection, coordinates, vertex)
                if abs(mysql_vertex - fsdb_vertex) >= 0.01:
                    print(
                        "vertex distance",
                        coordinates,
                        vertex,
                        mysql_vertex,
                        fsdb_vertex,
                    )
        print(
            f"{matching_hole_distances}/{len(point_distance_mismatches)} polygon distances equal hole-line distances in both engines"
        )
        print(
            f"{matching_nearest_segments}/{len(point_distance_mismatches)} nearest hole segments agree"
        )
        for mismatch in point_mismatches:
            print("point", mismatch)
        polar_exterior = "POLYGON((85 0,85 4,89 4,89 0,85 0))"
        polar_hole = "POLYGON((86 1,88 1,88 3,86 3,86 1))"
        polar_hole_reversed = "POLYGON((86 1,86 3,88 3,88 1,86 1))"
        polar_combined = (
            "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,88 1,88 3,86 3,86 1))"
        )
        polar_combined_reversed = (
            "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,86 3,88 3,88 1,86 1))"
        )
        with mysql_connection.cursor() as cursor:
            for label, ring in (
                ("exterior", polar_exterior),
                ("hole", polar_hole),
                ("hole reversed", polar_hole_reversed),
                ("combined", polar_combined),
                ("combined reversed", polar_combined_reversed),
            ):
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
            for label, ring in (
                ("exterior", polar_exterior),
                ("hole", polar_hole),
                ("hole reversed", polar_hole_reversed),
                ("combined", polar_combined),
                ("combined reversed", polar_combined_reversed),
            ):
                expected = point_result(mysql_connection, coordinates, ring)
                assert expected == polar_expected[(coordinates, label)], (
                    coordinates,
                    label,
                    expected,
                )
                mysql_distance = point_distance(mysql_connection, coordinates, ring)
                assert mysql_distance == 0.0, (coordinates, label, mysql_distance)
                print(
                    "polar ring",
                    coordinates,
                    label,
                    expected,
                    point_result(fsdb_connection, coordinates, ring),
                    "distance",
                    mysql_distance,
                    point_distance(fsdb_connection, coordinates, ring),
                )
        for coordinates in ("88 2.99999", "88 1.00001"):
            for label, ring in (
                ("hole", polar_hole),
                ("hole reversed", polar_hole_reversed),
                ("combined", polar_combined),
                ("combined reversed", polar_combined_reversed),
            ):
                expected = point_result(mysql_connection, coordinates, ring)
                assert expected == (
                    (1, 0, 1) if label.startswith("hole") else (0, 0, 0)
                ), (coordinates, label, expected)
                print(
                    "polar residual ring",
                    coordinates,
                    label,
                    expected,
                    point_result(fsdb_connection, coordinates, ring),
                )
        polar_grid_differences = []
        for latitude in (85.99999, 86, 86.00001, 87, 87.99999, 88, 88.00001):
            for longitude in (0.99999, 1, 1.00001, 2, 2.99999, 3, 3.00001):
                coordinates = f"{latitude} {longitude}"
                mysql_position = point_result(
                    mysql_connection, coordinates, polar_combined
                )
                fsdb_position = point_result(
                    fsdb_connection, coordinates, polar_combined
                )
                if mysql_position != fsdb_position:
                    polar_grid_differences.append(
                        (coordinates, mysql_position, fsdb_position)
                    )
        print(
            "polar combined-grid differences",
            len(polar_grid_differences),
            polar_grid_differences,
        )
        polar_corner_differences = []
        for latitude in (85.9999, 85.99999, 86, 86.00001, 86.0001):
            for offset in (
                0.000001,
                0.000002,
                0.000005,
                0.00001,
                0.00002,
                0.00005,
                0.0001,
            ):
                coordinates = f"{latitude} {1 + offset}"
                mysql_position = point_result(
                    mysql_connection, coordinates, polar_combined
                )
                fsdb_position = point_result(
                    fsdb_connection, coordinates, polar_combined
                )
                if mysql_position != fsdb_position:
                    polar_corner_differences.append(
                        (coordinates, mysql_position, fsdb_position)
                    )
        print(
            "polar corner differences",
            len(polar_corner_differences),
            polar_corner_differences,
        )
        # Resolve the near-polar corner more finely before changing the
        # geographic winding rule. The broad point corpus samples only one
        # offset on each side of this boundary.
        polar_polygon = "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,88 1,88 3,86 3,86 1))"
        polar_latitudes = (
            85.99999,
            86,
            86.0000001,
            86.000001,
            86.00001,
            86.0001,
            86.001,
        )
        polar_longitudes = (0.99999, 1, 1.0000001, 1.000001, 1.00001, 1.0001, 1.001)
        polar_mismatches = []
        for latitude in polar_latitudes:
            for longitude in polar_longitudes:
                coordinates = f"{latitude} {longitude}"
                expected = point_result(mysql_connection, coordinates, polar_polygon)
                actual = point_result(fsdb_connection, coordinates, polar_polygon)
                if actual != expected:
                    polar_mismatches.append((coordinates, expected, actual))
        print(
            f"{len(polar_latitudes) * len(polar_longitudes)} polar-corner points; "
            f"{len(polar_mismatches)} point mismatches"
        )
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
            return (
                "("
                + ",".join(f"{latitude} {longitude}" for latitude, longitude in points)
                + ")"
            )

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
                print(
                    "polar orientation",
                    reverse_outer,
                    reverse_inner,
                    "MySQL valid",
                    valid,
                    "MySQL",
                    values[0],
                    "fsdb",
                    values[1],
                )
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
        print(
            f"{len(edge_probes)} near-exterior points; {len(edge_mismatches)} point mismatches"
        )
        for mismatch in edge_mismatches:
            print("exterior point", mismatch)
        rng = random.Random(42)
        segment_mismatches = []
        for _ in range(100):
            first_latitude = rng.uniform(-75, 75)
            first_longitude = rng.uniform(-160, 160)
            last_latitude = first_latitude + rng.uniform(-10, 10)
            last_longitude = first_longitude + rng.uniform(-10, 10)
            point_latitude = (first_latitude + last_latitude) / 2 + rng.uniform(-1, 1)
            point_longitude = (first_longitude + last_longitude) / 2 + rng.uniform(
                -1, 1
            )
            coordinates = f"{point_latitude:.9f} {point_longitude:.9f}"
            line = (
                f"LINESTRING({first_latitude:.9f} {first_longitude:.9f},"
                f"{last_latitude:.9f} {last_longitude:.9f})"
            )
            expected = point_line_distance(mysql_connection, coordinates, line)
            actual = point_line_distance(fsdb_connection, coordinates, line)
            if abs(expected - actual) >= 0.01:
                segment_mismatches.append((coordinates, line, expected, actual))
        print(f"100 seeded point-line distances; {len(segment_mismatches)} mismatches")
        for mismatch in segment_mismatches[:10]:
            print("sampled point-line distance", mismatch)
        for offset in (0.000001, 0.000002, 0.000005, 0.00001, 0.00002, 0.00005, 0.0001):
            point = f"3 {1 + offset:.8f}"
            line = "LINESTRING(3 1,3 3)"
            print(
                "near-latitude segment",
                point,
                point_line_distance(mysql_connection, point, line),
                point_line_distance(fsdb_connection, point, line),
            )
        initial_distance = 1.1164968068957286
        print(
            "near-edge first Newton point",
            initial_distance,
            near_edge_newton_control(mysql_connection, initial_distance),
            near_edge_newton_control(fsdb_connection, initial_distance),
        )
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
                [
                    "mysqladmin",
                    "--no-defaults",
                    f"--socket={mysql_socket}",
                    "-uroot",
                    "shutdown",
                ],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            mysql.wait(timeout=20)
        shutil.rmtree(root)


if __name__ == "__main__":
    raise SystemExit(main())
