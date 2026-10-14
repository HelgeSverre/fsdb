# LOAD DATA does not write through a joined view

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` for a simple inner-join view and
allows explicit-column INSERT, but rejects `LOAD DATA LOCAL INFILE` into that
view with error 1288, `The target table v of the LOAD is not updatable`.
The result is the same with or without a field list and with `REPLACE`.
MySQL returns the error before asking the client for file bytes; the base
table remains unchanged. A single-table updatable view still accepts LOAD.

FSDB previously sent the local file request, then rewrote the LOAD into the
joined view's base table. It now rejects joined-view LOAD during wire-level
preparation, before requesting or reading bytes, and the executor returns the
same error if a parsed LOAD reaches it directly. The focused wire regression
checks all three spellings and preserves the existing single-table view LOAD
regression.
