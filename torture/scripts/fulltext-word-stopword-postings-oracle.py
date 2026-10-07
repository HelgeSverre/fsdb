"""Verify stopped word lookups and indexed collation equivalents on MySQL 8.4.11."""

import math
import pathlib
import runpy

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = native["expect"]


def verify(client, _other):
    for collation in ["utf8mb4_0900_ai_ci", "utf8mb4_0900_as_cs", "utf8mb4_bin"]:
        client.query("DROP TABLE IF EXISTS probe.docs;"
                     f"CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE {collation},FULLTEXT ft(body));"
                     "INSERT INTO probe.docs VALUES(1,'the'),(2,'thé'),(3,'THE'),(4,'tHe'),(5,'zzzz');"
                     "ANALYZE TABLE probe.docs")
        equivalent = "2" if collation == "utf8mb4_0900_ai_ci" else "NULL"
        for term, expected in [("the", equivalent), ("thé", "2"), ("THE", equivalent),
                               ("the the", equivalent), ('"the zzzz"', "5")]:
            for mode in ["IN NATURAL LANGUAGE MODE", "IN BOOLEAN MODE", "WITH QUERY EXPANSION"]:
                query = ("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                         f"WHERE MATCH(body) AGAINST('{term}' {mode})")
                expect(f"{collation} {term} {mode}", client.query(query), expected)
        expect(f"{collation} prefix", client.query(
            "SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs WHERE MATCH(body) AGAINST('th*' IN BOOLEAN MODE)"), "2")
        score = float(client.query("SELECT MATCH(body) AGAINST('thé') FROM probe.docs WHERE id=2"))
        if not math.isclose(score, math.log10(5) ** 2, rel_tol=1e-6):
            raise AssertionError(f"{collation}: document frequency includes filtered words: {score}")
        print(f"{collation} score: {score}", flush=True)


if __name__ == "__main__":
    native["run"](verify)
