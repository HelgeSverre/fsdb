"""Check keyless text-index filesort writes against native MySQL 8.4.11."""

import pathlib
import runpy
import threading

oracle = runpy.run_path(
    str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py"))
)


def verify(owner, waiter):
    common = (
        ("n>=0", 0, "a000", "", True, (0, 1)),
        ("n>=0", 100, "z999", "", True, (1, 2)),
        ("rank_value>'a000'", 0, "a000", "", False, (1, 2)),
        ("rank_value>'a000'", 100, "z999", "", True, (1, 2)),
        ("rank_value>='a000'", 0, "a000", "", True, (0, 1)),
        ("rank_value<'z000'", 100, "z999", "", True, (1, 2)),
        ("n>=0", 0, None, "", True, (0, 1)),
        ("rank_value>'a000'", 0, None, "", False, (1, 2)),
    )
    update_only = (
        ("rank_value>'a000'", 0, "a000", " IGNORE INDEX(ix_rank)", True, (1, 2)),
    )
    cases = 0
    for collation in ("utf8mb4_bin", "utf8mb4_0900_ai_ci"):
        for verb in ("UPDATE", "DELETE"):
            scenarios = common + (update_only if verb == "UPDATE" else ())
            for (
                predicate,
                pending_id,
                pending_rank,
                hint,
                should_wait,
                changed_ids,
            ) in scenarios:
                effective_predicate = (
                    predicate.replace("'a000'", "'A000'")
                    if collation == "utf8mb4_0900_ai_ci"
                    else predicate
                )
                owner.query("DROP TABLE IF EXISTS probe.target")
                owner.query(
                    "CREATE TABLE probe.target(id INT,rank_value VARCHAR(8) "
                    f"COLLATE {collation},n INT,KEY ix_rank(rank_value))"
                )
                owner.query(
                    "INSERT INTO probe.target VALUES"
                    "(1,'a001',0),(2,'a002',0),(3,'a003',0)"
                )
                owner.query("BEGIN")
                pending_literal = (
                    "NULL" if pending_rank is None else f"'{pending_rank}'"
                )
                owner.query(
                    f"INSERT INTO probe.target VALUES({pending_id},{pending_literal},0)"
                )
                waiter.query("USE probe;BEGIN")
                action = (
                    f"UPDATE probe.target{hint} SET n=n+1"
                    if verb == "UPDATE"
                    else "DELETE FROM probe.target"
                )
                statement = (
                    f"{action} WHERE {effective_predicate} "
                    "ORDER BY CONCAT(rank_value,'') LIMIT 2"
                )
                started = threading.Event()
                outcome = {}

                def waiting_write(
                    started=started, outcome=outcome, statement=statement
                ):
                    try:
                        started.set()
                        outcome["affected"] = int(
                            waiter.query(statement + ";SELECT ROW_COUNT()")
                        )
                        waiter.query("COMMIT")
                    except Exception as error:  # noqa: BLE001 - report worker failures on the main thread
                        outcome["error"] = error

                thread = threading.Thread(target=waiting_write, daemon=True)
                thread.start()
                assert started.wait(2), statement
                thread.join(0.2)
                assert thread.is_alive() == should_wait, (
                    collation,
                    statement,
                    pending_rank,
                    "wait behavior",
                )
                owner.query("COMMIT")
                thread.join(5)
                assert not thread.is_alive(), (statement, "writer did not finish")
                assert outcome == {"affected": 2}, (statement, outcome)

                rows = [
                    (pending_id, pending_rank),
                    (1, "a001"),
                    (2, "a002"),
                    (3, "a003"),
                ]
                expected = "\n".join(
                    f"{row_id}\t{rank if rank is not None else 'NULL'}\t"
                    f"{int(row_id in changed_ids) if verb == 'UPDATE' else 0}"
                    for row_id, rank in sorted(rows)
                    if verb == "UPDATE" or row_id not in changed_ids
                )
                actual = owner.query(
                    "SELECT id,rank_value,n FROM probe.target ORDER BY rank_value,id"
                )
                assert actual == expected, (collation, statement, pending_rank, actual)
                cases += 1
    print(f"MySQL 8.4.11: {cases} keyless text filesort cases passed")


if __name__ == "__main__":
    oracle["run"](verify)
