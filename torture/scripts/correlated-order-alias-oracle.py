"""Verify correlated ORDER BY alias scopes on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('CREATE TABLE ordering_scope(v INT,w INT);INSERT INTO ordering_scope VALUES(2,10),(1,20)', ''),
    ("SELECT _latin1'a' AS a FROM ordering_scope ORDER BY (SELECT a COLLATE utf8mb4_bin)", (1253, '42000')),
    ('SELECT -v AS a FROM ordering_scope ORDER BY -(SELECT a)', 'a\n-1\n-2\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY ABS((SELECT a)+3)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a)', 'a\n-2\n-1\n'),
    ('SELECT -v AS v FROM ordering_scope ORDER BY (SELECT v)', 'v\n-1\n-2\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a+0)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT (SELECT a))', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS a) t)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS x) t)', 'a\n-2\n-1\n'),
    ('SELECT v AS a,w AS a FROM ordering_scope ORDER BY (SELECT a)', (1052, '23000')),
    ('SELECT v AS a,-v AS a FROM ordering_scope ORDER BY (SELECT a)', 'a\ta\n2\t-2\n1\t-1\n'),
    ('SELECT SUM(-v) AS a FROM ordering_scope GROUP BY v ORDER BY (SELECT a)', (1247, '42S22')),
    ('SELECT ROW_NUMBER() OVER (ORDER BY v DESC) AS a FROM ordering_scope ORDER BY (SELECT a)', (3594, 'HY000')),
    ('SELECT -v AS a FROM ordering_scope WHERE FALSE ORDER BY (SELECT a)', ''),
    ('SET @n=0;SELECT (@n:=@n+1) AS a FROM ordering_scope ORDER BY (SELECT a);SELECT @n', 'a\n1\n2\n@n\n2\n'),
    ('SET @n=0;SELECT (@n:=@n+1) AS a FROM ordering_scope ORDER BY (SELECT a+0);SELECT @n', 'a\n1\n2\n@n\n2\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY EXISTS(SELECT a)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT 1 WHERE a=-1)', 'a\n-2\n-1\n'),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT ordering_scope.a)', (1054, '42S22')),
    ('SELECT -v AS a FROM ordering_scope ORDER BY (SELECT a FROM ordering_scope inner_scope LIMIT 1)', 'a\n-2\n-1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v ORDER BY i.v DESC) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v ORDER BY w DESC) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v ORDER BY i.w DESC,i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v ORDER BY i.w+1 DESC) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v ORDER BY ABS(i.w) DESC,w+1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 AS w FROM ordering_scope i WHERE i.v=o.v ORDER BY w) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 AS w FROM ordering_scope i WHERE i.v=o.v ORDER BY w+1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 ORDER BY i.v DESC) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY v LIMIT 1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT i.w FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (i.v IS NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (v IS NOT NULL) LIMIT 1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY NOT (i.v IS NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (i.v <=> NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (NULL <=> v) LIMIT 1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY NOT (i.v <=> NULL) OR (i.w IS NOT NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (i.v IS NULL) OR (i.w IS NOT NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY (i.v IS NOT NULL) AND NOT (i.w IS NULL) LIMIT 1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT i.v IS NULL FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT (i.v <=> NULL) FROM ordering_scope i WHERE i.v=o.v GROUP BY (i.v <=> NULL)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT NOT (i.v IS NULL) FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT i.* FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT i.v AS group_key FROM ordering_scope i WHERE i.v=o.v GROUP BY group_key) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT i.v IS NULL AS group_key FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY group_key) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT NOT i.v AS group_key FROM ordering_scope i WHERE i.v=o.v GROUP BY group_key) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v HAVING COUNT(*)>0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING COUNT(*)>0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v HAVING COUNT(*)<>0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v HAVING 0!=COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v HAVING COUNT(*)>0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v HAVING COUNT(*)>0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v HAVING 0<COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v HAVING COUNT(*)>=1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v GROUP BY i.v HAVING 1<=COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING COUNT(*)=0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING 0=COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v HAVING COUNT(*)=0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING COUNT(*)<1) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING 1>COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 HAVING COUNT(*)<=0) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t1\n2\t1\n'),
    ('SELECT o.v,EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=o.v+10 GROUP BY i.v HAVING 0>=COUNT(*)) AS hit FROM ordering_scope o ORDER BY o.v', 'v\thit\n1\t0\n2\t0\n'),
    ('SELECT EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=1 GROUP BY i.missing)', (1054, '42S22')),
    ('SELECT EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=1 ORDER BY i.missing)', (1054, '42S22')),
    ('SELECT EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=1 ORDER BY missing)', (1054, '42S22')),
    ('SELECT EXISTS(SELECT 1 FROM ordering_scope i WHERE i.v=1 ORDER BY i.missing+1)', (1054, '42S22')),
]

cases.extend([
    ('CREATE TABLE count_outer(id INT PRIMARY KEY);INSERT INTO count_outer VALUES(0),(1),(2),(3)', ''),
    ('CREATE TABLE count_inner(id INT PRIMARY KEY,outer_id INT,KEY ix_outer_id(outer_id));INSERT INTO count_inner VALUES(1,1),(2,2),(3,2),(4,3),(5,3),(6,3)', ''),
])
for grouped in (False, True):
    grouping = ' GROUP BY i.outer_id' if grouped else ''
    for having, predicate in (
        ('COUNT(*)>1', lambda count: count > 1),
        ('2<=COUNT(*)', lambda count: 2 <= count),
        ('COUNT(*)=2', lambda count: count == 2),
        ('3<>COUNT(*)', lambda count: 3 != count),
        ('COUNT(*)<2', lambda count: count < 2),
        ('1>=COUNT(*)', lambda count: 1 >= count),
    ):
        sql = (
            'SELECT o.id,EXISTS(SELECT 1 FROM count_inner i WHERE i.outer_id=o.id'
            + grouping + ' HAVING ' + having + ') AS hit FROM count_outer o ORDER BY o.id'
        )
        values = [int((not grouped or count > 0) and predicate(count)) for count in range(4)]
        expected = 'id\thit\n' + ''.join(f'{row}\t{value}\n' for row, value in enumerate(values))
        cases.append((sql, expected))
    for having, predicate in (
        ('COUNT(*)>1 AND COUNT(*)<3', lambda count: 1 < count < 3),
        ('COUNT(*)=0 OR COUNT(*)=3', lambda count: count in (0, 3)),
        ('NOT (COUNT(*)<=1 OR COUNT(*)>=3)', lambda count: not (count <= 1 or count >= 3)),
        ('COUNT(*) BETWEEN 1 AND 2', lambda count: 1 <= count <= 2),
        ('COUNT(*) NOT BETWEEN 1 AND 2', lambda count: not (1 <= count <= 2)),
        ('COUNT(*) IN (0,2)', lambda count: count in (0, 2)),
        ('COUNT(*) NOT IN (0,2)', lambda count: count not in (0, 2)),
    ):
        sql = (
            'SELECT o.id,EXISTS(SELECT 1 FROM count_inner i WHERE i.outer_id=o.id'
            + grouping + ' HAVING ' + having + ') AS hit FROM count_outer o ORDER BY o.id'
        )
        values = [int((not grouped or count > 0) and predicate(count)) for count in range(4)]
        expected = 'id\thit\n' + ''.join(f'{row}\t{value}\n' for row, value in enumerate(values))
        cases.append((sql, expected))
for having, single_row_matches in (
    ('COUNT(*)>1', False),
    ('COUNT(*)=1', True),
    ('COUNT(*)<>1', False),
):
    sql = (
        'SELECT o.id,EXISTS(SELECT 1 FROM count_inner i WHERE i.outer_id=o.id'
        + ' GROUP BY i.id HAVING ' + having + ') AS hit FROM count_outer o ORDER BY o.id'
    )
    expected = 'id\thit\n' + ''.join(
        f'{row}\t{int(row > 0 and single_row_matches)}\n' for row in range(4)
    )
    cases.append((sql, expected))

prepared_cases = [
    ("PREPARE scoped_order FROM '" + sql.replace("'", "''") + "'", expected if isinstance(expected, tuple) else "")
    for sql, expected in cases
    if sql.startswith("SELECT ")
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases + prepared_cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
