"""Compare multi-column CJK ngram searches with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
Each token size uses disposable servers and data directories.
Use --legacy-punctuation, --legacy-radicals, or --legacy-range for the expanded
utf8mb4_unicode_ci corpora, --cache-lifecycle for explicit flushes,
--cache-pressure for per-table pressure, or --global-cache-pressure for
server-wide pressure.
"""

import argparse
import math
import pathlib
import re
import socket
import string
import subprocess
import tempfile
import time
import unicodedata
import xml.etree.ElementTree as ET
from collections import Counter

import pymysql

REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"

DOCUMENTS = [
    ("生日快乐", "祝福"), ("生日", "快乐"), ("中文", "生日"),
    ("日本語", "韓国語"), ("生日　快乐", "東京"), ("🙂生日", "微笑"),
    ("生🙂日", "快乐"), ("ab生日", "ことば"), ("生日abc快乐", "火曜日"),
    ("快乐", "生日快乐"), ("今日は晴れ", "日本 語"), ("한국어", "생일 축하"),
    ("生日，快乐", "中文"), ("生日!快乐", "日本語"), ("", "生日"),
    ("生日", None), ("中，文", "快乐"), ("𠮷野家", "𠮷"),
    ("生日\u200d快乐", "连字"), ("生日\ufe0f快乐", "变体"),
    ("生e\u0301日", "组合"), ("生日\n快乐", "换行"),
    ("生日。快乐", "句号"), ("生日\u00a0快乐", "空格"),
    ("한글한", "자모"), ("👩\u200d💻生日", "表情"),
    ("カタカナ", ""), ("ｶﾀｶﾅ", ""), ("日本語カタカナ", ""),
    ("ガッコウ", ""), ("ｶﾞｯｺｳ", ""), ("ガラス", ""),
    ("ｶﾞﾗｽ", ""), ("パピプペポ", ""), ("ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ", ""),
    ("㍿株式会社", "株式會社"), ("㈱株式会社", "株式"),
    ("神神社", "神社"), ("⽇本語", "日本語"), ("髙橋", "高橋"),
    ("ｼﾞｭｰｽ", "ジュース"), ("ヴァイオリン", "ｳﾞｧｲｵﾘﾝ"),
    ("ガギグゲゴ", "ｶﾞｷﾞｸﾞｹﾞｺﾞ"), ("パピプペポ", "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ"),
    ("하늘하늘", "한글"), ("々木々", "佐々木"),
    ("㎏キログラム", "キロ"), ("①一", "一①"), ("Ⅳ四", "四Ⅳ"),
    ("生\u200c日", "零宽非连接"), ("生\u2060日", "连接词"),
    ("生\u034f日", "字素连接"), ("生\u180e日", "蒙古分隔"),
    ("生\u200b日", "零宽空格"), ("カ\u3099ラス", "分解片假名"),
    ("ハ\u309aピ", "分解半浊点"), ("①②③", "数字序列"),
    ("禰\U000e0100豆子", "异体字选择"), ("禰豆子", "基字"),
    ("𠮷\U000e0100野家", "异体选择"), ("東京\ufe0f都", "文字变体"),
    ("학교", "ㅎㅏㄱㄱㅛ"), ("한글", "ㅎㅏㄴㄱㅡㄹ"),
    ("ガラス", "か\u3099らす"),
    ("時々人々", "繰返し"), ("ゝゞヽヾ", "かな"),
    ("ひらがなゟ", "カタカナヿ"), ("中文︐標點", "︑"),
    ("中文﹅標點", "中文"), ("字〆字", "〆"), ("字々字", "々"),
]

TERMS = [
    "生日快乐", "生日", "快乐", "生", "生日*", "生*", "中文", "日本語",
    "日本 語", "한국어", "생일 축하", "🙂生日", "生🙂日", "𠮷野家", "𠮷",
    "中，文", "生日，快乐", "生日!快乐", '"生日 快乐"', '"生日，快乐"',
    "+生日 +快乐", "生日 -快乐", "生日* +快乐", "ab生日", "abc", "ことば",
    "東京", "祝福", "火曜日", "生日 日本語", "+生日 -日本語", '"生日快乐"',
    "生 日", "生　日", "生日生日", "日本語日本語", "生日生日 日本語",
    "生日快乐生日快乐", "生日日生", "한국어한국어",
    "生日\u200d快乐", "生日\ufe0f快乐", "生e\u0301日", "生日\n快乐",
    "生日。快乐", "生日\u00a0快乐", "한글한", "👩\u200d💻生日",
    "カタカナ", "ｶﾀｶﾅ",
    "ガッコウ", "ｶﾞｯｺｳ", "ガラス", "ｶﾞﾗｽ", "パピプペポ", "ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ",
    "㍿", "㈱", "株式会社", "株式會社", "神", "神", "⽇", "日", "髙", "高",
    "ｼﾞｭｰｽ", "ジュース", "ヴァ", "ｳﾞｧ", "ガギ", "ｶﾞｷﾞ", "パピ", "ﾊﾟﾋﾟ",
    "하늘", "하늘", "한글", "々", "佐々木", "㎏", "キロ", "①", "一", "Ⅳ", "四",
    '"①"', '"Ⅳ"', "+①", "+Ⅳ", "①*", "Ⅳ*", "①一", "Ⅳ四",
    "生\u200c日", "生\u2060日", "生\u034f日", "生\u180e日", "生\u200b日",
    "カ\u3099ラス", "ハ\u309aピ", "①②③", "①②", "②③",
    "禰\U000e0100豆子", "禰豆子", "𠮷\U000e0100野家", "𠮷野家",
    "東京\ufe0f都", "東京都", '"禰\U000e0100豆子"', "+禰\U000e0100豆子",
    "학교", "ㅎㅏㄱㄱㅛ", "한글", "ㅎㅏㄴㄱㅡㄹ", "ガラス", "か\u3099らす",
    "時々", "人々", "ゝゞ", "ヽヾ", "ひらがなゟ", "カタカナヿ",
    "中文︐標點", "中文﹅標點", "字〆字", "字々字", "︑", "﹅",
]

