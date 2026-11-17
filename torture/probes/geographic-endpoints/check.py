"""Check shared geographic endpoints and global rings against pinned MySQL."""

import argparse
import importlib.util
import json
import pathlib
import random
import socket
import subprocess
import sys
import tempfile

from polygon_cases import boundary_only_rings, polygon, recorded_components
from polygon_cases import cases as polygon_cases

REPO = pathlib.Path(__file__).resolve().parents[3]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location(
    "validity_oracle", REPO / "torture/scripts/geographic-validity-oracle.py"
)
VALIDITY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDITY)


def line_relation(connection, first, second, srid=4326):
    left = f"ST_GeomFromText('LINESTRING({first})',{srid})"
    right = f"ST_GeomFromText('LINESTRING({second})',{srid})"
    with connection.cursor() as cursor:
        cursor.execute(
            f"SELECT ST_Intersects({left},{right}),ST_Crosses({left},{right}),"
            f"ST_Touches({left},{right}),ST_Distance({left},{right})"
        )
        return cursor.fetchone()


def relation(connection, first, shared, second):
    return line_relation(connection, f"{first},{shared}", f"{shared},{second}")


def check_crossing(mysql, fsdb):
    first = ["13.81883866 96.65582728", "-16.27065705 -81.50116582"]
    second = ["-5.47457294 82.28363800", "57.65336721 169.58817054"]
    results = []
    for left in [first, first[::-1]]:
        for right in [second, second[::-1]]:
            for a, b in [(left, right), (right, left)]:
                a, b = ",".join(a), ",".join(b)
                expected = line_relation(mysql, a, b)
                actual = line_relation(fsdb, a, b)
                assert expected == (1, 1, 0, 0.0), expected
                results.append(
                    {"first": a, "second": b, "mysql": expected, "fsdb": actual}
                )
    print(
        json.dumps(
            {
                "mysql_version": VALIDITY.scalar(mysql, "SELECT VERSION()"),
                "crossings": results,
            },
            indent=2,
        ),
        flush=True,
    )
    assert all(row["mysql"] == row["fsdb"] for row in results), (
        "known nonadjacent crossing still disagrees"
    )


def check_line_pairs(mysql, fsdb, count):
    rng = random.Random(33076)
    differences = []
    for case in range(count):
        points = [
            f"{rng.uniform(-85, 85):.8f} {rng.uniform(-180, 180):.8f}" for _ in range(4)
        ]
        first, second = ",".join(points[:2]), ",".join(points[2:])
        for srid in [4326, 4258, 4267]:
            expected = line_relation(mysql, first, second, srid)
            actual = line_relation(fsdb, first, second, srid)
            if expected[:3] != actual[:3] or (expected[3] == 0) != (actual[3] == 0):
                differences.append(
                    {
                        "case": case,
                        "srid": srid,
                        "first": first,
                        "second": second,
                        "mysql": expected,
                        "fsdb": actual,
                    }
                )
    print(
        json.dumps(
            {
                "mysql_version": VALIDITY.scalar(mysql, "SELECT VERSION()"),
                "seed": 33076,
                "pairs_per_srid": count,
                "srids": [4326, 4258, 4267],
                "differences": differences,
            },
            indent=2,
        ),
        flush=True,
    )
    assert not differences, "line topology or zero-distance classification differs"


