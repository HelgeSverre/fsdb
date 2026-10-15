"""Pin correlated text equality/range OR results on native MySQL 8.4.11."""

import pathlib
import runpy


oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def verify(client, _writer):
    values = ",".join(f"({number},'a{number:03d}')" for number in range(1000))
    client.query(
        "USE probe;CREATE TABLE text_or_bounds(id INT PRIMARY KEY,lo VARCHAR(8) COLLATE utf8mb4_bin,"
        "hi VARCHAR(8) COLLATE utf8mb4_bin,lo2 VARCHAR(8) COLLATE utf8mb4_bin,"
        "hi2 VARCHAR(8) COLLATE utf8mb4_bin,exact_code VARCHAR(8) COLLATE utf8mb4_bin);"
        "CREATE TABLE text_or_rows(id INT PRIMARY KEY,code VARCHAR(8) COLLATE utf8mb4_bin,"
        "KEY ix_code(code),KEY ix_lower ((LOWER(code))));"
        "INSERT INTO text_or_bounds VALUES(1,'a010','a012','a011','a013','a015'),"
        "(2,'a020','a022','a021','a023','a025');"
        "CREATE TABLE text_not_bounds(id INT PRIMARY KEY,lo VARCHAR(8) COLLATE utf8mb4_bin,"
        "hi VARCHAR(8) COLLATE utf8mb4_bin);"
        "INSERT INTO text_not_bounds VALUES(3,NULL,'a990'),(4,'a010',NULL);"
        f"INSERT INTO text_or_rows VALUES{values}"
    )

    sources = [
        ("physical", "", "text_or_rows o"),
        ("pass-through", "", "(SELECT id,code FROM text_or_rows) o"),
        ("materialized", "", "(SELECT id,CONCAT(code,'') AS code FROM text_or_rows LIMIT 1000) o"),
    ]
    predicates = [
        ("equality/range", "o.code BETWEEN u.lo AND u.hi OR o.code=u.exact_code"),
        ("range/range", "o.code BETWEEN u.lo AND u.hi OR o.code BETWEEN u.lo2 AND u.hi2"),
    ]
    for source_name, prefix, source in sources:
        for predicate_name, predicate in predicates:
            query = (prefix + " SELECT u.id,(SELECT COUNT(*) FROM " + source + " WHERE " + predicate + ") "
                     "FROM text_or_bounds u ORDER BY u.id")
            oracle["expect"](source_name + " " + predicate_name, client.query(query), "1\t4\n2\t4")

        query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source
                 + " WHERE o.code NOT BETWEEN u.lo AND 'a990') "
                 "FROM text_or_bounds u ORDER BY u.id")
        oracle["expect"](source_name + " text NOT BETWEEN", client.query(query), "1\t19\n2\t29")
        null_query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source
                      + " WHERE o.code NOT BETWEEN u.lo AND u.hi) "
                      "FROM text_not_bounds u ORDER BY u.id")
        oracle["expect"](source_name + " text NOT BETWEEN nullable", client.query(null_query), "3\t9\n4\t10")

    for source_name, _, source in sources[:2]:
        query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source
                 + " WHERE LOWER(o.code) NOT BETWEEN LOWER(u.lo) AND 'a990') "
                 "FROM text_or_bounds u ORDER BY u.id")
        oracle["expect"](source_name + " functional text NOT BETWEEN", client.query(query), "1\t19\n2\t29")

        for predicate_name, predicate in [
            ("functional equality/range", "LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi) "
                                          "OR LOWER(o.code)=LOWER(u.exact_code)"),
            ("functional range/range", "LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi) "
                                       "OR LOWER(o.code) BETWEEN LOWER(u.lo2) AND LOWER(u.hi2)"),
        ]:
            query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source + " WHERE " + predicate + ") "
                     "FROM text_or_bounds u ORDER BY u.id")
            oracle["expect"](source_name + " " + predicate_name, client.query(query), "1\t4\n2\t4")

    for source_name, _, source in sources[:2]:
        for predicate_name, predicate in [
            ("functional equality/equality", "LOWER(o.code)=LOWER(u.lo) "
                                             "OR LOWER(o.code)=LOWER(u.exact_code)"),
            ("mixed equality/equality", "o.code=u.lo OR LOWER(o.code)=LOWER(u.exact_code)"),
        ]:
            query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source + " WHERE " + predicate + ") "
                     "FROM text_or_bounds u ORDER BY u.id")
            oracle["expect"](source_name + " " + predicate_name, client.query(query), "1\t2\n2\t2")

    for source_name, _, source in sources[:2]:
        for predicate_name, predicate in [
            ("mixed equality/range", "o.code=u.exact_code OR "
                                     "LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi)"),
            ("mixed range/range", "o.code BETWEEN u.lo AND u.hi OR "
                                  "LOWER(o.code) BETWEEN LOWER(u.lo2) AND LOWER(u.hi2)"),
        ]:
            query = ("SELECT u.id,(SELECT COUNT(*) FROM " + source + " WHERE " + predicate + ") "
                     "FROM text_or_bounds u ORDER BY u.id")
            oracle["expect"](source_name + " " + predicate_name, client.query(query), "1\t4\n2\t4")

    oracle["expect"](
        "uncovered functional branch",
        client.query("SELECT u.id,(SELECT COUNT(*) FROM text_or_rows o "
                     "WHERE LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi) OR MOD(o.id,1000)=1) "
                     "FROM text_or_bounds u ORDER BY u.id"),
        "1\t4\n2\t4",
    )


if __name__ == "__main__":
    oracle["run"](verify)
