module Fsdb.Tests.AuthTests

open System
open System.Security.Cryptography
open Expecto
open Fsdb.Auth

/// A client's mysql_native_password answer, computed the way a real client
/// does: SHA1(pw) XOR SHA1(scramble + SHA1(SHA1(pw))).
let private clientResponse (password: string) (scramble: byte[]) : byte[] =
    let sha1 (b: byte[]) = SHA1.HashData b
    let stage1 = sha1 (Text.Encoding.UTF8.GetBytes password)
    let mask = sha1 (Array.append scramble (sha1 stage1))
    Array.map2 (^^^) stage1 mask

let tests =
    testList
        "Auth"
        [ testCase "nativePasswordHash matches MySQL's documented PASSWORD('password') value"
          <| fun _ ->
              // Oracle-verifiable: SELECT PASSWORD('password') on pre-8
              // MySQL / the mysql_native_password authentication_string.
              Expect.equal
                  (nativePasswordHash "password")
                  "*2470C0C06DEE42FD1618BB99005ADCA2EC9D1E19"
                  "known hash vector"

          testCase "verifyNative accepts a correctly computed client response and rejects everything else"
          <| fun _ ->
              let scramble = Array.init 20 byte
              let stored = nativePasswordHash "s3cret"

              Expect.isTrue (verifyNative stored scramble (clientResponse "s3cret" scramble)) "right password verifies"
              Expect.isFalse (verifyNative stored scramble (clientResponse "wrong" scramble)) "wrong password fails"
              Expect.isFalse (verifyNative stored scramble [||]) "empty response fails"
              Expect.isFalse (verifyNative stored scramble (Array.zeroCreate 20)) "garbage fails"
              Expect.isFalse (verifyNative "*NOTHEX" scramble (clientResponse "s3cret" scramble)) "junk stored hash fails closed"

          testCase "tryUserRow finds the bootstrap root and reports its empty stored hash"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              match tryUserRow store "root" with
              | Some(cols, row) -> Expect.equal (storedPasswordHash cols row) "" "no password out of the box"
              | None -> failtest "expected the bootstrap root row"

              Expect.isNone (tryUserRow store "nobody") "unknown user is None"

          testCase "requiredPrivileges reaches SELECT tables hidden in subqueries and derived tables"
          <| fun _ ->
              let selectTablesOf sql =
                  match Fsdb.Parser.parse sql with
                  | Ok stmt ->
                      requiredPrivileges "app" stmt
                      |> List.choose (function
                          | "SELECT", OnTable(_, t) -> Some t
                          | _ -> None)
                      |> List.sort
                  | Error e -> failtestf "parse %s: %s" sql e

              // Each of these reads `secret` and must require SELECT on it.
              Expect.equal (selectTablesOf "SELECT (SELECT s FROM secret)") [ "secret" ] "scalar subquery in projection"
              Expect.equal (selectTablesOf "SELECT * FROM (SELECT * FROM secret) x") [ "secret" ] "derived table"
              Expect.equal (selectTablesOf "SELECT * FROM mine WHERE id IN (SELECT id FROM secret)") [ "mine"; "secret" ] "IN subquery in WHERE"
              Expect.equal (selectTablesOf "SELECT * FROM mine WHERE EXISTS (SELECT 1 FROM secret)") [ "mine"; "secret" ] "EXISTS in WHERE"
              Expect.equal (selectTablesOf "SELECT * FROM mine JOIN (SELECT * FROM secret) d ON 1=1") [ "mine"; "secret" ] "joined derived table"
              Expect.equal (selectTablesOf "SELECT * FROM ((SELECT * FROM a) UNION (SELECT * FROM secret)) x") [ "a"; "secret" ] "union inside a derived table"
              Expect.equal
                  (selectTablesOf "WITH public AS (SELECT * FROM secret) SELECT * FROM public")
                  [ "public"; "secret" ]
                  "CTE bodies cannot hide a table read"
              Expect.equal
                  (selectTablesOf "SELECT FIRST_VALUE((SELECT s FROM secret)) OVER ()")
                  [ "secret" ]
                  "window arguments cannot hide a table read"

              match Fsdb.Parser.parse "UPDATE mine SET x = (SELECT s FROM secret)" with
              | Ok stmt ->
                  Expect.isTrue
                      (requiredPrivileges "app" stmt |> List.contains ("SELECT", OnTable("app", "secret")))
                      "subquery in an UPDATE SET clause needs SELECT on secret"
              | Error e -> failtestf "parse update: %s" e

              match Fsdb.Parser.parse "DELETE FROM mine WHERE EXISTS (SELECT 1 FROM secret)" with
              | Ok stmt ->
                  Expect.isTrue
                      (requiredPrivileges "app" stmt |> List.contains ("SELECT", OnTable("app", "secret")))
                      "EXISTS in a DELETE WHERE needs SELECT on secret"
              | Error e -> failtestf "parse delete: %s" e

              match Fsdb.Parser.parse "INSERT INTO mine VALUES ((SELECT s FROM secret))" with
              | Ok stmt ->
                  Expect.isTrue
                      (requiredPrivileges "app" stmt |> List.contains ("SELECT", OnTable("app", "secret")))
                      "subquery in an INSERT VALUES needs SELECT on secret"
              | Error e -> failtestf "parse insert: %s" e

          testCase "ON DUPLICATE KEY UPDATE requires UPDATE on the target table"
          <| fun _ ->
              for sql in
                  [ "INSERT INTO dst VALUES (1) ON DUPLICATE KEY UPDATE value = VALUES(value)"
                    "INSERT INTO dst SELECT * FROM src ON DUPLICATE KEY UPDATE value = VALUES(value)" ] do
                  match Fsdb.Parser.parse sql with
                  | Ok statement ->
                      Expect.contains
                          (requiredPrivileges "app" statement)
                          ("UPDATE", OnTable("app", "dst"))
                          "the duplicate branch updates an existing target row"
                  | Error error -> failtestf "parse %s: %s" sql error

          testCase "REPLACE requires INSERT and DELETE plus SELECT for its source"
          <| fun _ ->
              for sql, source in
                  [ "REPLACE INTO dst VALUES (1)", None
                    "REPLACE INTO dst SELECT id FROM src", Some(OnTable("app", "src")) ] do
                  match Fsdb.Parser.parse sql with
                  | Ok statement ->
                      let privileges = requiredPrivileges "app" statement
                      Expect.contains privileges ("INSERT", OnTable("app", "dst")) "insert privilege"
                      Expect.contains privileges ("DELETE", OnTable("app", "dst")) "delete privilege"
                      source |> Option.iter (fun target -> Expect.contains privileges ("SELECT", target) "source privilege")
                  | Error error -> failtestf "parse %s: %s" sql error

          testCase "REVOKE GRANT OPTION, SELECT still requires SELECT on the target"
          <| fun _ ->
              // The GRANT OPTION token must not make expandPrivs error and
              // drop the SELECT requirement — otherwise a scoped grant-option
              // holder could revoke a privilege it doesn't hold.
              match Fsdb.Parser.parse "REVOKE GRANT OPTION, SELECT ON shop.* FROM victim" with
              | Ok stmt ->
                  let reqs = requiredPrivileges "app" stmt
                  Expect.isTrue (reqs |> List.contains ("SELECT", OnDb "shop")) "SELECT on shop is still required"
                  Expect.isTrue (reqs |> List.contains ("GRANT OPTION", OnDb "shop")) "GRANT OPTION is also required"
              | Error e -> failtestf "parse: %s" e ]
