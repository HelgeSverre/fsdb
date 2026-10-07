"""Verify binary full-text phrase matching on disposable MySQL 8.4.11."""

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
    for collation in ["utf8mb4_bin", "utf8mb4_0900_bin"]:
        for parser in ["", " WITH PARSER ngram"]:
            client.query("DROP TABLE IF EXISTS probe.docs;"
                         f"CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE {collation},FULLTEXT ft(body){parser});"
                         "INSERT INTO probe.docs VALUES(1,'orchard cobalt'),(2,'Orchard Cobalt'),(3,'ORCHARD COBALT'),"
                         "(4,'Orchard cobalt orchard'),(5,'orchard Cobalt cobalt'),(6,'orchard cobalt Orchard Cobalt'),(7,'zzzz');"
                         "ANALYZE TABLE probe.docs")
            if parser:
                cases = [
                    ('"orchard cobalt"', ["1,2,4,5,6", "1,5,6", "1,2,4,5,6"]),
                    ('"Orchard cobalt"', ["1,2,4,5,6", "NULL", "1,2,4,5,6"]),
                    ('"orchard Cobalt"', ["1,2,4,5,6", "NULL", "1,2,4,5,6"]),
                    ('"Orchard Cobalt"', ["1,2,4,5,6", "NULL", "1,2,4,5,6"]),
                    ('Orchard', ["1,2,4,5,6", "NULL", "1,2,4,5,6"]),
                    ('"ORCHARD"', ["3", "NULL", "3"]),
                    ('"Or"', ["2,4,6", "2,4,6", "1,2,4,5,6"]),
                    ('"Orchard Cobalt" @10', ["1,2,4,5,6", "NULL", "1,2,4,5,6"]),
                ]
            else:
                cases = [
                    ('"orchard cobalt"', ["1,5,6", "1,5,6", "1,2,4,5,6"]),
                    ('"Orchard cobalt"', ["NULL"] * 3),
                    ('"orchard Cobalt"', ["NULL"] * 3),
                    ('"Orchard Cobalt"', ["NULL"] * 3),
                    ('"Orchard"', ["2,4,6", "2,4,6", "1,2,4,5,6"]),
                    ('"ORCHARD"', ["3"] * 3),
                    ('"Orchard Cobalt" @10', ["NULL", "2,6", "NULL"]),
                    ('"orchard cobalt" @10', ["1,5,6", "1,4,5,6", "1,2,4,5,6"]),
                ]
            for term, expected in cases:
                for mode, ids in zip(modes, expected):
                    expect(f"{collation}{parser} {term} {mode}", matches(client, term, mode), ids)

        client.query("DROP TABLE probe.docs;"
                     f"CREATE TABLE probe.docs(id INT PRIMARY KEY,body TEXT COLLATE {collation},FULLTEXT ft(body));"
                     "INSERT INTO probe.docs VALUES(1,'orchard Cobalt'),(2,'orchard XX cobalt'),"
                     "(3,'orchard café'),(4,'orchard CAFÉ café'),(5,'zzzz')")
        for term, expected in [('"orchard cobalt"', "NULL"), ('"orchard xx cobalt"', "2"),
                               ('"orchard XX cobalt"', "NULL"), ('"orchard café"', "3,4"),
                               ('"orchard CAFÉ"', "NULL")]:
            for mode in modes[:2]:
                expect(f"{collation} internal {term} {mode}", matches(client, term, mode), expected)


if __name__ == "__main__":
    native["run"](verify)
