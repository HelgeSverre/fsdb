module Fsdb.Xa

open System
open FParsec

[<StructuralEquality; StructuralComparison>]
type Xid =
    { GlobalId: byte list
      BranchQualifier: byte list
      FormatId: uint32 }

type Command =
    | Start of Xid * joinOrResume: bool
    | End of Xid * suspend: bool
    | Prepare of Xid
    | Commit of Xid * onePhase: bool
    | Rollback of Xid
    | Recover of convertXid: bool

let private spaces1: Parser<unit, unit> = skipMany1Satisfy Char.IsWhiteSpace
let private token value = pstringCI value .>> spaces

let private hexBytes (allowOddLength: bool) (digits: string) =
    let digits =
        if allowOddLength && digits.Length % 2 <> 0 then
            "0" + digits
        else
            digits

    if digits.Length % 2 <> 0 then
        invalidArg (nameof digits) "hexadecimal XA identifiers require complete bytes"
    else
        [ for index in 0..2 .. digits.Length - 2 do
              yield Convert.ToByte(digits.Substring(index, 2), 16) ]

let private bitBytes (bits: string) =
    let padded = bits.PadLeft((bits.Length + 7) / 8 * 8, '0')

    [ for index in 0..8 .. padded.Length - 8 do
          yield Convert.ToByte(padded.Substring(index, 8), 2) ]

let private escapedCharacter noBackslashEscapes =
    let doubledQuote = attempt (pstring "''") >>% '\''

    let backslashEscape =
        pchar '\\'
        >>. anyChar
        |>> function
            | '0' -> '\000'
            | 'b' -> '\b'
            | 'n' -> '\n'
            | 'r' -> '\r'
            | 't' -> '\t'
            | 'Z' -> char 26
            | value -> value

    if noBackslashEscapes then
        doubledQuote <|> noneOf "'"
    else
        doubledQuote <|> backslashEscape <|> noneOf "'"

let private xidPart noBackslashEscapes charset =
    let quoted =
        between (pchar '\'') (pchar '\'') (manyChars (escapedCharacter noBackslashEscapes))
        |>> (Collation.Charset.encode charset >> List.ofArray)

    let quotedHex =
        attempt (pstringCI "X" >>. between (pchar '\'') (pchar '\'') (manyChars hex))
        >>= fun digits ->
            if digits.Length % 2 = 0 then
                preturn (hexBytes false digits)
            else
                fail "hexadecimal XA identifiers require complete bytes"

    let prefixedHex =
        attempt (pstringCI "0x" >>. many1Chars hex)
        |>> hexBytes true

    let bits =
        attempt (pstringCI "b" >>. between (pchar '\'') (pchar '\'') (manyChars (anyOf "01")))
        |>> bitBytes

    choice [ quotedHex; prefixedHex; bits; quoted ] .>> spaces

let private xid noBackslashEscapes charset =
    pipe3
        (xidPart noBackslashEscapes charset)
        (opt (pchar ',' >>. spaces >>. xidPart noBackslashEscapes charset))
        (opt (pchar ',' >>. spaces >>. puint32 .>> spaces))
        (fun globalId branchQualifier formatId ->
            { GlobalId = globalId
              BranchQualifier = branchQualifier |> Option.defaultValue []
              FormatId = formatId |> Option.defaultValue 1u })
    >>= fun value ->
        if value.GlobalId.Length <= 64 && value.BranchQualifier.Length <= 64 then
            preturn value
        else
            fail "XA transaction identifiers are limited to 64 bytes per part"

let private command noBackslashEscapes charset =
    let xid = xid noBackslashEscapes charset

    token "XA"
    >>. choice
            [ (token "START" <|> token "BEGIN") >>. xid .>>. opt (token "JOIN" <|> token "RESUME")
              |>> fun (value, option) -> Start(value, option.IsSome)
              token "END" >>. xid .>>. opt (token "SUSPEND" >>. opt (token "FOR" >>. token "MIGRATE"))
              |>> fun (value, option) -> End(value, option.IsSome)
              token "PREPARE" >>. xid |>> Prepare
              token "COMMIT" >>. xid .>>. opt (token "ONE" >>. token "PHASE") |>> fun (value, onePhase) -> Commit(value, onePhase.IsSome)
              token "ROLLBACK" >>. xid |>> Rollback
              token "RECOVER" >>. opt (token "CONVERT" >>. token "XID") |>> (Option.isSome >> Recover) ]
    .>> eof

let parse noBackslashEscapes charset sql =
    match run (spaces >>. command noBackslashEscapes charset) sql with
    | Success(value, _, _) -> Result.Ok value
    | Failure(message, _, _) -> Result.Error message

let data (xid: Xid) = Array.ofList (xid.GlobalId @ xid.BranchQualifier)

let sameBranch left right =
    left.GlobalId = right.GlobalId && left.BranchQualifier = right.BranchQualifier
