/// Authentication plugins and their password transforms.
module Fsdb.Authentication

open System
open System.Security.Cryptography
open System.Text

type Plugin =
    | MysqlNativePassword
    | CachingSha2Password

let name = function
    | MysqlNativePassword -> "mysql_native_password"
    | CachingSha2Password -> "caching_sha2_password"

let tryParse = function
    | value when String.Equals(value, name MysqlNativePassword, StringComparison.OrdinalIgnoreCase) ->
        Some MysqlNativePassword
    | value when String.Equals(value, name CachingSha2Password, StringComparison.OrdinalIgnoreCase) ->
        Some CachingSha2Password
    | _ -> None

let defaultPlugin = CachingSha2Password

let private utf8 = UTF8Encoding(false, true)
let private sha1 (bytes: byte[]) = SHA1.HashData bytes
let private sha256 (bytes: byte[]) = SHA256.HashData bytes

let nativePasswordHash (password: string) =
    "*" + Convert.ToHexString(sha1 (sha1 (utf8.GetBytes password)))

let private cryptAlphabet =
    "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

let private repeatToLength length (bytes: byte[]) =
    Array.init length (fun index -> bytes.[index % bytes.Length])

let private sha256Parts (parts: byte[] list) =
    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
    parts |> List.iter hash.AppendData
    hash.GetHashAndReset()

let private encodeCryptBlock (digest: byte[]) (b2, b1, b0, count) =
    let mutable word = (uint32 digest.[b2] <<< 16) ||| (uint32 digest.[b1] <<< 8) ||| uint32 digest.[b0]

    String.init count (fun _ ->
        let encoded = cryptAlphabet.[int (word &&& 0x3fu)]
        word <- word >>> 6
        string encoded)

let private sha256Crypt (password: byte[]) (salt: byte[]) rounds =
    let alternate = sha256Parts [ password; salt; password ]
    let alternatePrefix = repeatToLength password.Length alternate

    let rec mixLength bits parts =
        if bits = 0 then
            parts
        else
            mixLength (bits >>> 1) ((if bits &&& 1 = 1 then alternate else password) :: parts)

    let first = sha256Parts ([ password; salt; alternatePrefix ] @ (mixLength password.Length [] |> List.rev))
    let passwordDigest = sha256 (Array.concat (List.replicate password.Length password))
    let repeatedPassword = repeatToLength password.Length passwordDigest
    let saltDigest = sha256 (Array.concat (List.replicate (16 + int first.[0]) salt))
    let repeatedSalt = repeatToLength salt.Length saltDigest

    let final =
        [ 0 .. rounds - 1 ]
        |> List.fold
            (fun previous round ->
                sha256Parts
                    [ if round &&& 1 = 1 then repeatedPassword else previous
                      if round % 3 <> 0 then repeatedSalt
                      if round % 7 <> 0 then repeatedPassword
                      if round &&& 1 = 1 then previous else repeatedPassword ])
            first

    [ 0, 10, 20, 4
      21, 1, 11, 4
      12, 22, 2, 4
      3, 13, 23, 4
      24, 4, 14, 4
      15, 25, 5, 4
      6, 16, 26, 4
      27, 7, 17, 4
      18, 28, 8, 4
      9, 19, 29, 4 ]
    |> List.map (encodeCryptBlock final)
    |> fun blocks ->
        let mutable word = (uint32 final.[31] <<< 8) ||| uint32 final.[30]
        let tail =
            String.init 3 (fun _ ->
                let encoded = cryptAlphabet.[int (word &&& 0x3fu)]
                word <- word >>> 6
                string encoded)

        String.concat "" blocks + tail

let private cachingPrefix = "$A$"
let private cachingSaltLength = 20
let private cachingDigestLength = 43
let private cachingDefaultRounds = 5000
let private cachingMaximumPasswordBytes = 256

let acceptsPassword plugin (password: string) =
    match plugin with
    | MysqlNativePassword -> true
    | CachingSha2Password -> utf8.GetByteCount password <= cachingMaximumPasswordBytes

