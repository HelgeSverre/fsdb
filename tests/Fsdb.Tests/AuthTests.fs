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

          testCase "account lookup selects the most specific matching host"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for host in [ "%"; "127.0.0.%"; "localhost"; "127.0.0.1" ] do
                  createUser store "hosted" host None |> Result.mapError snd |> Result.defaultWith failtest

              let selected host =
                  resolveAccount store "hosted" host
                  |> Option.map (fun (account, _, _) -> account.Host)

              Expect.equal (selected "127.0.0.1") (Some "127.0.0.1") "exact address wins"
              Expect.equal (selected "127.0.0.2") (Some "localhost") "loopback localhost wins over a wildcard"
              Expect.equal (selected "10.0.0.1") (Some "%") "percent remains the fallback"

          testCase "account lookup honours wildcard escaping and subnet specificity"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for host in [ "%"; "127.0.0.\\%"; "10.0.0.0/8"; "10.1.0.0/16" ] do
                  createUser store "patterned" host None |> Result.mapError snd |> Result.defaultWith failtest

              let selected host =
                  resolveAccount store "patterned" host
                  |> Option.map (fun (account, _, _) -> account.Host)

              Expect.equal (selected "127.0.0.%") (Some "127.0.0.\\%") "backslash escapes percent"
              Expect.equal (selected "10.1.2.3") (Some "10.1.0.0/16") "the narrower subnet wins"
              Expect.equal (selected "10.2.3.4") (Some "10.0.0.0/8") "the broader subnet still matches"

          testCase "host specificity chooses anonymous accounts before broader named accounts"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for name, host in [ "named", "%"; "", "localhost"; "", "127.0.0.0/8" ] do
                  createUser store name host None |> Result.mapError snd |> Result.defaultWith failtest

              let selected host =
                  resolveAccount store "named" host
                  |> Option.map (fun (account, _, _) -> account.Name, account.Host)

              Expect.equal (selected "::1") (Some("", "localhost")) "anonymous localhost beats named percent"
              Expect.equal (selected "127.0.0.1") (Some("", "127.0.0.0/8")) "CIDR beats localhost"

              createUser store "named" "127.0.0.1" None |> Result.mapError snd |> Result.defaultWith failtest
              Expect.equal (selected "127.0.0.1") (Some("named", "127.0.0.1")) "exact beats CIDR"

          testCase "account hosts compare case-insensitively and normalize IPv6 addresses"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              createUser store "renamed" "LOCALHOST" None |> Result.mapError snd |> Result.defaultWith failtest
              renameUser store "renamed" "localhost" "moved" "127.0.0.1" |> Result.mapError snd |> Result.defaultWith failtest
              Expect.isSome (tryUserRowForAccount store (account "moved" "127.0.0.1")) "case-insensitive host matches rename"

              createUser store "ipv6" "0:0:0:0:0:0:0:1" None |> Result.mapError snd |> Result.defaultWith failtest

              match resolveAccount store "ipv6" "0:0:0:0:0:0:0:1" with
              | Some(selected, _, _) -> Expect.equal selected.Host "::1" "canonical IPv6 account host"
              | None -> failtest "expected the IPv6 account"

          testCase "account DDL and grants keep same-name host accounts separate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let broad = account "separate" "%"
              let local = account "separate" "localhost"
              createUser store broad.Name broad.Host None |> Result.mapError snd |> Result.defaultWith failtest
              createUser store local.Name local.Host None |> Result.mapError snd |> Result.defaultWith failtest
              grant store [ "SELECT" ] (OnDb "shop") [ local.Name, local.Host ] false |> Result.mapError snd |> Result.defaultWith failtest

              Expect.isTrue (checkForAccount store local [ "SELECT", OnDb "shop" ] |> Result.isOk) "localhost grant applies locally"
              Expect.isTrue (checkForAccount store broad [ "SELECT", OnDb "shop" ] |> Result.isError) "percent account remains ungranted"

              dropUser store local.Name local.Host |> Result.mapError snd |> Result.defaultWith failtest
              Expect.isNone (tryUserRowForAccount store local) "drop removes only its exact account"
              Expect.isSome (tryUserRowForAccount store broad) "other host remains"

          testCase "anonymous accounts are only a fallback when no named account matches"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              createUser store "" "%" None |> Result.mapError snd |> Result.defaultWith failtest
              createUser store "named" "%" None |> Result.mapError snd |> Result.defaultWith failtest

              let selected user =
                  resolveAccount store user "192.0.2.5"
                  |> Option.map (fun (account, _, _) -> account.Name)

              Expect.equal (selected "named") (Some "named") "named account wins"
              Expect.equal (selected "missing") (Some "") "anonymous account is the fallback"

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
              Expect.equal (selectTablesOf "SELECT * FROM mine WHERE id = ANY (SELECT id FROM secret)") [ "mine"; "secret" ] "quantified subquery in WHERE"
              Expect.equal (selectTablesOf "SELECT * FROM mine WHERE EXISTS (SELECT 1 FROM secret)") [ "mine"; "secret" ] "EXISTS in WHERE"
              Expect.equal (selectTablesOf "SELECT * FROM mine JOIN (SELECT * FROM secret) d ON 1=1") [ "mine"; "secret" ] "joined derived table"
              Expect.equal (selectTablesOf "SELECT * FROM ((SELECT * FROM a) UNION (SELECT * FROM secret)) x") [ "a"; "secret" ] "union inside a derived table"
              Expect.equal
                  (selectTablesOf "WITH public AS (SELECT * FROM secret) SELECT * FROM public")
                  [ "secret" ]
                  "CTE bodies expose the physical read without treating the binding as a table"
              Expect.equal
                  (selectTablesOf "WITH first AS (SELECT * FROM later), later AS (SELECT 1 AS id) SELECT * FROM first")
                  [ "later" ]
                  "a forward CTE name still resolves as a physical table"
              Expect.equal
                  (selectTablesOf "WITH RECURSIVE seq (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 2) SELECT * FROM seq")
                  []
                  "a recursive self-reference is not a physical table"
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

              for sql, privilege in
                  [ "WITH chosen AS (SELECT id FROM secret) UPDATE mine SET x = 1 WHERE id IN (SELECT id FROM chosen)", "UPDATE"
                    "WITH chosen AS (SELECT id FROM secret) DELETE FROM mine WHERE id IN (SELECT id FROM chosen)", "DELETE" ] do
                  match Fsdb.Parser.parse sql with
                  | Ok statement ->
                      let privileges = requiredPrivileges "app" statement
                      Expect.contains privileges (privilege, OnTable("app", "mine")) "the mutation target is protected"
                      Expect.contains privileges ("SELECT", OnTable("app", "secret")) "the CTE source is protected"
                      Expect.isFalse
                          (privileges |> List.exists (function _, OnTable(_, "chosen") -> true | _ -> false))
                          "the CTE binding is not a physical privilege target"
                  | Error error -> failtestf "parse CTE mutation: %s" error

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
