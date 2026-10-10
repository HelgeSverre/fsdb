"""Check equality-bound full-text joins on disposable native MySQL 8.4.11."""

import pathlib
import runpy
import re
import subprocess

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _):
    client.query("USE probe;CREATE TABLE owners(id INT PRIMARY KEY);"
                 "INSERT INTO owners VALUES(42);"
                 "CREATE TABLE bounds(id INT PRIMARY KEY);INSERT INTO bounds VALUES(42);"
                 "CREATE TABLE limits(chosen_owner INT PRIMARY KEY);INSERT INTO limits VALUES(42);"
                 "CREATE TABLE shadow(owner_id INT PRIMARY KEY);INSERT INTO shadow VALUES(42);"
                 "CREATE TABLE docs(id INT PRIMARY KEY,owner_id INT,body TEXT,"
                 "KEY(owner_id),FULLTEXT(body))")
    values = [f"({i},{i % 100},'{ 'ordinary' if i % 5 == 0 else 'needle'}')"
              for i in range(1, 1001)]
    client.query("INSERT INTO docs VALUES " + ",".join(values))
    expected = client.query(
        "SELECT id,ROUND(MATCH(body) AGAINST('needle'),6) FROM docs "
        "WHERE owner_id=42 AND MATCH(body) AGAINST('needle') ORDER BY id")
    for label, joins, bound in [
        ("direct", "JOIN owners o ON o.id=d.owner_id", "o.id=42"),
        ("transitive", "JOIN owners o ON o.id=d.owner_id JOIN bounds b ON b.id=o.id", "42=b.id"),
        ("ON literal", "JOIN owners o ON o.id=d.owner_id AND 42=o.id", "TRUE"),
        ("cross join", "CROSS JOIN owners o", "o.id=d.owner_id AND o.id=42"),
        ("bare join key", "JOIN owners o ON o.id=owner_id", "o.id=42"),
        ("bare transitive bound", "JOIN owners o ON o.id=owner_id JOIN limits b ON chosen_owner=o.id", "chosen_owner=42"),
        ("later name collision", "JOIN owners o ON o.id=owner_id JOIN limits b ON chosen_owner=o.id JOIN shadow s ON s.owner_id=o.id", "chosen_owner=42"),
    ]:
        query = ("SELECT d.id,ROUND(MATCH(d.body) AGAINST('needle'),6) FROM docs d "
                 + joins + " WHERE " + bound + " AND MATCH(d.body) AGAINST('needle')")
        actual = client.query(query + " ORDER BY d.id")
        native["expect"](label, actual, expected)
        native["expect"](label + " explicit bound", client.query(query + " AND d.owner_id=42 ORDER BY d.id"), actual)

    client.query("INSERT INTO owners VALUES(43),(97)")
    for bound, selected in [
        ("o.id BETWEEN 42 AND 43", [42, 43]),
        ("o.id>=42 AND o.id<44", [42, 43]),
        ("44>o.id AND 42<=o.id", [42, 43]),
        ("o.id IN (42,97)", [42, 97]),
        ("o.id IN (42,97,NULL)", [42, 97]),
    ]:
        query = ("SELECT d.id,ROUND(MATCH(d.body) AGAINST('needle'),6) FROM docs d "
                 "JOIN owners o ON o.id=d.owner_id WHERE " + bound
                 + " AND MATCH(d.body) AGAINST('needle')")
        expected = client.query(
            "SELECT id,ROUND(MATCH(body) AGAINST('needle'),6) FROM docs "
            "WHERE owner_id IN (" + ",".join(map(str, selected))
            + ") AND MATCH(body) AGAINST('needle') ORDER BY id")
        native["expect"](bound, client.query(query + " ORDER BY d.id"), expected)
        native["expect"](bound + " explicit bound", client.query(
            query + " AND " + bound.replace("o.id", "d.owner_id") + " ORDER BY d.id"), expected)

    for label, query, expected_code in [
        ("ambiguous WHERE", "SELECT d.id FROM docs d JOIN owners o ON o.id=d.owner_id WHERE id=42 AND MATCH(d.body) AGAINST('needle')", "1052"),
        ("ambiguous ON", "SELECT d.id FROM docs d JOIN owners o ON id=d.owner_id WHERE o.id=42 AND MATCH(d.body) AGAINST('needle')", "1052"),
        ("forward ON reference", "SELECT d.id FROM docs d JOIN owners o ON o.id=chosen_owner JOIN limits b ON b.chosen_owner=d.owner_id WHERE b.chosen_owner=42 AND MATCH(d.body) AGAINST('needle')", "1054"),
    ]:
        result = subprocess.run([*client.process.args, "-e", "USE probe;" + query], capture_output=True, text=True)
        error = re.search(r"ERROR (\d+)", result.stderr)
        native["expect"](label, error.group(1) if error else result.stdout, expected_code)

    native["expect"]("STRAIGHT_JOIN with USING", client.query(
        "SELECT d.id FROM docs d STRAIGHT_JOIN owners o USING(id) ORDER BY d.id"), "42\n43\n97")
    native["expect"]("parenthesized STRAIGHT_JOIN", client.query(
        "SELECT COUNT(*) FROM ((docs d STRAIGHT_JOIN owners o ON o.id=d.owner_id))"), "30")
    native["expect"]("STRAIGHT_JOIN without ON", client.query(
        "SELECT COUNT(*) FROM docs d STRAIGHT_JOIN owners o"), "3000")
    native["expect"]("STRAIGHT_JOIN with ON", client.query(
        "SELECT COUNT(*) FROM docs d STRAIGHT_JOIN owners o ON o.id=d.owner_id"), "30")

    client.query("CREATE TABLE straight_left(id INT PRIMARY KEY,v INT);"
                 "CREATE TABLE straight_right(id INT PRIMARY KEY);"
                 "INSERT INTO straight_left VALUES(1,10),(2,20),(3,30);"
                 "INSERT INTO straight_right VALUES(2),(3);"
                 "CREATE VIEW straight_view AS SELECT a.id,a.v FROM straight_left a "
                 "STRAIGHT_JOIN straight_right b ON a.id=b.id")
    native["expect"]("STRAIGHT_JOIN view is updatable", client.query(
        "SELECT IS_UPDATABLE FROM information_schema.views WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='straight_view'"), "YES")
    native["expect"]("STRAIGHT_JOIN view update", client.query(
        "UPDATE straight_view SET v=v+1 WHERE id=2;SELECT ROW_COUNT()"), "1")
    native["expect"]("STRAIGHT_JOIN update", client.query(
        "UPDATE straight_left a STRAIGHT_JOIN straight_right b ON a.id=b.id SET a.v=a.v+1 WHERE b.id=3;SELECT ROW_COUNT()"), "1")
    native["expect"]("STRAIGHT_JOIN delete", client.query(
        "DELETE a FROM straight_left a STRAIGHT_JOIN straight_right b ON a.id=b.id WHERE b.id=3;SELECT ROW_COUNT()"), "1")
    native["expect"]("STRAIGHT_JOIN mutation rows", client.query(
        "SELECT id,v FROM straight_left ORDER BY id"), "1\t10\n2\t21")

    native["expect"]("STRAIGHT_JOIN LATERAL", client.query(
        "SELECT a.id,x.n FROM straight_left a STRAIGHT_JOIN LATERAL (SELECT a.v+1 AS n) x ON TRUE ORDER BY a.id"), "1\t11\n2\t22")
    native["expect"]("STRAIGHT_JOIN JSON_TABLE", client.query(
        "SELECT a.id,j.n FROM straight_left a STRAIGHT_JOIN JSON_TABLE(JSON_ARRAY(a.v),'$[*]' COLUMNS(n INT PATH '$')) j ON TRUE ORDER BY a.id"), "1\t10\n2\t21")

    client.query("CREATE TABLE names(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,body TEXT,KEY(k),FULLTEXT(body));"
                 "CREATE TABLE labels(k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci);"
                 "INSERT INTO names VALUES(1,'①','needle'),(2,'1','needle'),(3,'other','ordinary');"
                 "INSERT INTO labels VALUES('1')")
    prefix = "SELECT d.id FROM names d JOIN labels o ON o.k=d.k WHERE "
    suffix = " AND MATCH(d.body) AGAINST('needle') ORDER BY d.id"
    native["expect"]("numeric text bound", client.query(prefix + "o.k=1" + suffix), "1\n2")
    native["expect"]("numeric text bound cannot propagate", client.query(prefix + "o.k=1 AND d.k=1" + suffix), "2")
    native["expect"]("numeric text range", client.query(prefix + "o.k BETWEEN 1 AND 1" + suffix), "1\n2")
    native["expect"]("numeric text IN without MATCH", client.query(prefix + "o.k IN (1,NULL) ORDER BY d.id"), "1\n2")
    native["expect"]("numeric text IN with MATCH", client.query(prefix + "o.k IN (1,NULL)" + suffix), "2")
    native["expect"]("string text bound", client.query(prefix + "o.k='1'" + suffix), "1\n2")

    for source, fulltext, expected_ids, filter_owner in [
        ("names d JOIN labels o", False, "1\n2", "o"),
        ("names d JOIN labels o", True, "2", "d"),
        ("names d STRAIGHT_JOIN labels o", False, "2", "d"),
        ("names d STRAIGHT_JOIN labels o", True, "2", "d"),
        ("labels o STRAIGHT_JOIN names d", False, "1\n2", "o"),
        ("labels o STRAIGHT_JOIN names d", True, "1\n2", "o"),
    ]:
        query = ("SELECT d.id FROM " + source + " ON o.k=d.k WHERE o.k IN (1,NULL)"
                 + (" AND MATCH(d.body) AGAINST('needle')" if fulltext else "")
                 + " ORDER BY d.id")
        label = source + (" with MATCH" if fulltext else " without MATCH")
        native["expect"](label, client.query(query), expected_ids)
        plan = client.query("EXPLAIN FORMAT=TREE " + query)
        native["expect"](label + " filter owner", f"({filter_owner}.k in (1,NULL))" in plan, True)
        print(plan, flush=True)

    for label, extra_labels, predicate, expected_ids, filter_owner in [
        ("costed original names bound", False, "d.k IN (1,NULL)", "1\n2", "o"),
        ("costed original labels bound", False, "o.k IN (1,NULL)", "1\n2", "o"),
        ("costed expanded names bound", True, "d.k IN (1,NULL)", "1\n2", "d"),
        ("costed expanded labels bound", True, "o.k IN (1,NULL)", "1\n2", "o"),
    ]:
        if extra_labels and client.query("SELECT COUNT(*) FROM labels") == "1":
            client.query("INSERT INTO labels VALUES('other')")
        query = ("SELECT d.id FROM names d JOIN labels o ON o.k=d.k WHERE "
                 + predicate + " ORDER BY d.id")
        native["expect"](label, client.query(query), expected_ids)
        plan = client.query("EXPLAIN FORMAT=TREE " + query)
        print(label + ":\n" + plan, flush=True)
        native["expect"](label + " filter owner", f"({filter_owner}.k in (1,NULL))" in plan, True)

    client.query("INSERT INTO names VALUES(4,'missing','needle')")
    native["expect"]("outer join retains unmatched rows", client.query(
        "SELECT d.id FROM names d LEFT JOIN labels o ON o.k=d.k AND o.k='1' "
        "WHERE MATCH(d.body) AGAINST('needle') ORDER BY d.id"), "1\n2\n4")
    native["expect"]("disjunction retains other matches", client.query(
        "SELECT d.id FROM names d CROSS JOIN labels o WHERE (o.k=d.k OR d.id=4) "
        "AND o.k='1' AND MATCH(d.body) AGAINST('needle') ORDER BY d.id"), "1\n2\n4")


if __name__ == "__main__":
    native["run"](verify)
