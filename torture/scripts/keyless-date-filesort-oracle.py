"""Check keyless temporal filesort writes against native MySQL 8.4.11."""

import pathlib
import runpy
import threading

oracle = runpy.run_path(
    str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py"))
)


def verify_cases(
    owner, waiter, column_type, base_ranks, order_expression, scenarios, label=None
):
    cases = 0
    for verb in ("UPDATE", "DELETE"):
        for (
            predicate,
            pending_id,
            pending_rank,
            hint,
            should_wait,
            changed_ids,
        ) in scenarios:
            if hint and verb != "UPDATE":
                continue
            owner.query("DROP TABLE IF EXISTS probe.target")
            owner.query(
                f"CREATE TABLE probe.target(id INT,rank_value {column_type},n INT,"
                "KEY ix_rank(rank_value))"
            )
            owner.query(
                "INSERT INTO probe.target VALUES"
                + ",".join(
                    f"({index},'{rank}',0)"
                    for index, rank in enumerate(base_ranks, start=1)
                )
            )
            owner.query("BEGIN")
            rank_literal = "NULL" if pending_rank is None else f"'{pending_rank}'"
            owner.query(
                f"INSERT INTO probe.target VALUES({pending_id},{rank_literal},0)"
            )
            waiter.query("USE probe;BEGIN")
            action = (
                "UPDATE probe.target" + hint + " SET n=n+1"
                if verb == "UPDATE"
                else "DELETE FROM probe.target"
            )
            statement = (
                f"{action} WHERE {predicate} ORDER BY {order_expression} LIMIT 2"
            )
            started = threading.Event()
            outcome = {}

            def waiting_write(started=started, outcome=outcome, statement=statement):
                try:
                    started.set()
                    outcome["affected"] = int(
                        waiter.query(statement + ";SELECT ROW_COUNT()")
                    )
                    waiter.query("COMMIT")
                except Exception as error:  # noqa: BLE001 - report failures on the main thread
                    outcome["error"] = error

            thread = threading.Thread(target=waiting_write, daemon=True)
            thread.start()
            assert started.wait(2), statement
            thread.join(0.2)
            assert thread.is_alive() == should_wait, (
                verb,
                predicate,
                pending_rank,
                "wait behavior",
            )
            owner.query("COMMIT")
            thread.join(5)
            assert not thread.is_alive(), (statement, "writer did not finish")
            assert outcome == {"affected": 2}, (statement, outcome)

            rows = [
                (pending_id, pending_rank),
                *enumerate(base_ranks, start=1),
            ]
            expected = "\n".join(
                f"{row_id}\t{rank if rank is not None else 'NULL'}\t"
                f"{int(row_id in changed_ids) if verb == 'UPDATE' else 0}"
                for row_id, rank in sorted(rows, key=lambda row: (row[1] or "", row[0]))
                if verb == "UPDATE" or row_id not in changed_ids
            )
            actual = owner.query(
                "SELECT id,rank_value,n FROM probe.target ORDER BY rank_value,id"
            )
            assert actual == expected, (statement, pending_rank, actual)
            cases += 1
    print(f"MySQL 8.4.11: {cases} keyless {label or column_type} filesort cases passed")


