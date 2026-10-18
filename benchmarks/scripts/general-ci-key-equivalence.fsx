open System
open System.Security.Cryptography
open System.Text
open Fsdb

let collation = Collation.tryFind "utf8mb4_general_ci" |> Option.get
let digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
let append (value: string) =
    let key = collation.KeyOf value
    digest.AppendData(BitConverter.GetBytes key.Length)
    digest.AppendData(Encoding.UTF8.GetBytes key)
for code in 0 .. 0xffff do append (string (char code))
let markers = [| '\uE0C0'; '\uE0D0'; '\uE0E0'; '\uE300'; '\uE0F0'; '\uE600'; '\uE0B1'; '\uE0B2'; '\uE0B3'; '\uE0B4' |]
let special = [| '①'; '⓵'; '〒'; '〶'; '〝'; '〞'; '〪'; '〫'; '㎞'; '⽇'; 'ｶ'; 'ﾊ'; '\uff9e'; '\u3099'; '\u034f'; '\u0301'; '\u3000' |]
for marker in markers do
    for c in Array.append markers special do
        append (String [| marker; c |])
        append (String [| c; marker |])
let random = Random 117
let pool = Array.append markers special
for _ in 1 .. 50000 do
    let length = random.Next(1, 11)
    append (String(Array.init length (fun _ ->
        if random.Next(4) = 0 then char (random.Next(0x10000))
        else pool.[random.Next(pool.Length)])))
printfn "%s" (Convert.ToHexString(digest.GetHashAndReset()))
