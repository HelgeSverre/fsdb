"""Verify full-text case folding on disposable native MySQL 8.4.11."""

import math
import pathlib
import runpy

native = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))
expect = native["expect"]
modes = ["IN NATURAL LANGUAGE MODE", "IN BOOLEAN MODE", "WITH QUERY EXPANSION"]


def matches(client, term, mode):
    return client.query("SELECT GROUP_CONCAT(id ORDER BY id) FROM probe.docs "
                        f"WHERE MATCH(body) AGAINST('{term}' {mode})")


def verify(client, _other):
    client.query("SET SESSION innodb_ft_enable_stopword=OFF")
    for collation in ["utf8mb4_0900_ai_ci", "utf8mb4_0900_as_cs", "utf8mb4_bin", "utf8mb4_0900_bin"]:
        for parser in ["", " WITH PARSER ngram"]:
            client.query("DROP TABLE IF EXISTS probe.docs;"
                         f"CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE {collation},FULLTEXT ft(body){parser});"
                         "INSERT INTO probe.docs VALUES(1,'orchard cobalt'),(2,'Orchard Cobalt'),(3,'ORCHARD COBALT'),(4,'zzzz');"
                         "ANALYZE TABLE probe.docs")
            binary = collation.endswith("_bin")
            # Binary phrase verification is a separate compatibility boundary.
            if binary and parser:
                continue
            for term, binary_ids in [("orchard", "1"), ("Orchard", "2"), ("ORCHARD", "3")]:
                for mode in modes:
                    expect(f"{collation}{parser} {term} {mode}", matches(client, term, mode), binary_ids if binary else "1,2,3")
            for term, binary_ids in [("orch*", "1"), ("Orch*", "2"), ("ORCH*", "3")]:
                expect(f"{collation}{parser} {term}", matches(client, term, modes[1]), binary_ids if binary else "1,2,3")
            if not binary:
                for term in ['"orchard cobalt"', '"Orchard Cobalt"']:
                    for mode in modes:
                        expect(f"{collation}{parser} {term} {mode}", matches(client, term, mode), "1,2,3")
                client.query("DELETE FROM probe.docs WHERE id=2;UPDATE probe.docs SET body='violet violet' WHERE id=3")
                expect(f"{collation}{parser} remove prefix", matches(client, "ORCH*", modes[1]), "1")
                expect(f"{collation}{parser} remove word", matches(client, "Orchard", modes[0]), "1")

    client.query("DROP TABLE probe.docs;"
                 "CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_0900_as_cs,FULLTEXT ft(body));"
                 "INSERT INTO probe.docs VALUES(1,'cafe'),(2,'café'),(3,'CAFÉ'),(4,'zzzz');ANALYZE TABLE probe.docs")
    for mode in modes:
        expect(f"unaccented {mode}", matches(client, "CAFE", mode), "1")
        expect(f"accented {mode}", matches(client, "café", mode), "2,3")
    score = float(client.query("SELECT MATCH(body) AGAINST('CAFÉ') FROM probe.docs WHERE id=2"))
    if not math.isclose(score, math.log10(2) ** 2, rel_tol=1e-6):
        raise AssertionError(f"case variants have separate document frequencies: {score}")
    print(f"case-folded score: {score}", flush=True)


if __name__ == "__main__":
    native["run"](verify)