MODES = ["IN NATURAL LANGUAGE MODE", "IN BOOLEAN MODE", "WITH QUERY EXPANSION"]

LEGACY_PUNCTUATION = [
    chr(code)
    for start, end in ((0x3000, 0x3040), (0xFE30, 0xFE70), (0xFF01, 0xFFA0))
    for code in range(start, end)
    if chr(code).isprintable()
] + list(string.punctuation)

RADICALS = [
    chr(code)
    for start, end in ((0x2E80, 0x2F00), (0x2F00, 0x2FE0))
    for code in range(start, end)
    if chr(code).isprintable()
]
LEGACY_RADICALS = list(dict.fromkeys(
    RADICALS + [unicodedata.normalize("NFKC", char) for char in RADICALS]
))


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def query(connection, sql, args=None):
    with connection.cursor() as cursor:
        cursor.execute(sql, args)
        return cursor.fetchall()


def setup(connection, size, documents=DOCUMENTS, collation=None):
    reported = query(connection, "SELECT @@ngram_token_size")[0][0]
    assert reported == size, (reported, size)
    column_collation = f" COLLATE {collation}" if collation else ""
    for sql in [
        "CREATE DATABASE probe", "USE probe",
        (
            "CREATE TABLE docs(id INT PRIMARY KEY,"
            f"body TEXT{column_collation},"
            f"title TEXT{column_collation},"
            "FULLTEXT KEY ft(body,title) WITH PARSER ngram)"
        ),
    ]:
        query(connection, sql)
    for index, (body, title) in enumerate(documents, 1):
        query(connection, "INSERT INTO docs VALUES(%s,%s,%s)", (index, body, title))


def result(connection, term, mode):
    sql = f"SELECT id FROM docs WHERE MATCH(body,title) AGAINST(%s {mode}) ORDER BY id"
    try:
        return tuple(row[0] for row in query(connection, sql, (term,)))
    except pymysql.MySQLError as error:
        return "error", error.args[0]


def scored_result(connection, term, mode):
    score = f"MATCH(body,title) AGAINST(%s {mode})"
    sql = f"SELECT id,{score} AS relevance FROM docs WHERE {score} ORDER BY relevance DESC,id"
    try:
        return tuple((row[0], float(row[1])) for row in query(connection, sql, (term, term)))
    except pymysql.MySQLError as error:
        return "error", error.args[0]


def scores_agree(expected, actual):
    expected_error = bool(expected) and expected[0] == "error"
    actual_error = bool(actual) and actual[0] == "error"
    if expected_error or actual_error:
        return expected == actual
    if len(expected) != len(actual):
        return False
    actual_scores = dict(actual)
    if len(actual_scores) != len(expected):
        return False
    if not all(
        id in actual_scores and math.isclose(score, actual_scores[id], rel_tol=1e-5, abs_tol=1e-5)
        for id, score in expected
    ):
        return False
    positions = {id: position for position, (id, _) in enumerate(actual)}
    return all(
        positions[first_id] < positions[second_id]
        for position, (first_id, first_score) in enumerate(expected)
        for second_id, second_score in expected[position + 1:]
        if first_score > second_score and not math.isclose(first_score, second_score, rel_tol=1e-5, abs_tol=1e-5)
    )


def cldr_pinyin_ranks(path):
    root = ET.parse(path).getroot()
    collation = next(item for item in root.findall("./collations/collation")
                     if item.attrib.get("type") == "pinyin" and "alt" not in item.attrib)
    rules = collation.find("cr").text
    rules = rules.split("# START AUTOGENERATED PINYIN LONG", 1)[1]
    rules = rules.split("# END AUTOGENERATED PINYIN LONG", 1)[0]
    ordered = "".join(re.findall(r"<\*([^#\n]+)", rules)).replace(" ", "").replace("\t", "")
    return {char: rank for rank, char in enumerate(ordered)}


def order_difference(expected, actual):
    first = next(index for index, (left, right) in enumerate(zip(expected, actual)) if left != right)
    ranks = {value: index for index, value in enumerate(expected)}
    seen = [0] * (len(actual) + 1)
    inversions = 0
    for count, value in enumerate(actual):
        index = ranks[value] + 1
        preceding = 0
        while index:
            preceding += seen[index]
            index -= index & -index
        inversions += count - preceding
        index = ranks[value] + 1
        while index < len(seen):
            seen[index] += 1
            index += index & -index
    displaced = sum(left != right for left, right in zip(expected, actual))
    return first, displaced, inversions


