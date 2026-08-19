module Fsdb.Tests.VectorTests

open System
open System.IO
open Expecto
open Fsdb.Executor
open Fsdb.QueryHandler

// The MySQL 9 VECTOR surface, pinned by tests written from the MySQL 9 docs
// — the local torture oracle is 8.4 (no VECTOR), so the exact formats here
// (scientific notation, error codes, `vector(N)` type text) are the contract.

let private fresh () = Fsdb.Session.create 1 (Fsdb.Storage.create ())

/// Runs `queries` in order on one session and returns the last result.
let private run (queries: string list) : QueryResult =
    let session = fresh ()
    queries |> List.fold (fun (s, _) q -> handle s q) (session, Affected 0UL) |> snd

let private expectScalar (expected: string) (queries: string list) (label: string) =
    match run queries with
    | ResultSet(_, [ [ Some actual ] ]) -> Expect.equal actual expected label
    | other -> failtestf "%s: expected one scalar, got %A" label other

let private expectErr (code: int) (queries: string list) (label: string) =
    match run queries with
    | Err(actual, _) -> Expect.equal actual code label
    | other -> failtestf "%s: expected error %d, got %A" label code other

let tests =
    testList
        "Vector"
        [ testCase "STRING_TO_VECTOR round-trips through VECTOR_TO_STRING in MySQL 9's exact scientific format"
          <| fun _ ->
              // Pinned verbatim from the MySQL 9 docs example: lowercase e,
              // 5 fractional digits, signed two-digit exponent, no spaces.
              expectScalar
                  "[1.05000e+00,-1.78000e+01]"
                  [ "SELECT VECTOR_TO_STRING(STRING_TO_VECTOR('[1.05, -17.8]'))" ]
                  "scientific round-trip"

          testCase "TO_VECTOR and FROM_VECTOR are aliases of the STRING_TO_VECTOR/VECTOR_TO_STRING pair"
          <| fun _ -> expectScalar "[0.00000e+00,2.50000e-01]" [ "SELECT FROM_VECTOR(TO_VECTOR('[0, 0.25]'))" ] "alias round-trip"

          testCase "VECTOR_DIM reports the dimension, and NULL propagates through all vector functions"
          <| fun _ ->
              expectScalar "3" [ "SELECT VECTOR_DIM(STRING_TO_VECTOR('[1,2,3]'))" ] "dim 3"

              match run [ "SELECT VECTOR_DIM(NULL), STRING_TO_VECTOR(NULL), VECTOR_TO_STRING(NULL)" ] with
              | ResultSet(_, [ [ None; None; None ] ]) -> ()
              | other -> failtestf "expected all-NULL row, got %A" other

          testCase "DISTANCE computes all three metrics against hand-computed values"
          <| fun _ ->
              // EUCLIDEAN([1,0],[0,1]) = sqrt(2)
              expectScalar
                  "1.4142135623730951"
                  [ "SELECT DISTANCE(STRING_TO_VECTOR('[1,0]'), STRING_TO_VECTOR('[0,1]'), 'EUCLIDEAN')" ]
                  "euclidean"

              // COSINE(orthogonal) = 1 - 0 = 1
              expectScalar "1" [ "SELECT DISTANCE(STRING_TO_VECTOR('[1,0]'), STRING_TO_VECTOR('[0,1]'), 'COSINE')" ] "cosine orthogonal"

              // COSINE([1,0],[2,0]) = 0 (parallel; exactly representable so
              // no epsilon comparison is needed)
              expectScalar "0" [ "SELECT DISTANCE(STRING_TO_VECTOR('[1,0]'), STRING_TO_VECTOR('[2,0]'), 'COSINE')" ] "cosine parallel"

              // DOT([1,2],[3,4]) = 3 + 8 = 11
              expectScalar "11" [ "SELECT DISTANCE(STRING_TO_VECTOR('[1,2]'), STRING_TO_VECTOR('[3,4]'), 'DOT')" ] "dot"

              // VECTOR_DISTANCE is the same function under its other name.
              expectScalar "11" [ "SELECT VECTOR_DISTANCE(STRING_TO_VECTOR('[1,2]'), STRING_TO_VECTOR('[3,4]'), 'DOT')" ] "vector_distance alias"

          testCase "DISTANCE rejects a dimension mismatch and an unknown metric with 1210"
          <| fun _ ->
              expectErr 1210 [ "SELECT DISTANCE(STRING_TO_VECTOR('[1,0]'), STRING_TO_VECTOR('[1,2,3]'), 'COSINE')" ] "dim mismatch"
              expectErr 1210 [ "SELECT DISTANCE(STRING_TO_VECTOR('[1]'), STRING_TO_VECTOR('[2]'), 'MANHATTAN')" ] "unknown metric"

          testCase "malformed vector strings are refused, not silently zeroed"
          <| fun _ ->
              expectErr 6138 [ "SELECT STRING_TO_VECTOR('not a vector')" ] "unbracketed"
              expectErr 6138 [ "SELECT STRING_TO_VECTOR('[]')" ] "empty vector"
              expectErr 6138 [ "SELECT STRING_TO_VECTOR('[1,x]')" ] "non-numeric element"

          testCase "a VECTOR column stores exactly its dimension and reads back intact"
          <| fun _ ->
              expectScalar
                  "[1.00000e+00,2.00000e+00,3.00000e+00]"
                  [ "CREATE TABLE docs (id INT, embedding VECTOR(3))"
                    "INSERT INTO docs VALUES (1, STRING_TO_VECTOR('[1,2,3]'))"
                    "SELECT VECTOR_TO_STRING(embedding) FROM docs" ]
                  "stored vector round-trip"

          testCase "inserting a wrong-dimension vector fails in the 1366 incorrect-value shape"
          <| fun _ ->
              expectErr
                  1366
                  [ "CREATE TABLE docs (id INT, embedding VECTOR(3))"
                    "INSERT INTO docs VALUES (1, STRING_TO_VECTOR('[1,2]'))" ]
                  "dim 2 into VECTOR(3)"

              expectErr
                  1366
                  [ "CREATE TABLE docs (id INT, embedding VECTOR(3))"
                    "INSERT INTO docs VALUES (1, '[1,2,3]')" ]
                  "plain string without STRING_TO_VECTOR"

          testCase "bare VECTOR means dimension 2048 and VECTOR() is a syntax error"
          <| fun _ ->
              expectScalar
                  "vector(2048)"
                  [ "CREATE TABLE v (e VECTOR)"
                    "SELECT column_type FROM information_schema.columns WHERE table_name = 'v' AND column_name = 'e'" ]
                  "bare VECTOR = vector(2048)"

              expectScalar
                  "vector"
                  [ "CREATE TABLE v (e VECTOR(4))"
                    "SELECT data_type FROM information_schema.columns WHERE table_name = 'v' AND column_name = 'e'" ]
                  "DATA_TYPE is the bare name"

              expectErr 1064 [ "CREATE TABLE v (e VECTOR())" ] "VECTOR() is a syntax error"

          testCase "the 16383 dimension ceiling is enforced at DDL time"
          <| fun _ ->
              expectErr 1074 [ "CREATE TABLE v (e VECTOR(16384))" ] "over the ceiling"
              expectErr 1074 [ "CREATE TABLE v (e VECTOR(0))" ] "zero dimensions"

              match run [ "CREATE TABLE v (e VECTOR(16383))" ] with
              | Affected 0UL -> ()
              | other -> failtestf "VECTOR(16383) should be accepted, got %A" other

          testCase "a VECTOR column is rejected as any kind of key, at CREATE and via ALTER"
          <| fun _ ->
              expectErr 3152 [ "CREATE TABLE v (e VECTOR(3) PRIMARY KEY)" ] "primary key"
              expectErr 3152 [ "CREATE TABLE v (e VECTOR(3) UNIQUE)" ] "unique"
              expectErr 3152 [ "CREATE TABLE v (e VECTOR(3), KEY (e))" ] "plain index"
              expectErr 3152 [ "CREATE TABLE v (e VECTOR(3))"; "CREATE INDEX ix ON v (e)" ] "CREATE INDEX"

              expectErr
                  3152
                  [ "CREATE TABLE v (id INT, e VECTOR(3))"; "ALTER TABLE v ADD INDEX ix (e)" ]
                  "ALTER ADD INDEX"

              expectErr
                  3152
                  [ "CREATE TABLE v (id INT, e VECTOR(3))"; "ALTER TABLE v ADD PRIMARY KEY (e)" ]
                  "ALTER ADD PRIMARY KEY"

          testCase "CAST(... AS VECTOR) is an error — STRING_TO_VECTOR is the sanctioned conversion"
          <| fun _ -> expectErr 1064 [ "SELECT CAST('[1,2]' AS VECTOR(2))" ] "CAST to VECTOR"

          testCase "a VECTOR column and its rows survive a WAL/snapshot round-trip"
          <| fun _ ->
              let dir = Path.Combine(Path.GetTempPath(), "fsdb-vector-tests", Guid.NewGuid().ToString "N")
              Directory.CreateDirectory dir |> ignore

              let store = Fsdb.Storage.create ()
              Fsdb.Persistence.attach dir store
              let session = Fsdb.Session.create 1 store

              let session, _ = handle session "CREATE TABLE docs (id INT, embedding VECTOR(2))"
              let session, r = handle session "INSERT INTO docs VALUES (1, STRING_TO_VECTOR('[1.5, -2.5]'))"

              match r with
              | Affected 1UL -> ()
              | other -> failtestf "insert should succeed pre-reload, got %A" other

              ignore session
              let reloaded = Fsdb.Persistence.load dir
              let session2 = Fsdb.Session.create 2 reloaded

              match handle session2 "SELECT VECTOR_TO_STRING(embedding), VECTOR_DIM(embedding) FROM docs" |> snd with
              | ResultSet(_, [ [ Some text; Some dim ] ]) ->
                  Expect.equal text "[1.50000e+00,-2.50000e+00]" "reloaded vector value"
                  Expect.equal dim "2" "reloaded vector dimension"
              | other -> failtestf "expected the reloaded vector row, got %A" other

          testCase "MySqlConnector reads a VECTOR column as byte[] (blob + binary charset on the wire)"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening System.Net.IPAddress.Loopback 0
                  let port = Fsdb.Server.port listener
                  Fsdb.Server.serve listener (Fsdb.Storage.create ()) Fsdb.Functions.builtins |> Async.StartAsTask |> ignore

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use create = conn.CreateCommand()
                      create.CommandText <- "CREATE TABLE docs (id INT, embedding VECTOR(2))"
                      do! create.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                      use insert = conn.CreateCommand()
                      insert.CommandText <- "INSERT INTO docs VALUES (1, STRING_TO_VECTOR('[1.5, -2.5]'))"
                      do! insert.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore

                      use select = conn.CreateCommand()
                      select.CommandText <- "SELECT embedding FROM docs"
                      use! reader = select.ExecuteReaderAsync() |> Async.AwaitTask
                      let! hasRow = reader.ReadAsync() |> Async.AwaitTask
                      Expect.isTrue hasRow "vector row present"

                      let expected = Array.append (BitConverter.GetBytes 1.5f) (BitConverter.GetBytes -2.5f)
                      Expect.equal (reader.GetFieldValue<byte[]> 0) expected "little-endian float32 bytes"
                      do! reader.CloseAsync() |> Async.AwaitTask
                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously ]
