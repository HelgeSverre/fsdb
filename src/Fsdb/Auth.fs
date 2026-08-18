/// Account lookup and mysql_native_password verification against the
/// `mysql.user` system table (see `Storage`'s bootstrap). The rule the
/// handshake enforces: an account must exist, and is verified only when it
/// has a non-empty stored hash — an empty `authentication_string` accepts
/// any offered credential. ponytail: that last part deliberately diverges
/// from real MySQL (which would reject a wrong password even for an
/// empty-password account) so every existing passwordless client and the
/// torture harness keep connecting unchanged; tighten if fsdb ever fronts
/// anything but loopback.
module Fsdb.Auth

open System
open System.Security.Cryptography
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage

let private sha1 (bytes: byte[]) : byte[] = SHA1.HashData bytes

/// The stored mysql_native_password hash for a plaintext password:
/// `'*' + uppercase hex SHA1(SHA1(password))` — what `IDENTIFIED BY` writes
/// into `mysql.user.authentication_string`.
let nativePasswordHash (password: string) : string =
    "*" + Convert.ToHexString(sha1 (sha1 (Text.Encoding.UTF8.GetBytes password)))

/// Verifies a client's mysql_native_password challenge answer.
/// The client sends `SHA1(pw) XOR SHA1(scramble + SHA1(SHA1(pw)))` (20
/// bytes); XORing with `SHA1(scramble + stage2)` recovers `SHA1(pw)`, whose
/// SHA1 must equal the stored `stage2 = SHA1(SHA1(pw))`.
let verifyNative (storedHash: string) (scramble: byte[]) (response: byte[]) : bool =
    if response.Length <> 20 then
        false
    else
        try
            let stage2 = Convert.FromHexString(storedHash.TrimStart '*')
            let mask = sha1 (Array.append scramble stage2)
            let stage1 = Array.map2 (^^^) response mask
            sha1 stage1 = stage2
        with _ ->
            false

/// The `mysql.user` row for `username` (host ignored — every account is
/// `'%'`, see `Session.User`'s doc), as `(columns, row)`. Reads the live
/// catalog every time: the tables are tiny and rows are the single source
/// of truth, no cache to invalidate.
let tryUserRow (store: Store) (username: string) : (ColumnDef list * Value[]) option =
    match scanList store "mysql" "user" with
    | Error _ -> None
    | Ok(cols, rows) ->
        match resolveColumn cols "User" with
        | Error _ -> None
        | Ok userIdx -> rows |> List.tryFind (fun r -> r.[userIdx] = VString username) |> Option.map (fun r -> cols, r)

/// A user row's column as text, `""` for NULL/absent.
let userColumnText (cols: ColumnDef list) (row: Value[]) (name: string) : string =
    match resolveColumn cols name with
    | Ok i ->
        match row.[i] with
        | VString s -> s
        | v -> Value.toText v |> Option.defaultValue ""
    | Error _ -> ""

/// The stored password hash for a user row — `""` means "no password set,
/// accept anything".
let storedPasswordHash (cols: ColumnDef list) (row: Value[]) : string = userColumnText cols row "authentication_string"
