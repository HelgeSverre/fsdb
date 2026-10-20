"""Check keyless DATE-index filesort writes against native MySQL 8.4.11."""

import pathlib
import runpy
import threading

oracle = runpy.run_path(
    str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py"))
)


def verify(owner, waiter):
    scenarios = (
        ("rank_date>'2026-01-01'", 0, "2026-01-01", "", False, (1, 2)),
        ("rank_date>='2026-01-01'", 0, "2026-01-01", "", True, (0, 1)),
        ("rank_date>'2026-01-01'", 100, "2026-12-31", "", True, (1, 2)),
        ("rank_date<'2026-02-01'", 100, "2026-12-31", "", True, (1, 2)),
        ("n>=0", 0, None, "", True, (0, 1)),
        ("rank_date>'2026-01-01'", 0, None, "", False, (1, 2)),
    )
    update_only = (
        (
            "rank_date>'2026-01-01'",
            0,
            "2026-01-01",
            " IGNORE INDEX(ix_rank)",
            True,
            (1, 2),
        ),
    )
    cases = 0
    for verb in ("UPDATE", "DELETE"):
        for predicate, pending_id, pending_date, hint, should_wait, changed_ids in (
            scenarios + (update_only if verb == "UPDATE" else ())
        ):
            owner.query("DROP TABLE IF EXISTS probe.target")
            owner.query(
                "CREATE TABLE probe.target(id INT,rank_date DATE,n INT,"
                "KEY ix_rank(rank_date))"
            )
            owner.query(
                "INSERT INTO probe.target VALUES"
                "(1,'2026-01-02',0),(2,'2026-01-03',0),(3,'2026-01-04',0)"
            )
            owner.query("BEGIN")
            date_literal = "NULL" if pending_date is None else f"'{pending_date}'"
            owner.query(
                f"INSERT INTO probe.target VALUES({pending_id},{date_literal},0)"
            )
            waiter.query("USE probe;BEGIN")
            action = (
                "UPDATE probe.target" + hint + " SET n=n+1"
                if verb == "UPDATE"
                else "DELETE FROM probe.target"
            )
            statement = (
                f"{action} WHERE {predicate} ORDER BY DAYOFYEAR(rank_date) LIMIT 2"
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
                pending_date,
                "wait behavior",
            )
            owner.query("COMMIT")
            thread.join(5)
            assert not thread.is_alive(), (statement, "writer did not finish")
            assert outcome == {"affected": 2}, (statement, outcome)

            rows = [
                (pending_id, pending_date),
                (1, "2026-01-02"),
                (2, "2026-01-03"),
                (3, "2026-01-04"),
            ]
            expected = "\n".join(
                f"{row_id}\t{date if date is not None else 'NULL'}\t"
                f"{int(row_id in changed_ids) if verb == 'UPDATE' else 0}"
                for row_id, date in sorted(rows, key=lambda row: (row[1] or "", row[0]))
                if verb == "UPDATE" or row_id not in changed_ids
            )
            actual = owner.query(
                "SELECT id,rank_date,n FROM probe.target ORDER BY rank_date,id"
            )
            assert actual == expected, (statement, pending_date, actual)
            cases += 1
    print(f"MySQL 8.4.11: {cases} keyless DATE filesort cases passed")


if __name__ == "__main__":
    oracle["run"](verify)
