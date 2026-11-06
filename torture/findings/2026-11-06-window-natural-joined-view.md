# Windowed read source in a writable NATURAL JOIN view

The pinned MySQL 8.4.11 view oracle now covers three ways to join a windowed
read source to a physical write target: source first under `NATURAL JOIN`,
target first under `NATURAL JOIN`, and target first with the window result in
a CTE. Each has two window rows for target `id=1`.

MySQL reports `IS_UPDATABLE=YES`, returns both joined rows, and updates the
physical target once. A `WITH CASCADED CHECK OPTION` view rejects an update
that would make `a.n` negative with error 1369 without changing the target.
INSERT and DELETE through the joined view fail with errors 1471 and 1395.
The three shapes pass the maintained native oracle and focused fsdb tests.

The corresponding LEFT and RIGHT outer-join views report
`IS_UPDATABLE=NO` and reject UPDATE with error 1288 in MySQL. That
non-updatable boundary was already covered by fsdb's outer-join view tests.
Other layered source and security combinations remain open.

Reproduce the native contract with:

```sh
uv run --with pymysql torture/scripts/composed-join-view-oracle.py
```
