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

              Expect.isNone (tryUserRow store "nobody") "unknown user is None" ]
