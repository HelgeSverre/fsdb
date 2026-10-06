module Fsdb.PreparedVariables

open System
open System.Globalization
open System.Numerics
open System.Text
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value

let private typeOf = function
    | VInt _ -> UserVariableType.SignedInteger
    | VUInt _ -> UserVariableType.UnsignedInteger
    | VDecimal _ -> UserVariableType.Decimal
    | VDouble _ -> UserVariableType.Double
    | VNull | VBinaryLiteral _ | VBytes _ | VBit _ | VGeometry _ -> UserVariableType.Binary
    | _ -> UserVariableType.Text

let metadata = function
    | UserVariableType.SignedInteger -> ColumnWire.metadataOfType(TBigInt false)
    | UserVariableType.UnsignedInteger -> ColumnWire.metadataOfType(TBigInt true)
    | UserVariableType.Decimal -> ColumnWire.metadataOfType(TDecimal(65, 30, false))
    | UserVariableType.Double -> ColumnWire.metadataOfType(TDouble false)
    | UserVariableType.Text -> ColumnWire.metadataOfType(TVarchar 16383)
    | UserVariableType.Binary -> ColumnWire.metadataOfType TLongBlob

/// Captures types on individual reads, leaving their values live during execution.
let capture variables statement =
    Fsdb.Sql.Expression.rewriteStatement
        (function
        | UserVariable variable when variable.Sql <> "@" ->
            let variableType = variables |> Map.tryFind variable.Name |> Option.defaultValue VNull |> typeOf
            Some(UserVariable { variable with PreparedType = Some variableType })
        | _ -> None)
        statement

let private integerPrefix = Regex(@"^\s*[+-]?[0-9]+")
let private minimumInteger = bigint Int64.MinValue
let private maximumInteger = bigint UInt64.MaxValue
let private integerModulus = maximumInteger + 1I

let private integerBits value =
    let integer =
        match value with
        | VInt number -> bigint number
        | VUInt number | VBit(_, number) -> bigint number
        | VDecimal number -> bigint (Math.Round(number, MidpointRounding.AwayFromZero))
        | VDouble number when Double.IsFinite number -> BigInteger(Math.Truncate number)
        | VDouble _ -> 0I
        | _ ->
            let matched = integerPrefix.Match(toText value |> Option.defaultValue "")
            if matched.Success then BigInteger.Parse(matched.Value, CultureInfo.InvariantCulture) else 0I
    let bounded = max minimumInteger (min maximumInteger integer)
    uint64 (if bounded < 0I then bounded + integerModulus else bounded)

let private decimalValue value =
    match value with
    | VDecimal _ -> Ok value
    | VInt number -> Ok(VDecimal(decimal number))
    | VUInt number | VBit(_, number) -> Ok(VDecimal(decimal number))
    | VDouble number ->
        let text = number.ToString("F28", CultureInfo.InvariantCulture)
        match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, number -> Ok(VDecimal number)
        | _ -> Error(1292, sprintf "Truncated incorrect DECIMAL value: '%s'" text)
    | _ ->
        let text = toText value |> Option.defaultValue ""
        match tryLeadingDecimal text with
        | Some number -> Ok(VDecimal number)
        | None -> Error(1292, sprintf "Truncated incorrect DECIMAL value: '%s'" text)

/// User-variable integer reads round decimals but truncate doubles and text.
let convert variableType value =
    match value, variableType with
    | VNull, _ -> Ok VNull
    | _, UserVariableType.SignedInteger -> Ok(VInt(int64 (integerBits value)))
    | _, UserVariableType.UnsignedInteger -> Ok(VUInt(integerBits value))
    | _, UserVariableType.Decimal -> decimalValue value
    | _, UserVariableType.Double -> Ok(VDouble(toDouble value))
    | _, UserVariableType.Text -> Ok(VString(toText value |> Option.defaultValue ""))
    | VBytes _, UserVariableType.Binary -> Ok value
    | _, UserVariableType.Binary -> Ok(VBytes(Encoding.UTF8.GetBytes(toText value |> Option.defaultValue "")))
