"""Geographic validity fixtures, contacts, and deterministic global polygons."""

import random
from itertools import product


def ring(points):
    closed = points + points[:1]
    return "(" + ",".join(f"{lat:.8f} {lon:.8f}" for lat, lon in closed) + ")"


def polygon(rings):
    return "POLYGON(" + ",".join(ring(points) for points in rings) + ")"


def multi(polygons):
    return (
        "MULTIPOLYGON("
        + ",".join(
            "(" + ",".join(ring(points) for points in rings) + ")" for rings in polygons
        )
        + ")"
    )


def square(low, high):
    return [(low, low), (low, high), (high, high), (high, low)]


def boundary_only_rings():
    return square(0, 10), [(0, 5), (5, 10), (5, 0)]


def recorded_components():
    yield (
        "recorded_global_components_1",
        [
            [
                (55.095784, 43.215101),
                (-1.549061, -106.976186),
                (25.691447, 176.152400),
                (-77.605773, 8.784222),
            ],
            [(21.909158, 172.938139), (28.962876, 174.095155), (26.699045, 174.007825)],
        ],
    )
    yield (
        "recorded_global_components_2",
        [
            [
                (-63.962352, -175.602231),
                (47.774240, -160.567110),
                (-32.437052, 70.954473),
                (10.638088, -84.992182),
            ],
            [(12.728225, -85.708396), (9.138578, -86.807402), (11.084715, -87.604848)],
        ],
    )


def cases():
    shell = square(0, 10)
    hole = square(2, 4)
    local = [
        ("hole_inside", [shell, hole]),
        ("hole_outside", [shell, square(12, 14)]),
        ("hole_shell_vertex", [shell, [(0, 0), (1, 2), (2, 1)]]),
        ("hole_shell_edge_point", [shell, [(0, 5), (2, 6), (2, 4)]]),
        ("hole_shell_edge_overlap", [shell, [(0, 2), (0, 4), (2, 4), (2, 2)]]),
        ("hole_crosses_shell", [shell, [(-1, 2), (-1, 4), (2, 4), (2, 2)]]),
        ("two_shell_contacts", [shell, [(5, 0), (8, 5), (5, 10), (2, 5)]]),
        ("holes_point_contact", [shell, hole, square(4, 6)]),
        ("holes_shared_edge", [shell, hole, [(4, 2), (4, 4), (6, 4), (6, 2)]]),
        ("holes_nested", [shell, square(2, 6), square(3, 4)]),
        ("duplicate_holes", [shell, hole, hole]),
        ("contact_cycle", [shell, [(2, 0), (5, 2), (2, 4)], [(8, 0), (8, 4), (5, 2)]]),
        (
            "common_contact",
            [
                shell,
                [(5, 5), (7, 4), (7, 6)],
                [(5, 5), (4, 7), (3, 6)],
                [(5, 5), (3, 3), (5, 3)],
            ],
        ),
    ]
    for name, rings in local:
        for orientation in product([False, True], repeat=len(rings)):
            oriented = [
                points[::-1] if reverse else points
                for points, reverse in zip(rings, orientation, strict=True)
            ]
            suffix = "".join("1" if reverse else "0" for reverse in orientation)
            yield f"{name}_orientation_{suffix}", polygon(oriented)
    multipart = [
        ("components_disjoint", [[hole], [square(6, 8)]]),
        ("components_point_contact", [[hole], [square(4, 6)]]),
        ("components_edge_overlap", [[hole], [[(4, 2), (4, 4), (6, 4), (6, 2)]]]),
        ("components_overlap", [[hole], [square(3, 5)]]),
        ("component_inside_hole", [[shell, square(2, 8)], [square(3, 4)]]),
        ("component_inside_fill", [[shell], [square(3, 4)]]),
        ("components_identical", [[hole], [hole]]),
    ]
    for name, polygons in multipart:
        yield name, multi(polygons)
        yield name + "_swapped", multi(polygons[::-1])
    enclosing, inscribed = boundary_only_rings()
    yield "hole_all_vertices_on_shell", polygon([enclosing, inscribed])
    yield "component_all_vertices_on_shell", multi([[enclosing], [inscribed]])
    yield (
        "components_two_point_contacts",
        multi(
            [
                [[(0, 0), (0, 10), (10, 5)]],
                [[(0, 0), (-1, 4), (0, 10), (-3, 10), (-3, 0)]],
            ]
        ),
    )
    yield (
        "components_common_contact",
        multi(
            [
                [[(5, 5), (7, 4), (7, 6)]],
                [[(5, 5), (4, 7), (3, 6)]],
                [[(5, 5), (3, 3), (5, 3)]],
            ]
        ),
    )
    yield (
        "hole_crosses_at_vertices",
        polygon(
            [
                shell,
                [(1, 0), (2, 2), (3, 0), (2, -2)],
            ]
        ),
    )
    for name, components in recorded_components():
        yield name, multi([[points] for points in components])
    yield (
        "collection_valid_hole",
        "GEOMETRYCOLLECTION(POINT(0 0)," + polygon([shell, hole]) + ")",
    )
    yield (
        "collection_invalid_hole",
        "GEOMETRYCOLLECTION(POINT(0 0)," + polygon([shell, square(12, 14)]) + ")",
    )
    rng = random.Random(9176)
    for case in range(300):
        shell = [(rng.uniform(-80, 80), rng.uniform(-179, 179)) for _ in range(4)]
        latitude, longitude = rng.uniform(-79, 79), rng.uniform(-178, 178)
        triangle = [
            (latitude, longitude),
            (latitude + 0.01, longitude),
            (latitude, longitude + 0.01),
        ]
        yield f"global_hole_{case}", polygon([shell, triangle])
        yield f"global_components_{case}", multi([[shell], [triangle]])
