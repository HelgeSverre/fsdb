"""Check pending TIMESTAMP filesort writes across named-zone DST transitions."""

import pathlib
import runpy

scripts = pathlib.Path(__file__).parent
harness = runpy.run_path(str(scripts / "fulltext-transaction-oracle.py"))
verify_cases = runpy.run_path(str(scripts / "keyless-date-filesort-oracle.py"))[
    "verify_cases"
]


def verify(owner, waiter):
    owner.query(
        "INSERT INTO mysql.time_zone (Time_zone_id,Use_leap_seconds) "
        "VALUES (900001,'N')"
    )
    owner.query("INSERT INTO mysql.time_zone_name VALUES ('Fsdb/Test_Eastern',900001)")
    owner.query(
        "INSERT INTO mysql.time_zone_transition_type "
        "(Time_zone_id,Transition_type_id,Offset,Is_DST,Abbreviation) "
        "VALUES (900001,0,-18000,0,'EST'),(900001,1,-14400,1,'EDT')"
    )
    owner.query(
        "INSERT INTO mysql.time_zone_transition VALUES "
        "(900001,1710054000,1),(900001,1730613600,0)"
    )
    owner.query("SET time_zone='+00:00'")
    waiter.query("SET time_zone='Fsdb/Test_Eastern'")
    scenarios = (
        (
            "rank_value>'2024-11-03 01:30:00'",
            0,
            "2024-11-03 05:30:00.000000",
            "",
            False,
            (1, 2),
        ),
        (
            "rank_value>='2024-11-03 01:30:00'",
            0,
            "2024-11-03 05:30:00.000000",
            "",
            True,
            (0, 1),
        ),
        (
            "rank_value>'2024-11-03 01:30:00'",
            0,
            "2024-11-03 06:30:00.000000",
            "",
            True,
            (1, 2),
        ),
        (
            "rank_value>='2024-11-03 01:30:00'",
            0,
            "2024-11-03 06:30:00.000000",
            "",
            True,
            (0, 1),
        ),
        (
            "rank_value>'2024-03-10 02:30:00'",
            0,
            "2024-03-10 06:59:59.000000",
            "",
            False,
            (1, 2),
        ),
        (
            "rank_value>'2024-03-10 02:30:00'",
            0,
            "2024-03-10 07:00:00.000000",
            "",
            True,
            (0, 1),
        ),
        (
            "rank_value>='2024-03-10 02:30:00'",
            0,
            "2024-03-10 07:00:00.000000",
            "",
            True,
            (0, 1),
        ),
        (
            "rank_value!='2024-03-10 02:30:00'",
            0,
            "2024-03-10 07:00:00.000000",
            "",
            True,
            (0, 1),
        ),
    )
    verify_cases(
        owner,
        waiter,
        "TIMESTAMP(6)",
        (
            "2024-12-01 12:00:00.000000",
            "2024-12-02 12:00:00.000000",
            "2024-12-03 12:00:00.000000",
        ),
        "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
        scenarios,
        "TIMESTAMP(6) named-zone DST transition",
    )
    verify_cases(
        owner,
        waiter,
        "TIMESTAMP(6)",
        (
            "2024-12-01 12:00:00.000000",
            "2024-12-02 12:00:00.000000",
            "2024-12-03 12:00:00.000000",
        ),
        "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
        (
            (
                "rank_value='2024-03-10 02:30:00'",
                0,
                "2024-03-10 07:00:00.000000",
                "",
                True,
                (),
            ),
            (
                "rank_value<'2024-03-10 02:30:00'",
                0,
                "2024-03-10 07:00:00.000000",
                "",
                True,
                (),
            ),
        ),
        "TIMESTAMP(6) named-zone zero-row DST boundary",
        expected_affected=0,
    )


if __name__ == "__main__":
    harness["run"](verify)