let cachingSha2PasswordHashWithSalt (salt: byte[]) (password: string) =
    if salt.Length <> cachingSaltLength then
        invalidArg (nameof salt) "caching_sha2_password requires a 20-byte salt"

    if not (acceptsPassword CachingSha2Password password) then
        invalidArg (nameof password) "caching_sha2_password accepts at most 256 password bytes"

    let saltText = Encoding.ASCII.GetString salt
    cachingPrefix + "005$" + saltText + sha256Crypt (utf8.GetBytes password) salt cachingDefaultRounds

let private randomCachingSalt () =
    let salt = Array.zeroCreate<byte> cachingSaltLength
    RandomNumberGenerator.Fill salt

    salt
    |> Array.map (fun value -> byte cryptAlphabet.[int value &&& 0x3f])

let passwordHash plugin (password: string) =
    if not (acceptsPassword plugin password) then
        invalidArg (nameof password) "the password is too long for the authentication plugin"
    elif password = "" then
        ""
    else
        match plugin with
        | MysqlNativePassword -> nativePasswordHash password
        | CachingSha2Password -> cachingSha2PasswordHashWithSalt (randomCachingSalt ()) password

let internal passwordHashesEqual (left: string) (right: string) =
    let left = Encoding.ASCII.GetBytes left
    let right = Encoding.ASCII.GetBytes right
    left.Length = right.Length && CryptographicOperations.FixedTimeEquals(left, right)

let private tryCachingParts (storedHash: string) =
    if
        storedHash.Length <> 7 + cachingSaltLength + cachingDigestLength
        || not (storedHash.StartsWith cachingPrefix)
        || storedHash.[6] <> '$'
    then
        None
    else
        match Int32.TryParse(storedHash.Substring(3, 3), Globalization.NumberStyles.HexNumber, null) with
        | true, iterationBlocks when iterationBlocks > 0 ->
            let salt = Encoding.ASCII.GetBytes(storedHash.Substring(7, cachingSaltLength))
            let digest = storedHash.Substring(7 + cachingSaltLength)
            Some(salt, min (iterationBlocks * 1000) 4095000, digest)
        | _ -> None

let isValidHash plugin storedHash =
    storedHash = ""
    || match plugin with
       | MysqlNativePassword ->
           storedHash.Length = 41
           && storedHash.[0] = '*'
           && (try
                   Convert.FromHexString(storedHash[1..]).Length = SHA1.HashSizeInBytes
               with _ ->
                   false)
       | CachingSha2Password -> tryCachingParts storedHash |> Option.isSome

let verifyPassword plugin storedHash password =
    if not (acceptsPassword plugin password) then
        false
    elif storedHash = "" then
        password = ""
    else
        match plugin with
        | MysqlNativePassword -> passwordHashesEqual storedHash (nativePasswordHash password)
        | CachingSha2Password ->
            match tryCachingParts storedHash with
            | Some(salt, rounds, digest) ->
                passwordHashesEqual digest (sha256Crypt (utf8.GetBytes password) salt rounds)
            | None -> false

let verifyNative (storedHash: string) (scramble: byte[]) (response: byte[]) =
    if response.Length <> SHA1.HashSizeInBytes then
        false
    else
        try
            let stage2 = Convert.FromHexString(storedHash.TrimStart '*')
            let mask = sha1 (Array.append scramble stage2)
            let stage1 = Array.map2 (^^^) response mask
            CryptographicOperations.FixedTimeEquals(sha1 stage1, stage2)
        with _ ->
            false

let cachingFastDigest (password: string) =
    password |> utf8.GetBytes |> sha256 |> sha256

let verifyCachingResponse (knownDigest: byte[]) (scramble: byte[]) (response: byte[]) =
    if knownDigest.Length <> SHA256.HashSizeInBytes || response.Length <> SHA256.HashSizeInBytes then
        false
    else
        let mask = sha256Parts [ knownDigest; scramble ]
        let stage1 = Array.map2 (^^^) response mask
        CryptographicOperations.FixedTimeEquals(sha256 stage1, knownDigest)
