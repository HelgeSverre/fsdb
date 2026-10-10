/// Renders declared column values consistently for SQL text results and
/// functional indexes whose input is the column's displayed text.
module internal Fsdb.ColumnDisplay

open Fsdb.Ast
open Fsdb.Value

type OutputColumnFormat =
    { Fsp: int option
      DecimalScale: int option
      ApproximateScale: int option
      Column: ColumnDef option }

let private padNumeric width (text: string) =
    if text.Length >= width then
        text
    elif text.StartsWith("-", System.StringComparison.Ordinal) then
        "-" + text.Substring(1).PadLeft(width - 1, '0')
    else
        text.PadLeft(width, '0')

let renderOutputValue format value =
    let renderDouble number =
        match format.Column |> Option.map _.Type with
        | Some(TFloat _) -> Some(Value.formatFloat (float32 number))
        | _ -> Value.toText value

    let text =
        match format.Column |> Option.bind _.NumericDisplay, value with
        | Some display, VDouble number ->
            match display.Decimals with
            | Some decimals -> Some(Value.formatDoubleWithScale decimals number)
            | None -> renderDouble number
        | None, VDouble number ->
            match format.ApproximateScale with
            | Some scale -> Some(Value.formatDoubleWithScale scale number)
            | None -> renderDouble number
        | Some _, VDecimal number ->
            match format.Column |> Option.map _.Type with
            | Some(TDecimal(_, scale, _)) -> Some(number.ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture))
            | _ -> Value.toText value
        | _, VInt number when format.DecimalScale.IsSome ->
            format.DecimalScale |> Option.map (fun scale -> (decimal number).ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture))
        | _, VUInt number when format.DecimalScale.IsSome ->
            format.DecimalScale |> Option.map (fun scale -> (decimal number).ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture))
        | _, VDecimal number ->
            match format.DecimalScale with
            | Some scale -> Some(number.ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture))
            | None -> Value.toText value
        | _ ->
            match format.Fsp with
            | Some fsp -> Value.toTextFsp fsp value
            | None -> Value.toText value

    match format.Column |> Option.bind _.NumericDisplay, text with
    | Some({ ZeroFill = true } as display), Some rendered ->
        let width =
            match display.Width, format.Column |> Option.map _.Type with
            | Some width, _ -> width
            | None, Some(TDecimal(precision, scale, _)) -> precision + (if scale > 0 then 1 else 0)
            | None, Some(TFloat _) -> 12
            | None, Some(TDouble _) -> 22
            | _ -> rendered.Length

        Some(padNumeric width rendered)
    | _ -> text

let renderStoredColumnValue column value =
    renderOutputValue
        { Fsp = None
          DecimalScale = None
          ApproximateScale = None
          Column = Some column }
        value