def probe_han_permutation(mysql_connection, fsdb_connection, collation,
                          legacy_chars, expected_order, actual_order):
    mysql_chars = [legacy_chars[row[0] - 1] for row in expected_order]
    host_chars = [legacy_chars[row[0] - 1] for row in actual_order]
    replacement = dict(zip(mysql_chars, host_chars))
    samples = legacy_chars + ["a", "A", "中", "文", "!", "〇", "0", "é", "ア", "가", "㐀", "⼀", "神", "𠀀", "😀", "e\u0301"]
    for char in legacy_chars[::max(1, len(legacy_chars) // 32)]:
        samples.extend(("A" + char, char + "A", "文" + char, char + "文"))

    for connection, transform in (
        (mysql_connection, lambda value: value),
        (fsdb_connection, lambda value: "".join(replacement.get(char, char) for char in value)),
    ):
        query(connection, f"CREATE TABLE tailor_probe(id INT PRIMARY KEY,value VARCHAR(20) COLLATE {collation},KEY idx_value(value))")
        for index, value in enumerate(samples, 1):
            query(connection, "INSERT INTO tailor_probe VALUES(%s,%s)", (index, transform(value)))
    query(fsdb_connection, f"CREATE TABLE baseline_probe(id INT PRIMARY KEY,value VARCHAR(20) COLLATE {collation},KEY idx_value(value))")
    for index, value in enumerate(samples, 1):
        query(fsdb_connection, "INSERT INTO baseline_probe VALUES(%s,%s)", (index, value))

    order_sql = "SELECT id FROM tailor_probe ORDER BY value,id"
    mysql_order = query(mysql_connection, order_sql)
    host_order = query(fsdb_connection, order_sql)
    baseline_order = query(fsdb_connection, "SELECT id FROM baseline_probe ORDER BY value,id")
    _, baseline_displaced, baseline_inversions = order_difference(mysql_order, baseline_order)
    print(f"Host ICU baseline: {len(samples)} mixed values; {baseline_displaced} displaced, {baseline_inversions} inversions")
    if mysql_order == host_order:
        first, displaced, inversions = None, 0, 0
    else:
        first, displaced, inversions = order_difference(mysql_order, host_order)
    print(f"Han permutation: {len(samples)} mixed values; {displaced} displaced, {inversions} inversions")
    if displaced:
        print("  first order difference", first,
              samples[mysql_order[first][0] - 1], samples[host_order[first][0] - 1])
    range_sql = "SELECT id FROM tailor_probe FORCE INDEX(idx_value) WHERE value < %s ORDER BY id"
    baseline_range_sql = "SELECT id FROM baseline_probe FORCE INDEX(idx_value) WHERE value < %s ORDER BY id"
    range_mismatches = []
    baseline_range_mismatches = []
    thresholds = samples
    for threshold in thresholds:
        expected = query(mysql_connection, range_sql, (threshold,))
        transformed = "".join(replacement.get(char, char) for char in threshold)
        actual = query(fsdb_connection, range_sql, (transformed,))
        if expected != actual:
            range_mismatches.append(threshold)
        if expected != query(fsdb_connection, baseline_range_sql, (threshold,)):
            baseline_range_mismatches.append(threshold)
    print(f"Host ICU baseline: {len(baseline_range_mismatches)}/{len(thresholds)} indexed-range mismatches")
    print(f"Han permutation: {len(range_mismatches)}/{len(thresholds)} indexed-range mismatches", range_mismatches[:5])
    weight_sql = f"SELECT HEX(WEIGHT_STRING(_utf8mb4%s COLLATE {collation}))"
    singleton_weights = {}
    composition_mismatches = []
    composition_values = samples + ["重庆", "重慶", "沉阳", "沈阳", "藏文", "é", "e\u0301", "æ", "ae"]
    for value in composition_values:
        parts = []
        for char in value:
            if char not in singleton_weights:
                encoded = query(mysql_connection, weight_sql, (char,))[0][0]
                singleton_weights[char] = bytes.fromhex(encoded).split(b"\x00\x00")
            parts.append(singleton_weights[char])
        expected = bytes.fromhex(query(mysql_connection, weight_sql, (value,))[0][0])
        composed = b"\x00\x00".join(b"".join(part[level] for part in parts) for level in range(3))
        if expected != composed:
            composition_mismatches.append(value)
    print(f"Han permutation: {len(composition_mismatches)}/{len(composition_values)} singleton-weight composition mismatches", composition_mismatches[:10])
    for value in ("一", "乾", "亁", "a", "A", "〇", "一a", "a一", "一乾", "乾一"):
        print("  MySQL weight", value, query(mysql_connection, weight_sql, (value,))[0][0])


def verify_cache_lifecycle(mysql, fsdb):
    documents = [("東京", ""), ("東海", ""), ("大阪", "")]
    for connection in (mysql, fsdb):
        query(connection, "SET SESSION innodb_ft_enable_stopword=OFF")
        setup(connection, 2, documents, "utf8mb4_bin")

    terms = (
        "東京",
        "東*",
        "+東*",
        "東* 東*",
        "東京 東*",
        "東* 東京",
        "東京 (東*)",
        "(東京) 東*",
        "不存在 東*",
        "東京 +東*",
        "東京 -東*",
        "東* -東京",
        "東* 京都 -東*",
        "東京 >東*",
        "東京 ~東*",
        "東京 +(+東*)",
        "東京駅 東*",
        '"東海" 東*',
        '"不存在" 東*',
        "+京都 +東* +東京",
        "+京都 +東* +(東京)",
    )
    natural_terms = ("東京", "東京 x", "東京 東北", "東京 東京", "東京 札幌", "東北")
    searches = [(term, "IN BOOLEAN MODE") for term in terms]
    for mode in ("IN NATURAL LANGUAGE MODE", "WITH QUERY EXPANSION"):
        searches.extend((term, mode) for term in natural_terms)
    seed = "INSERT INTO docs VALUES(1,'東京',''),(2,'東海',''),(3,'大阪','')"
    # Refresh the estimated document count after flushing so it cannot mask
    # changes caused by the posting lookup.
    optimize = ("OPTIMIZE TABLE docs", "ANALYZE TABLE docs")
    stages = (
        ("fresh", (), (3, 0)),
        ("analyzed", ("ANALYZE TABLE docs",), (3, 0)),
        ("table flush", ("FLUSH TABLES docs",), (3, 0)),
        ("default optimize", ("OPTIMIZE TABLE docs",), (3, 0)),
        (
            "full-text optimize",
            ("SET GLOBAL innodb_optimize_fulltext_only=ON", *optimize),
            (0, 3),
        ),
        (
            "mixed postings",
            (
                "INSERT INTO docs VALUES(4,'東北',''),(5,'東京 東海 東海','')",
                "ANALYZE TABLE docs",
            ),
            (3, 3),
        ),
        ("full-text optimize again", optimize, (0, 6)),
        (
            "populated index creation",
            (
                "DROP TABLE docs",
                "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_bin,title TEXT COLLATE utf8mb4_bin)",
                seed,
                "ALTER TABLE docs ADD FULLTEXT KEY ft(body,title) WITH PARSER ngram",
            ),
            (0, 3),
        ),
        (
            "metadata alter with mixed postings",
            (
                "INSERT INTO docs VALUES(4,'東北','')",
                "ALTER TABLE docs COMMENT='keep posting layout'",
                "ANALYZE TABLE docs",
            ),
            (1, 3),
        ),
        ("reinsert after truncate", ("TRUNCATE TABLE docs", seed), (3, 0)),
        (
            "cached frequency order",
            (
                "TRUNCATE TABLE docs",
                "INSERT INTO docs VALUES(1,'東京 東北 東京',''),(2,'東海',''),(4,'札幌',''),(3,'大阪','')",
                "ANALYZE TABLE docs",
            ),
            (5, 0),
        ),
        ("flushed frequency order", optimize, (0, 5)),
        (
            "cached frequency after deletion",
            (
                "TRUNCATE TABLE docs",
                "INSERT INTO docs VALUES(1,'東京 東北 東京',''),(3,'大阪',''),(2,'東海',''),(4,'札幌','')",
                "DELETE FROM docs WHERE id=3",
                "INSERT INTO docs VALUES(3,'大阪','')",
                "ANALYZE TABLE docs",
            ),
            (6, 0),
        ),
        (
            "deleted cached postings",
            (
                "TRUNCATE TABLE docs",
                seed,
                "INSERT INTO docs VALUES(4,'札幌',''),(99,'東京 東北','')",
                "DELETE FROM docs WHERE id=99",
                "ANALYZE TABLE docs",
            ),
            (6, 0),
        ),
        ("purged cached postings", optimize, (0, 4)),
        (
            "deleted disk posting",
            ("DELETE FROM docs WHERE id=2", "ANALYZE TABLE docs"),
            (0, 4),
        ),
        ("purged disk posting", optimize, (0, 3)),
        (
            "deleted cached gram outside a required range",
            (
                "TRUNCATE TABLE docs",
                "INSERT INTO docs VALUES(1,'京都 東京',''),(2,'東京',''),(3,'東海',''),(4,'大阪','')",
                *optimize,
                "INSERT INTO docs VALUES(99,'東京 東北','')",
                "DELETE FROM docs WHERE id=99",
                "ANALYZE TABLE docs",
            ),
            (2, 5),
        ),
    )
    failures = 0
    for stage, statements, expected_counts in stages:
        for statement in statements:
            query(mysql, statement)
            try:
                query(fsdb, statement)
            except pymysql.MySQLError as error:
                print(f"{stage}: Fsdb rejected {statement!r}: {error.args}")
                failures += 1

        query(mysql, "SET GLOBAL innodb_ft_aux_table='probe/docs'")
        posting_counts = tuple(
            query(
                mysql,
                f"SELECT COUNT(DISTINCT WORD,DOC_ID) FROM information_schema.INNODB_FT_INDEX_{source}",
            )[0][0]
            for source in ("CACHE", "TABLE")
        )
        if posting_counts != expected_counts:
            raise AssertionError(
                f"{stage}: expected MySQL cache/disk counts {expected_counts}, got {posting_counts}"
            )

        membership = relevance = 0
        for term, mode in searches:
            expected = result(mysql, term, mode)
            actual = result(fsdb, term, mode)
            if actual != expected:
                print(f"{stage}: {mode}: {term!r}: MySQL IDs {expected}, Fsdb IDs {actual}")
                membership += 1
            expected_scores = scored_result(mysql, term, mode)
            actual_scores = scored_result(fsdb, term, mode)
            if not scores_agree(expected_scores, actual_scores):
                if actual == expected:
                    print(
                        f"{stage}: {mode}: {term!r}: MySQL ranks {expected_scores}, Fsdb ranks {actual_scores}"
                    )
                relevance += 1
        print(
            f"{stage}: MySQL cache/disk postings {posting_counts}, "
            f"{membership} membership and {relevance} relevance differences in {len(searches)} queries"
        )
        failures += membership + relevance
    return failures


def verify_cache_pressure(mysql, fsdb):
    documents = [("東京", ""), ("東海", ""), ("大阪", "")]
    mode = "IN BOOLEAN MODE"
    search = "東京 東*"
    assert query(mysql, "SELECT @@innodb_ft_cache_size")[0][0] == 8_000_000
    for connection in (mysql, fsdb):
        query(connection, "SET SESSION innodb_ft_enable_stopword=OFF")
        setup(connection, 2, documents, "utf8mb4_bin")
        query(connection, "INSERT INTO docs VALUES(9999,'東京 東北','')")
        query(connection, "DELETE FROM docs WHERE id=9999")
    query(mysql, "SET GLOBAL innodb_ft_aux_table='probe/docs'")

    def posting_counts(term):
        return tuple(
            query(
                mysql,
                f"SELECT COUNT(DISTINCT DOC_ID) FROM information_schema.INNODB_FT_INDEX_{source} WHERE WORD=%s",
                (term,),
            )[0][0]
            for source in ("CACHE", "TABLE")
        )

    def disk_segments(term):
        return query(
            mysql,
            "SELECT DISTINCT FIRST_DOC_ID,LAST_DOC_ID FROM information_schema.INNODB_FT_INDEX_TABLE WHERE WORD=%s",
            (term,),
        )

    def insert_documents(start, count):
        for first in range(start, start + count, 64):
            rows = ",".join(
                f"({id},REPEAT('日',2048),'')" for id in range(first, min(first + 64, start + count))
            )
            for connection in (mysql, fsdb):
                query(connection, f"INSERT INTO docs VALUES {rows}")

    failures = 0

    def compare(stage, expected, wait_for_flush=False):
        nonlocal failures

        def read(connection):
            deadline = time.monotonic() + 5 if wait_for_flush else time.monotonic()
            while True:
                found = result(connection, search, mode)
                if found == expected or time.monotonic() >= deadline:
                    return found
                time.sleep(0.05)

        reference = read(mysql)
        assert reference == expected, (stage, expected, reference)
        actual = read(fsdb)
        print(f"{stage}: MySQL IDs {reference}, Fsdb IDs {actual}")
        failures += actual != reference

    compare("fresh", (1,))
    insert_documents(100, 64)
    compare("below pressure", (1,))
    for connection in (mysql, fsdb):
        query(connection, "START TRANSACTION")
    insert_documents(200, 448)
    for connection in (mysql, fsdb):
        query(connection, "ROLLBACK")
    compare("rolled back", (1,))
    assert posting_counts("東京") == (2, 0)

    for connection in (mysql, fsdb):
        query(connection, "START TRANSACTION")
    insert_documents(200, 448)
    for connection in (mysql, fsdb):
        query(connection, "COMMIT")
    compare("first automatic flush", (1, 2), wait_for_flush=True)

    # The deleted row still has a physical posting after a cache flush.
    assert posting_counts("東京") == (0, 2)
    first_segments = set(disk_segments("日日"))
    assert len(first_segments) > 1, first_segments
    for connection in (mysql, fsdb):
        query(connection, "ANALYZE TABLE docs")

    def frequency_ratio(connection):
        common = scored_result(connection, "東京", mode)
        rare = scored_result(connection, "大阪", mode)
        assert len(common) == len(rare) == 1, (common, rare)
        assert (common[0][0], rare[0][0]) == (1, 3), (common, rare)
        # Each live row has one occurrence. Comparing the two inverse document
        # frequencies cancels each server's independent table-row estimate.
        return 10 ** (math.sqrt(rare[0][1]) - math.sqrt(common[0][1]))

    reference = frequency_ratio(mysql)
    actual = frequency_ratio(fsdb)
    assert math.isclose(reference, 2, rel_tol=1e-5), reference
    print(f"retained frequency ratio after flush: MySQL {reference:.6f}, Fsdb {actual:.6f}")
    failures += not math.isclose(reference, actual, rel_tol=1e-5)

    for connection in (mysql, fsdb):
        query(connection, "INSERT INTO docs VALUES(4,'東南','')")
    compare("new cached prefix", (1, 2))
    assert posting_counts("東南") == (1, 0)
    insert_documents(700, 512)
    compare("second automatic flush", (1, 2, 4), wait_for_flush=True)
    assert posting_counts("東南") == (0, 1)
    second_segments = set(disk_segments("日日"))
    assert first_segments < second_segments, (first_segments, second_segments)
    print(f"MySQL disk posting segments for 日日: {len(first_segments)} then {len(second_segments)}")
    return failures


def verify_global_cache_pressure(mysql, fsdb):
    connections = (mysql, fsdb)
    tables = [f"fts{n % 2}.d{n}" for n in range(6)]
    # Isolate the generated bigrams so each occupies its own posting list.
    grams = [chr(0x3400 + n // 2048) + chr(0x4000 + n % 2048) for n in range(15000)]
    documents = [" ".join(grams[first:first + 100]) for first in range(0, len(grams), 100)]

    for connection in connections:
        assert query(connection, "SELECT @@innodb_ft_total_cache_size")[0][0] == 32_000_000
        query(connection, "SET SESSION innodb_ft_enable_stopword=OFF")
        query(connection, "CREATE DATABASE fts0")
        query(connection, "CREATE DATABASE fts1")
        query(connection, "CREATE TABLE fts0.plain(id INT PRIMARY KEY)")
        for table in tables:
            query(connection, f"CREATE TABLE {table}(id INT PRIMARY KEY,flag INT DEFAULT 0,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram) COLLATE=utf8mb4_bin")
            query(connection, f"INSERT INTO {table}(id,body) VALUES(1,'東京'),(2,'東海'),(3,'大阪')")

    for table in tables:
        for first in range(0, len(documents), 25):
            batch = list(enumerate(documents[first:first + 25], first + 10))
            placeholders = ",".join(["(%s,%s)"] * len(batch))
            values = [value for row in batch for value in row]
            for connection in connections:
                query(connection, f"INSERT INTO {table}(id,body) VALUES {placeholders}", values)

    def read(connection):
        return tuple(
            tuple(row[0] for row in query(connection,
                f"SELECT id FROM {table} WHERE MATCH(body) AGAINST('東京 東*' IN BOOLEAN MODE) ORDER BY id"))
            for table in tables
        )

    failures = 0

    def check(stage, expected, wait_for_sync=False):
        nonlocal failures

        def observe(connection):
            deadline = time.monotonic() + (5 if wait_for_sync else 0)
            while True:
                found = read(connection)
                if found == expected or time.monotonic() >= deadline:
                    return found
                time.sleep(0.05)

        reference = observe(mysql)
        assert reference == expected, (stage, reference)
        actual = observe(fsdb)
        print(f"{stage}: MySQL {reference}, Fsdb {actual}")
        failures += reference != actual

    cached = ((1,),) * len(tables)
    check("filled", cached)
    time.sleep(6)
    check("idle above limit", cached)
    for connection in connections:
        query(connection, "SET GLOBAL innodb_optimize_fulltext_only=ON")
        query(connection, "OPTIMIZE TABLE fts0.plain")
        query(connection, "UPDATE fts1.d5 SET flag=1 WHERE id=1")
        query(connection, "START TRANSACTION")
        query(connection, "INSERT INTO fts0.d4(id,body) VALUES(200,'東山')")
        query(connection, "ROLLBACK")
    check("plain optimize, non-indexed update, and rollback", cached)

    for connection in connections:
        query(connection, "START TRANSACTION")
        query(connection, "INSERT INTO fts0.d0(id,body) VALUES(200,'山')")
        query(connection, "COMMIT")
    first_synced = ((1, 2),) + cached[1:]
    check("indexed write with no searchable bigram", first_synced, wait_for_sync=True)
    time.sleep(6)
    for connection in connections:
        query(connection, "INSERT INTO fts1.d5(id,body) VALUES(200,'東山')")
    time.sleep(0.5)
    check("below total limit", first_synced)
    return failures


def verify(size, legacy_punctuation=False, legacy_radicals=False, legacy_range=None,
           collation_override=None, legacy_term_range=None, weight_checks=False,
           mixed_checks=False, order_checks=False, cldr_pinyin=None,
           tailoring_probe=False, all_range_scalars=False, cache_lifecycle=False, cache_pressure=False,
           global_cache_pressure=False):
    prefix = '中' * (size - 1)
    if legacy_radicals:
        legacy_chars = LEGACY_RADICALS
    elif legacy_punctuation:
        legacy_chars = LEGACY_PUNCTUATION
    elif legacy_range is not None:
        legacy_chars = [chr(code) for code in range(*legacy_range)
                        if not 0xD800 <= code < 0xE000
                        and (all_range_scalars or chr(code).isprintable())]
    else:
        legacy_chars = None
    documents = ([(prefix + char + '文', '') for char in legacy_chars]
                 if legacy_chars is not None else DOCUMENTS)
    if legacy_range == (0x2F800, 0x2F900):
        documents += [(prefix + char + '文', '') for char in ("𠀀", "😀", "丽", "並")]
    if legacy_range == (0xAC00, 0xAD00):
        documents += [(prefix + char + '文', '') for char in ("가", "ㄱㅏ", "각")]
    if legacy_range == (0x1DC0, 0x1E00):
        documents += [(prefix + '\u036c' + '文', '')]
    term_chars = (legacy_chars if legacy_term_range is None else
                  [char for char in legacy_chars if legacy_term_range[0] <= ord(char) < legacy_term_range[1]])
    terms = ([prefix + char for char in term_chars]
             if legacy_chars is not None else TERMS)
    if legacy_range is not None:
        terms += [f'"{prefix + char}"' for char in term_chars]
    if legacy_range in ((0xFE20, 0xFE30), (0x1AB0, 0x1B00),
                        (0x1DC0, 0x1E00), (0x20D0, 0x2100)):
        terms += [prefix + char + '文' for char in legacy_chars]
        terms += [char + prefix for char in legacy_chars]
    if legacy_range == (0x1DC0, 0x1E00):
        terms += [prefix + '\u036c', '\u036c' + prefix, prefix + '\u036c' + '文']
    if legacy_range == (0xAC00, 0xAD00):
        terms += [prefix + char for char in ("가", "각", "가", "ㄱㅏ", "각")]
    modes = MODES
    collation = collation_override or ("utf8mb4_unicode_ci" if legacy_chars is not None else None)
    cache_options = ["--innodb-ft-cache-size=80000000", "--innodb-ft-total-cache-size=32000000"] if global_cache_pressure else []
    with tempfile.TemporaryDirectory(prefix=f"fsdb-ngram-{size}-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql"
        data.mkdir()
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (root / "mysql.log").open("w") as mysql_log, (root / "fsdb.log").open("w") as fsdb_log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                     "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                     "--innodb-redo-log-capacity=64M", f"--ngram-token-size={size}", *cache_options],
                    stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                version = query(mysql_connection, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version

                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port), "--ngram-token-size", str(size), *cache_options],
                    stdout=fsdb_log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)
                if cache_lifecycle:
                    return verify_cache_lifecycle(mysql_connection, fsdb_connection)
                if cache_pressure:
                    return verify_cache_pressure(mysql_connection, fsdb_connection)
                if global_cache_pressure:
                    return verify_global_cache_pressure(mysql_connection, fsdb_connection)
                for connection in (mysql_connection, fsdb_connection):
                    setup(connection, size, documents, collation)

                if legacy_punctuation:
                    representative = {
                        index for index, char in enumerate(LEGACY_PUNCTUATION, 1)
                        if char in ("︵", "﹙", "（")
                    }
                    matched = result(mysql_connection, prefix + "︵", "IN NATURAL LANGUAGE MODE")
                    assert representative.issubset(set(matched)), (size, matched)
                # The legacy Unicode collation equates Kangxi and ideograph
                # spellings; general_ci assigns the radical its own weight.
                if legacy_radicals and collation != "utf8mb4_general_ci":
                    representative = {
                        index for index, char in enumerate(legacy_chars, 1)
                        if char in ("⼀", "一")
                    }
                    matched = result(mysql_connection, prefix + "⼀", "IN NATURAL LANGUAGE MODE")
                    assert representative.issubset(set(matched)), (size, matched)
                if legacy_range == (0x1B000, 0x1B130):
                    matched = result(mysql_connection, prefix + "𛀀", "IN NATURAL LANGUAGE MODE")
                    assert 1 in matched, (size, matched)

                mismatches = []
                equality_checks = 0
                weight_count = 0
                if order_checks:
                    for connection in (mysql_connection, fsdb_connection):
                        query(connection, "CREATE INDEX idx_body ON docs(body(20))")
                    order_sql = "SELECT id FROM docs ORDER BY body,id"
                    expected_order = query(mysql_connection, order_sql)
                    actual_order = query(fsdb_connection, order_sql)
                    if tailoring_probe:
                        probe_han_permutation(mysql_connection, fsdb_connection, collation,
                                              legacy_chars, expected_order, actual_order)
                    if cldr_pinyin is not None:
                        ranks = cldr_pinyin_ranks(cldr_pinyin)
                        ranked = {index: ranks[body[len(prefix)]]
                                  for index, (body, _) in enumerate(documents, 1)
                                  if len(body) > len(prefix) and body[len(prefix)] in ranks}
                        mysql_ranked = [row[0] for row in expected_order if row[0] in ranked]
                        cldr_ranked = sorted(ranked, key=lambda index: ranked[index])
                        if mysql_ranked == cldr_ranked:
                            print(f"CLDR 30 pinyin: {len(ranked)}/{len(documents)} ranked, exact MySQL order")
                        else:
                            cldr_first, cldr_displaced, cldr_inversions = order_difference(mysql_ranked, cldr_ranked)
                            mysql_positions = {index: position for position, index in enumerate(mysql_ranked)}
                            largest_shifts = sorted(
                                ((abs(position - mysql_positions[index]), index, mysql_positions[index], position)
                                 for position, index in enumerate(cldr_ranked)), reverse=True)[:3]
                            print(f"CLDR 30 pinyin: {len(ranked)}/{len(documents)} ranked, "
                                  f"first difference {cldr_first}: MySQL {mysql_ranked[cldr_first]}, "
                                  f"CLDR {cldr_ranked[cldr_first]}; {cldr_displaced} displaced, "
                                  f"{cldr_inversions} inversions; largest shifts {largest_shifts}")
                    if actual_order != expected_order:
                        first, displaced, inversions = order_difference(expected_order, actual_order)
                        mismatches.append(("collation order", first, displaced, inversions,
                                           expected_order[first:first + 10], actual_order[first:first + 10]))
                        expected_id = expected_order[first][0]
                        actual_id = actual_order[first][0]
                        comparison_sql = f"SELECT _utf8mb4%s COLLATE {collation} < _utf8mb4%s COLLATE {collation}"
                        pair = (documents[expected_id - 1][0], documents[actual_id - 1][0])
                        expected_comparison = query(mysql_connection, comparison_sql, pair)
                        actual_comparison = query(fsdb_connection, comparison_sql, pair)
                        if actual_comparison != expected_comparison:
                            mismatches.append(("collation comparison", pair, expected_comparison, actual_comparison))
                        range_sql = "SELECT id FROM docs FORCE INDEX (idx_body) WHERE body < %s ORDER BY id"
                        expected_range = query(mysql_connection, range_sql, (pair[1],))
                        actual_range = query(fsdb_connection, range_sql, (pair[1],))
                        if actual_range != expected_range:
                            first_range = next((index for index, (expected, actual) in enumerate(zip(expected_range, actual_range)) if expected != actual), min(len(expected_range), len(actual_range)))
                            mismatches.append(("collation indexed range", pair[1], first_range,
                                               expected_range[first_range:first_range + 10], actual_range[first_range:first_range + 10]))
                if legacy_range is not None:
                    equality_sql = (
                        f"SELECT _utf8mb4%s COLLATE {collation} "
                        f"= _utf8mb4%s COLLATE {collation}"
                    )
                    for char in legacy_chars:
                        expanded = unicodedata.normalize("NFKC", char)
                        if expanded == char:
                            continue
                        equality_checks += 1
                        expected = query(mysql_connection, equality_sql, (char, expanded))
                        actual = query(fsdb_connection, equality_sql, (char, expanded))
                        if actual != expected:
                            mismatches.append(("legacy equality", char, expanded, expected, actual))
                        if mixed_checks:
                            for prefix, suffix in (("A", ""), ("", "A"), ("文", ""), ("", "文")):
                                written = prefix + char + suffix
                                base = prefix + expanded + suffix
                                equality_checks += 1
                                expected = query(mysql_connection, equality_sql, (written, base))
                                actual = query(fsdb_connection, equality_sql, (written, base))
                                if actual != expected:
                                    mismatches.append(("legacy equality", written, base, expected, actual))
                    extra_pairs = []
                    if legacy_range == (0x3000, 0x3200):
                        kana = "クシストヌハヒフヘホムラリルレロ"
                        extra_pairs = [(chr(0x31F0 + offset), char) for offset, char in enumerate(kana)]
                        extra_pairs += [("ゟ", "より"), ("ヿ", "コト"), ("ㄍ", "ㆣ"),
                                        ("ㄮ", "ㄜ"), ("ㆻ", "ㄍ")]
                    elif legacy_range == (0x2100, 0x2200):
                        extra_pairs = [("℁", "⅍")]
                    elif legacy_range == (0xFA00, 0xFB00):
                        extra_pairs = [("郞", "郞"), ("並", "並"),
                                       ("逸", "逸"), ("艹", "艹")]
                    elif legacy_range in ((0x2F800, 0x2F900), (0x2F800, 0x2FA20)):
                        extra_pairs = [("𢌱", "𢌱"), ("𥪧", "𥪧"), ("丽", "丸"), ("丽", "𠀀"), ("丽", "😀"),
                                       ("丽", "\U00010000"), ("丽", "\U0001D11E"),
                                       ("丽", "\U000E0100"), ("丽", "\U0010FFFD"),
                                       ("丽", "丽"), ("丽", "並")]
                    for left, right in extra_pairs:
                        equality_checks += 1
                        expected = query(mysql_connection, equality_sql, (left, right))
                        actual = query(fsdb_connection, equality_sql, (left, right))
                        if actual != expected:
                            mismatches.append(("legacy equality", left, right, expected, actual))
                    if weight_checks:
                        weight_sql = f"SELECT HEX(WEIGHT_STRING(_utf8mb4%s COLLATE {collation}))"
                        for char in legacy_chars:
                            samples = [char]
                            if mixed_checks:
                                samples += [prefix + char + suffix for prefix, suffix in
                                            (("A", ""), ("", "A"), ("文", ""), ("", "文"))]
                            for sample in samples:
                                weight_count += 1
                                expected = query(mysql_connection, weight_sql, (sample,))
                                actual = query(fsdb_connection, weight_sql, (sample,))
                                if actual != expected:
                                    mismatches.append(("legacy weight", sample, expected, actual))
                for mode in modes:
                    for term in terms:
                        expected = result(mysql_connection, term, mode)
                        actual = result(fsdb_connection, term, mode)
                        if actual != expected:
                            mismatches.append((mode, term, expected, actual))
                        expected_scores = scored_result(mysql_connection, term, mode)
                        actual_scores = scored_result(fsdb_connection, term, mode)
                        if not scores_agree(expected_scores, actual_scores):
                            mismatches.append((mode, term, "relevance", expected_scores, actual_scores))
                equality_mismatches = sum(item[0] == "legacy equality" for item in mismatches)
                weight_mismatches = sum(item[0] == "legacy weight" for item in mismatches)
                order_mismatches = sum(item[0] in ("collation order", "collation comparison", "collation indexed range") for item in mismatches)
                search_mismatches = len(mismatches) - equality_mismatches - weight_mismatches - order_mismatches
                print(
                    f"size {size}: {len(modes) * len(terms)} ngram queries, "
                    f"{equality_checks} legacy equalities; {search_mismatches} search and "
                    f"{equality_mismatches} equality mismatches"
                    + (f"; {weight_count} weights, {weight_mismatches} mismatches" if weight_checks else "")
                    + (f"; {order_mismatches} order mismatches" if order_checks else "")
                )
                search_kinds = Counter(
                    (item[0], "relevance" if len(item) == 5 else "membership")
                    for item in mismatches
                    if item[0] not in ("legacy equality", "legacy weight", "collation order", "collation comparison", "collation indexed range")
                )
                for (mode, kind), count in sorted(search_kinds.items()):
                    print(f"  {mode} {kind}: {count}")
                for mismatch in mismatches[:40]:
                    print(mismatch)
                if len(mismatches) > 40:
                    print(f"... {len(mismatches) - 40} more mismatches")
                return len(mismatches)
            finally:
                if fsdb_connection:
                    fsdb_connection.close()
                if mysql_connection:
                    mysql_connection.close()
                if fsdb and fsdb.poll() is None:
                    fsdb.terminate()
                    fsdb.wait(timeout=20)
                if mysql and mysql.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                        check=False,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sizes", nargs="*", type=int)
    corpus = parser.add_mutually_exclusive_group()
    corpus.add_argument("--legacy-punctuation", action="store_true")
    corpus.add_argument("--legacy-radicals", action="store_true")
    corpus.add_argument("--legacy-range", help="hexadecimal Unicode range START:END for legacy-collation CJK probes")
    corpus.add_argument("--cache-lifecycle", action="store_true", help="compare binary bigram ranks across cache and disk transitions")
    corpus.add_argument("--cache-pressure", action="store_true", help="compare automatic binary-bigram cache flushes under write load")
    corpus.add_argument("--global-cache-pressure", action="store_true", help="compare full-text sync under server-wide pressure")
    parser.add_argument("--all-range-scalars", action="store_true", help="include range scalars missing from Python's Unicode character database")
    parser.add_argument("--legacy-term-range", help="search only this subrange of --legacy-range while indexing the whole range")
    parser.add_argument("--weight-checks", action="store_true", help="compare exact WEIGHT_STRING bytes for --legacy-range")
    parser.add_argument("--order-checks", action="store_true", help="compare document ordering under the selected collation")
    parser.add_argument("--cldr-pinyin", type=pathlib.Path, help="compare MySQL order with a local CLDR 30 zh.xml pinyin rule file")
    parser.add_argument("--tailoring-probe", action="store_true", help="test a Han-rank permutation on mixed text and indexed ranges")
    parser.add_argument("--mixed-checks", action="store_true", help="compare compatibility forms wrapped with ASCII or Han")
    parser.add_argument("--collation", choices=("utf8mb4_unicode_ci", "utf8mb4_unicode_520_ci", "utf8mb4_general_ci", "utf8mb4_0900_ai_ci", "utf8mb4_0900_as_ci", "utf8mb4_ja_0900_as_cs_ks", "utf8mb4_0900_as_cs", "utf8mb4_zh_0900_as_cs"))
    args = parser.parse_args()
    if args.cache_lifecycle or args.cache_pressure or args.global_cache_pressure:
        if args.sizes not in ([], [2]) or args.collation:
            parser.error("the cache probes use binary bigrams")
        raise SystemExit(bool(verify(2, cache_lifecycle=args.cache_lifecycle, cache_pressure=args.cache_pressure,
                                     global_cache_pressure=args.global_cache_pressure)))
    legacy_range = None
    if args.legacy_range:
        try:
            legacy_range = tuple(int(value, 16) for value in args.legacy_range.split(":"))
            if len(legacy_range) != 2 or not 0 <= legacy_range[0] < legacy_range[1] <= 0x110000:
                raise ValueError
        except ValueError:
            parser.error("--legacy-range requires hexadecimal START:END")
    legacy_term_range = None
    if args.legacy_term_range:
        if legacy_range is None:
            parser.error("--legacy-term-range requires --legacy-range")
        try:
            legacy_term_range = tuple(int(value, 16) for value in args.legacy_term_range.split(":"))
            if (len(legacy_term_range) != 2 or
                    not legacy_range[0] <= legacy_term_range[0] < legacy_term_range[1] <= legacy_range[1]):
                raise ValueError
        except ValueError:
            parser.error("--legacy-term-range must be a subrange of --legacy-range")
    if args.weight_checks and legacy_range is None:
        parser.error("--weight-checks requires --legacy-range")
    if args.all_range_scalars and legacy_range is None:
        parser.error("--all-range-scalars requires --legacy-range")
    if args.mixed_checks and legacy_range is None:
        parser.error("--mixed-checks requires --legacy-range")
    if args.cldr_pinyin and not args.order_checks:
        parser.error("--cldr-pinyin requires --order-checks")
    if args.tailoring_probe and (not args.order_checks or not args.legacy_range or args.collation != "utf8mb4_zh_0900_as_cs"):
        parser.error("--tailoring-probe requires --order-checks, --legacy-range, and Chinese 0900 collation")
    sizes = args.sizes or list(range(1, 11))
    raise SystemExit(1 if sum(verify(size, args.legacy_punctuation, args.legacy_radicals,
                                     legacy_range, args.collation, legacy_term_range,
                                     args.weight_checks, args.mixed_checks, args.order_checks,
                                     args.cldr_pinyin, args.tailoring_probe,
                                     args.all_range_scalars) for size in sizes) else 0)
