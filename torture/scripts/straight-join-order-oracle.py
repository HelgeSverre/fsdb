"""Verify partial STRAIGHT_JOIN ordering on disposable native MySQL 8.4.11."""

import pathlib
import runpy

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _):
    client.query("USE probe;CREATE TABLE bases(id INT PRIMARY KEY);"
                 "CREATE TABLE many_rows(id INT PRIMARY KEY,base_id INT,KEY(base_id));"
                 "CREATE TABLE few_rows(id INT PRIMARY KEY,base_id INT,KEY(base_id))")
    client.query("INSERT INTO bases VALUES " + ",".join(f"({i})" for i in range(1, 501)))
    for start in range(1, 25001, 250):
        values = [f"({i},{(i - 1) % 500 + 1})" for i in range(start, start + 250)]
        client.query("INSERT INTO many_rows VALUES " + ",".join(values))
    client.query("INSERT INTO few_rows VALUES(1,7)")
    for label, modifier, first, second, selective_first in [
        ("early local constraint", "", "STRAIGHT_JOIN", "JOIN", True),
        ("late local constraint", "", "JOIN", "STRAIGHT_JOIN", False),
        ("both local constraints", "", "STRAIGHT_JOIN", "STRAIGHT_JOIN", False),
        ("global constraint", "STRAIGHT_JOIN ", "JOIN", "JOIN", False),
    ]:
        query = ("SELECT " + modifier + "b.id FROM bases b " + first
                 + " many_rows l ON l.base_id=b.id " + second
                 + " few_rows s ON s.base_id=b.id ORDER BY b.id")
        native["expect"](label + " rows", client.query(query), "\n".join(["7"] * 50))
        plan = client.query("EXPLAIN " + query)
        tables = [row.split("\t")[2] for row in plan.splitlines()]
        native["expect"](label + " left before right", tables.index("b") < tables.index("l"), True)
        if selective_first:
            native["expect"](label + " selective source before fan-out", tables.index("s") < tables.index("l"), True)
        else:
            native["expect"](label + " written prefix", tables, ["b", "l", "s"])
        print(plan, flush=True)


if __name__ == "__main__":
    native["run"](verify)