def verify(owner, waiter):
    owner.query("SET time_zone='+00:00'")
    waiter.query("SET time_zone='+00:00'")
    dates = (
        ("rank_value>'2026-01-01'", 0, "2026-01-01", "", False, (1, 2)),
        ("rank_value>='2026-01-01'", 0, "2026-01-01", "", True, (0, 1)),
        ("rank_value>'2026-01-01'", 100, "2026-12-31", "", True, (1, 2)),
        ("rank_value<'2026-02-01'", 100, "2026-12-31", "", True, (1, 2)),
        ("n>=0", 0, None, "", True, (0, 1)),
        ("rank_value>'2026-01-01'", 0, None, "", False, (1, 2)),
        (
            "rank_value>'2026-01-01'",
            0,
            "2026-01-01",
            " IGNORE INDEX(ix_rank)",
            True,
            (1, 2),
        ),
    )
    verify_cases(
        owner,
        waiter,
        "DATE",
        ("2026-01-02", "2026-01-03", "2026-01-04"),
        "DAYOFYEAR(rank_value)",
        dates,
    )

    times = (
        ("rank_value>'-12:00:00'", 0, "-12:00:00", "", False, (1, 2)),
        ("rank_value>='-12:00:00'", 0, "-12:00:00", "", True, (0, 1)),
        ("rank_value>'350:00:00'", 0, "350:00:00", "", False, (1, 2)),
        ("rank_value>='350:00:00'", 0, "350:00:00", "", True, (0, 1)),
        ("rank_value>'350:00:00'", 100, "800:00:00", "", True, (1, 2)),
        ("rank_value<'400:00:00'", 100, "800:00:00", "", True, (1, 2)),
        ("n>=0", 0, None, "", True, (0, 1)),
        ("rank_value>'350:00:00'", 0, None, "", False, (1, 2)),
        (
            "rank_value>'350:00:00'",
            0,
            "350:00:00",
            " IGNORE INDEX(ix_rank)",
            True,
            (1, 2),
        ),
    )
    verify_cases(
        owner,
        waiter,
        "TIME",
        ("351:00:00", "352:00:00", "353:00:00"),
        "TIME_TO_SEC(rank_value)",
        times,
    )

    start = "2026-01-01 12:34:56.123456"
    end = "2026-12-31 12:34:56.123456"
    datetime_cases = (
        (f"rank_value>'{start}'", 0, start, "", False, (1, 2)),
        (f"rank_value>='{start}'", 0, start, "", True, (0, 1)),
        (f"rank_value>'{start}'", 100, end, "", True, (1, 2)),
        ("rank_value<'2026-02-01 00:00:00'", 100, end, "", True, (1, 2)),
        ("n>=0", 0, None, "", True, (0, 1)),
        (f"rank_value>'{start}'", 0, None, "", False, (1, 2)),
        (f"rank_value>'{start}'", 0, start, " IGNORE INDEX(ix_rank)", True, (1, 2)),
    )
    for column_type in ("DATETIME(6)", "TIMESTAMP(6)"):
        verify_cases(
            owner,
            waiter,
            column_type,
            (
                "2026-01-02 12:34:56.123456",
                "2026-01-03 12:34:56.123456",
                "2026-01-04 12:34:56.123456",
            ),
            "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
            datetime_cases,
        )
    owner.query("SET time_zone='+02:00'")
    waiter.query("SET time_zone='+02:00'")
    verify_cases(
        owner,
        waiter,
        "TIMESTAMP(6)",
        (
            "2026-01-02 12:34:56.123456",
            "2026-01-03 12:34:56.123456",
            "2026-01-04 12:34:56.123456",
        ),
        "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
        datetime_cases,
        "TIMESTAMP(6) at +02:00",
    )
    waiter.query("SET time_zone='+00:00'")
    verify_cases(
        owner,
        waiter,
        "TIMESTAMP(6)",
        (
            "2026-01-02 12:34:56.123456",
            "2026-01-03 12:34:56.123456",
            "2026-01-04 12:34:56.123456",
        ),
        "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
        (
            (f"rank_value>'{start}'", 0, start, "", False, (1, 2)),
            (
                f"rank_value>'{start}'",
                0,
                "2026-01-01 14:34:56.123456",
                "",
                False,
                (1, 2),
            ),
            (
                f"rank_value>='{start}'",
                0,
                "2026-01-01 14:34:56.123456",
                "",
                True,
                (0, 1),
            ),
            (
                f"rank_value>'{start}'",
                100,
                "2026-12-31 14:34:56.123456",
                "",
                True,
                (1, 2),
            ),
        ),
        "TIMESTAMP(6) owner +02:00 / waiter UTC",
    )
    owner.query("SET time_zone='+00:00'")
    waiter.query("SET time_zone='+02:00'")
    verify_cases(
        owner,
        waiter,
        "TIMESTAMP(6)",
        (
            "2026-01-02 12:34:56.123456",
            "2026-01-03 12:34:56.123456",
            "2026-01-04 12:34:56.123456",
        ),
        "DATE_ADD(rank_value, INTERVAL 0 SECOND)",
        (
            (
                f"rank_value>'{start}'",
                0,
                "2026-01-01 09:34:56.123456",
                "",
                False,
                (1, 2),
            ),
            (
                f"rank_value>'{start}'",
                0,
                "2026-01-01 10:34:56.123456",
                "",
                False,
                (1, 2),
            ),
            (
                f"rank_value>='{start}'",
                0,
                "2026-01-01 10:34:56.123456",
                "",
                True,
                (0, 1),
            ),
            (f"rank_value>'{start}'", 100, end, "", True, (1, 2)),
        ),
        "TIMESTAMP(6) owner UTC / waiter +02:00",
    )


if __name__ == "__main__":
    oracle["run"](verify)
