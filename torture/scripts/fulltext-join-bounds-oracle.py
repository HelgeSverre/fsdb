"""Check equality-bound full-text joins on disposable native MySQL 8.4.11."""

import pathlib
import runpy

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _):
    client.query("USE probe;CREATE TABLE owners(id INT PRIMARY KEY);"
                 "INSERT INTO owners VALUES(42);"
                 "CREATE TABLE bounds(id INT PRIMARY KEY);INSERT INTO bounds VALUES(42);"
                 "CREATE TABLE docs(id INT PRIMARY KEY,owner_id INT,body TEXT,"
                 "KEY(owner_id),FULLTEXT(body))")
    values = [f"({i},{i % 100},'{ 'ordinary' if i % 5 == 0 else 'needle'}')"
              for i in range(1, 1001)]
    client.query("INSERT INTO docs VALUES " + ",".join(values))
    expected = "\n".join(f"{i}\t0.009392" for i in range(42, 1001, 100))
    for label, joins, bound in [
        ("direct", "JOIN owners o ON o.id=d.owner_id", "o.id=42"),
        ("transitive", "JOIN owners o ON o.id=d.owner_id JOIN bounds b ON b.id=o.id", "42=b.id"),
        ("ON literal", "JOIN owners o ON o.id=d.owner_id AND 42=o.id", "TRUE"),
        ("cross join", "CROSS JOIN owners o", "o.id=d.owner_id AND o.id=42"),
    ]:
        query = ("SELECT d.id,ROUND(MATCH(d.body) AGAINST('needle'),6) FROM docs d "
                 + joins + " WHERE " + bound + " AND MATCH(d.body) AGAINST('needle')")
        actual = client.query(query + " ORDER BY d.id")
        native["expect"](label, actual, expected)
        native["expect"](label + " explicit bound", client.query(query + " AND d.owner_id=42 ORDER BY d.id"), actual)

    client.query("CREATE TABLE names(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,body TEXT,KEY(k),FULLTEXT(body));"
                 "CREATE TABLE labels(k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci);"
                 "INSERT INTO names VALUES(1,'①','needle'),(2,'1','needle'),(3,'other','ordinary');"
                 "INSERT INTO labels VALUES('1')")
    prefix = "SELECT d.id FROM names d JOIN labels o ON o.k=d.k WHERE "
    suffix = " AND MATCH(d.body) AGAINST('needle') ORDER BY d.id"
    native["expect"]("numeric text bound", client.query(prefix + "o.k=1" + suffix), "1\n2")
    native["expect"]("numeric text bound cannot propagate", client.query(prefix + "o.k=1 AND d.k=1" + suffix), "2")
    native["expect"]("string text bound", client.query(prefix + "o.k='1'" + suffix), "1\n2")


    client.query("INSERT INTO names VALUES(4,'missing','needle')")
    native["expect"]("outer join retains unmatched rows", client.query(
        "SELECT d.id FROM names d LEFT JOIN labels o ON o.k=d.k AND o.k='1' "
        "WHERE MATCH(d.body) AGAINST('needle') ORDER BY d.id"), "1\n2\n4")
    native["expect"]("disjunction retains other matches", client.query(
        "SELECT d.id FROM names d CROSS JOIN labels o WHERE (o.k=d.k OR d.id=4) "
        "AND o.k='1' AND MATCH(d.body) AGAINST('needle') ORDER BY d.id"), "1\n2\n4")


if __name__ == "__main__":
    native["run"](verify)
