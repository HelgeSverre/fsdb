"""Pin correlated equality/range OR results on disposable native MySQL 8.4.11."""

import pathlib
import runpy


oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    values = ",".join(f"({number},{(number - 1) % 50 + 1})" for number in range(1, 1001))
    client.query(
        "USE probe;CREATE TABLE range_or_users(id INT PRIMARY KEY);"
        "CREATE TABLE range_or_orders(id INT PRIMARY KEY,user_id INT,KEY ix_user(user_id));"
        "INSERT INTO range_or_users VALUES(1),(2);"
        f"INSERT INTO range_or_orders VALUES{values}"
    )

    sources = [
        ("physical", "", "range_or_orders o"),
        ("pass-through", "", "(SELECT id,user_id FROM range_or_orders) o"),
        ("pass-through CTE", "WITH candidates AS (SELECT id,user_id FROM range_or_orders)", "candidates o"),
        ("materialized", "", "(SELECT id+0 AS id,user_id+0 AS user_id FROM range_or_orders LIMIT 1000) o"),
        ("materialized CTE",
         "WITH candidates AS (SELECT id+0 AS id,user_id+0 AS user_id FROM range_or_orders LIMIT 1000)",
         "candidates o"),
    ]
    predicates = [
        ("equality/range", "o.user_id=u.id OR o.id BETWEEN u.id*20 AND u.id*20+2", "1\t23\n2\t23"),
        ("range/range", "o.id BETWEEN u.id*20 AND u.id*20+2 "
                        "OR o.id BETWEEN u.id*20+1 AND u.id*20+3", "1\t4\n2\t4"),
    ]
    for source_name, prefix, source in sources:
        for predicate_name, predicate, expected in predicates:
            query = (prefix + " SELECT u.id,(SELECT COUNT(*) FROM " + source + " WHERE " + predicate + ") "
                     "FROM range_or_users u ORDER BY u.id")
            oracle["expect"](source_name + " " + predicate_name, client.query(query), expected)


if __name__ == "__main__":
    oracle["run"](verify)
