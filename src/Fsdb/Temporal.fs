/// MySQL temporal values whose date component contains a zero.
module Fsdb.Temporal

open System
open System.Globalization

type ZeroDate =
    private
    | ZeroDate of year: int * month: int * day: int

type ZeroDateTime =
    private
    | ZeroDateTime of date: ZeroDate * hour: int * minute: int * second: int * microseconds: int

let tryZeroDate (year: int) (month: int) (day: int) : ZeroDate option =
    let calendarYear = if year = 0 then 2000 else year

    let validDay =
        month = 0
        || day = 0
        || day <= DateTime.DaysInMonth(calendarYear, month)

    if
        year >= 0
        && year <= 9999
        && month >= 0
        && month <= 12
        && day >= 0
        && day <= 31
        && validDay
        && (year = 0 || month = 0 || day = 0)
    then
        Some(ZeroDate(year, month, day))
    else
        None

let zeroDateParts (ZeroDate(year, month, day)) = year, month, day

let formatZeroDate date =
    let year, month, day = zeroDateParts date
    sprintf "%04d-%02d-%02d" year month day

let tryZeroDateTime (date: ZeroDate) (hour: int) (minute: int) (second: int) (microseconds: int) : ZeroDateTime option =
    if hour >= 0 && hour < 24 && minute >= 0 && minute < 60 && second >= 0 && second < 60 && microseconds >= 0 && microseconds < 1_000_000 then
        Some(ZeroDateTime(date, hour, minute, second, microseconds))
    else
        None

let zeroDateTimeParts (ZeroDateTime(date, hour, minute, second, microseconds)) = date, hour, minute, second, microseconds

let zeroDateOfDateTime dateTime =
    let date, _, _, _, _ = zeroDateTimeParts dateTime
    date

let formatZeroDateTime (dateTime: ZeroDateTime) =
    let date, hour, minute, second, microseconds = zeroDateTimeParts dateTime
    let baseText = sprintf "%s %02d:%02d:%02d" (formatZeroDate date) hour minute second
    if microseconds = 0 then baseText else sprintf "%s.%06d" baseText microseconds

let formatZeroDateTimeFsp (fsp: int) (dateTime: ZeroDateTime) =
    let date, hour, minute, second, microseconds = zeroDateTimeParts dateTime
    let baseText = sprintf "%s %02d:%02d:%02d" (formatZeroDate date) hour minute second
    if fsp <= 0 then baseText else sprintf "%s.%s" baseText ((sprintf "%06d" microseconds).Substring(0, min fsp 6))

let compareZeroDates left right =
    let ly, lm, ld = zeroDateParts left
    let ry, rm, rd = zeroDateParts right
    compare (ly, lm, ld) (ry, rm, rd)

let compareZeroDateTimes left right =
    let ld, lh, lmi, ls, lu = zeroDateTimeParts left
    let rd, rh, rmi, rs, ru = zeroDateTimeParts right
    compare (zeroDateParts ld, lh, lmi, ls, lu) (zeroDateParts rd, rh, rmi, rs, ru)

let compareZeroDateToZeroDateTime left right =
    let rightDate, hour, minute, second, microseconds = zeroDateTimeParts right
    compare (zeroDateParts left, 0, 0, 0, 0) (zeroDateParts rightDate, hour, minute, second, microseconds)

let compareZeroDateToDateTime left (right: DateTime) =
    let year, month, day = zeroDateParts left
    compare (year, month, day, 0, 0, 0, 0) (right.Year, right.Month, right.Day, right.Hour, right.Minute, right.Second, int ((right.Ticks % TimeSpan.TicksPerSecond) / 10L))

let compareZeroDateTimeToDateTime left (right: DateTime) =
    let date, hour, minute, second, microseconds = zeroDateTimeParts left
    let year, month, day = zeroDateParts date
    compare (year, month, day, hour, minute, second, microseconds) (right.Year, right.Month, right.Day, right.Hour, right.Minute, right.Second, int ((right.Ticks % TimeSpan.TicksPerSecond) / 10L))

let isAllZeroDate date = zeroDateParts date = (0, 0, 0)

let isAllZeroDateTime dateTime =
    let date, hour, minute, second, microseconds = zeroDateTimeParts dateTime
    isAllZeroDate date && hour = 0 && minute = 0 && second = 0 && microseconds = 0

let tryParseDateParts (text: string) =
    match text.Split '-' with
    | [| year; month; day |] ->
        match Int32.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture), Int32.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture), Int32.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture) with
        | (true, year), (true, month), (true, day) -> Some(year, month, day)
        | _ -> None
    | _ -> None

let tryParseZeroDate (text: string) =
    tryParseDateParts text |> Option.bind (fun (year, month, day) -> tryZeroDate year month day)

let tryParseZeroDateTime (text: string) =
    let pieces = text.Split([| ' '; 'T' |], StringSplitOptions.RemoveEmptyEntries)

    match pieces with
    | [| dateText; timeText |] ->
        match tryParseZeroDate dateText with
        | Some date ->
            let timeParts = timeText.Split '.'
            let hms = timeParts.[0].Split ':'
            let microseconds =
                if timeParts.Length = 1 then
                    Some 0
                elif timeParts.Length = 2 && timeParts.[1] |> Seq.forall Char.IsDigit && timeParts.[1].Length <= 6 then
                    Int32.TryParse(timeParts.[1].PadRight(6, '0'), NumberStyles.None, CultureInfo.InvariantCulture) |> function | true, value -> Some value | _ -> None
                else
                    None

            match hms, microseconds with
            | [| hour; minute; second |], Some microseconds ->
                match Int32.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture), Int32.TryParse(minute, NumberStyles.None, CultureInfo.InvariantCulture), Int32.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture) with
                | (true, hour), (true, minute), (true, second) -> tryZeroDateTime date hour minute second microseconds
                | _ -> None
            | _ -> None
        | None -> None
    | _ -> None