def check_polygons(mysql, fsdb, version, seed, srid):
    def relation(connection, sql):
        with connection.cursor() as cursor:
            cursor.execute(sql)
            return cursor.fetchone()

    differences = []
    fixtures = []
    errors = {}
    component_relations = []
    boundary_relations = []
    shell, inscribed = boundary_only_rings()
    for latitude, longitude in inscribed:
        point = f"ST_GeomFromText('POINT({latitude} {longitude})',{srid})"
        area = f"ST_GeomFromText('{polygon([shell])}',{srid})"
        sql = f"SELECT ST_Touches({point},{area}),ST_Within({point},{area})"
        boundary_relations.append(
            {
                "point": [latitude, longitude],
                "mysql": relation(mysql, sql),
                "fsdb": relation(fsdb, sql),
            }
        )
    for name, components in recorded_components():
        first, second = [
            f"ST_GeomFromText('{polygon([points])}',{srid})" for points in components
        ]
        sql = f"SELECT ST_Intersects({first},{second}),ST_Within({second},{first})"

        component_relations.append(
            {"name": name, "mysql": relation(mysql, sql), "fsdb": relation(fsdb, sql)}
        )
    valid = total = 0
    for name, wkt in polygon_cases(seed):
        sql = f"SELECT ST_IsValid(ST_GeomFromText('{wkt}',{srid}))"
        expected = VALIDITY.scalar(mysql, sql)
        valid += expected
        total += 1
        if not name.startswith("global_"):
            fixtures.append({"name": name, "mysql": expected})
        try:
            actual = VALIDITY.scalar(fsdb, sql)
        except VALIDITY.pymysql.MySQLError as error:
            actual = {"error": error.args[0]}
            errors[error.args[0]] = errors.get(error.args[0], 0) + 1
        if expected != actual:
            differences.append(
                {
                    "name": name,
                    "wkt": wkt,
                    "mysql": expected,
                    "fsdb": actual,
                }
            )
    print(
        json.dumps(
            {
                "mysql_version": version,
                "global_seed": seed,
                "srid": srid,
                "cases": total,
                "mysql_valid": valid,
                "difference_count": len(differences),
                "fsdb_error_counts": errors,
                "fixture_results": fixtures,
                "recorded_component_relations": component_relations,
                "boundary_vertex_relations": boundary_relations,
                "difference_examples": differences[:20],
            },
            indent=2,
        ),
        flush=True,
    )
    assert all(row["mysql"] == row["fsdb"] for row in component_relations), (
        "recorded component relations differ"
    )
    assert all(
        row["mysql"] == (1, 0) and row["fsdb"] == (1, 0) for row in boundary_relations
    ), "boundary-only fixture classification changed"
    assert not differences, "geographic polygon validity differs"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--crossing",
        action="store_true",
        help="assert parity for the recorded long-edge crossing",
    )
    mode.add_argument(
        "--line-pairs", type=int, help="compare independent random segments per SRS"
    )
    mode.add_argument(
        "--polygons",
        action="store_true",
        help="compare polygons with holes and multipart validity",
    )
    parser.add_argument(
        "--polygon-seed", type=int, default=9176, help="global polygon corpus seed"
    )
    parser.add_argument(
        "--polygon-srid", type=int, default=4326, help="SRS for polygon validity"
    )
    args = parser.parse_args()
    if args.line_pairs is not None and args.line_pairs < 1:
        parser.error("--line-pairs must be positive")
    address = subprocess.check_output(
        ["docker", "compose", "-f", "torture/compose.yaml", "port", "mysql", "3306"],
        cwd=REPO,
        text=True,
    ).strip()
    mysql = VALIDITY.connect(
        host="127.0.0.1", port=int(address.rsplit(":", 1)[1]), password="torture-secret"
    )
    version = VALIDITY.scalar(mysql, "SELECT VERSION()")
    assert version.startswith("8.4.11"), version
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    with (
        tempfile.TemporaryDirectory(prefix="fsdb-endpoint-oracle-") as directory,
        pathlib.Path(directory, "fsdb.log").open("w") as log,
    ):
        server = subprocess.Popen(
            ["dotnet", str(VALIDITY.FSDB), "--port", str(port)],
            stdout=log,
            stderr=subprocess.STDOUT,
            cwd=REPO,
        )
        fsdb = None
        try:
            fsdb = VALIDITY.connect(host="127.0.0.1", port=port)
            if args.crossing:
                check_crossing(mysql, fsdb)
                return
            if args.line_pairs is not None:
                check_line_pairs(mysql, fsdb, args.line_pairs)
                return
            if args.polygons:
                check_polygons(
                    mysql, fsdb, version, args.polygon_seed, args.polygon_srid
                )
                return
            rng = random.Random(33092)
            endpoint_differences = []
            for case in range(1000):
                points = [
                    f"{rng.uniform(-85, 85):.8f} {rng.uniform(-180, 180):.8f}"
                    for _ in range(3)
                ]
                expected = relation(mysql, *points)
                actual = relation(fsdb, *points)
                if expected != actual:
                    endpoint_differences.append((case, points, expected, actual))
            rings = {}
            for seed in [9173, 9174, 9175]:
                differences = {}
                for case, text in enumerate(VALIDITY.polygon_wkts(1000, seed)):
                    sql = f"SELECT ST_IsValid(ST_GeomFromText('{text}',4326))"
                    expected = VALIDITY.scalar(mysql, sql)
                    actual = VALIDITY.scalar(fsdb, sql)
                    if expected != actual:
                        differences[case] = [expected, actual]
                rings[seed] = differences
            summary = {
                "mysql_version": version,
                "endpoint_seed": 33092,
                "endpoint_cases": 1000,
                "endpoint_differences": endpoint_differences,
                "rings_per_seed": 1000,
                "ring_differences": rings,
            }
            print(json.dumps(summary, indent=2), flush=True)
            assert not endpoint_differences, "new endpoint relation disagreement"
            assert rings == {9173: {}, 9174: {}, 9175: {}}, (
                "global ring mismatch set changed; review against the oracle"
            )
        finally:
            if fsdb is not None:
                fsdb.close()
            mysql.close()
            server.terminate()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()


if __name__ == "__main__":
    main()
