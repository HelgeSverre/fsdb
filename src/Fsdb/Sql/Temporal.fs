/// MySQL temporal values whose date component contains a zero.
module Fsdb.Temporal

open System
open System.Globalization
open System.Text.RegularExpressions

type TimeValue =
    private
    | TimeValue of ticks: int64

type TimeParseResult =
    | ParsedTime of ticks: int64
    | TimeComponentsOutOfRange
    | NotATime

let maxTimeTicks =
    (838L * 3600L + 59L * 60L + 59L) * TimeSpan.TicksPerSecond

let timeTicks (TimeValue ticks) = ticks

let timeMagnitude (ticks: int64) : uint64 =
    if ticks < 0L then uint64 (-(ticks + 1L)) + 1UL else uint64 ticks

let tryTimeValue (ticks: int64) : TimeValue option =
    if ticks >= -maxTimeTicks && ticks <= maxTimeTicks && ticks % 10L = 0L then Some(TimeValue ticks) else None

let timeValueOrClamp (ticks: int64) : TimeValue =
    let clamped = max -maxTimeTicks (min maxTimeTicks ticks)
    let microsecondTicks = clamped - clamped % 10L
    TimeValue microsecondTicks

let roundTimeTicksToFsp (fsp: int) (ticks: int64) : int64 =
    let precision = max 0 (min 6 fsp)
    let unit = pown 10UL (7 - precision)
    let magnitude = timeMagnitude ticks
    let remainder = magnitude % unit
    let rounded = magnitude - remainder + (if remainder * 2UL >= unit then unit else 0UL)

    if ticks < 0L then
        if rounded = 9_223_372_036_854_775_808UL then Int64.MinValue else -int64 rounded
    else
        int64 rounded

let private timePattern =
    Regex(@"^([+-])?(?:(\d+)\s+)?(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d+))?$", RegexOptions.CultureInvariant)

let private parseFractionTicks (fraction: string) =
    if fraction = "" then
        0L
    else
        fraction.Substring(0, min 7 fraction.Length).PadRight(7, '0')
        |> fun digits -> Int64.Parse(digits, CultureInfo.InvariantCulture)

let private parseTimeTicks (text: string) : TimeParseResult =
    let matched = timePattern.Match(text.Trim())

    if not matched.Success then
        NotATime
    else
        let number (group: Group) =
            if group.Success then
                match Int64.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, value -> Some value
                | _ -> None
            else
                Some 0L

        match number matched.Groups.[2], number matched.Groups.[3], number matched.Groups.[4], number matched.Groups.[5] with
        | Some days, Some hours, Some minutes, Some seconds when minutes <= 59L && seconds <= 59L ->
            let fraction = matched.Groups.[6].Value
            let fractionTicks = parseFractionTicks fraction
            let totalHours =
                if days > 34L || hours > 838L then
                    839L
                else
                    min 839L (days * 24L + hours)
            let ticks = if totalHours > 838L then maxTimeTicks + 10L else (totalHours * 3600L + minutes * 60L + seconds) * TimeSpan.TicksPerSecond + fractionTicks
            ParsedTime(if matched.Groups.[1].Value = "-" then -ticks else ticks)
        | _ -> TimeComponentsOutOfRange

let private parseTimeNumberTicks (text: string) : TimeParseResult =
    let trimmed = text.Trim()
    let negative, body =
        if trimmed.StartsWith "-" then true, trimmed.Substring 1
        elif trimmed.StartsWith "+" then false, trimmed.Substring 1
        else false, trimmed

    match body.Split '.' with
    | [| whole |]
    | [| whole; _ |] when whole.Length > 0 ->
        let fraction = if body.Contains '.' then body.Substring(body.IndexOf '.' + 1) else ""

        if
            whole |> Seq.forall Char.IsDigit
            && fraction |> Seq.forall Char.IsDigit
        then
            match Int64.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, number ->
                let seconds = number % 100L
                let minutes = number / 100L % 100L
                let hours = number / 10000L

                if minutes > 59L || seconds > 59L then
                    TimeComponentsOutOfRange
                else
                    let ticks =
                        if hours > 838L then
                            maxTimeTicks + 10L
                        else
                            (hours * 3600L + minutes * 60L + seconds) * TimeSpan.TicksPerSecond
                            + parseFractionTicks fraction

                    ParsedTime(if negative then -ticks else ticks)
            | _ -> TimeComponentsOutOfRange
        else
            NotATime
    | _ -> NotATime

let parseTimeInput text =
    match parseTimeTicks text with
    | NotATime -> parseTimeNumberTicks text
    | result -> result

let tryParseTimeTicks (text: string) : int64 option =
    match parseTimeTicks text with
    | ParsedTime ticks -> Some ticks
    | _ -> None

let tryParseTimeNumberTicks (text: string) : int64 option =
    match parseTimeNumberTicks text with
    | ParsedTime ticks -> Some ticks
    | _ -> None

let tryParseTimeInputTicks text =
    match parseTimeInput text with
    | ParsedTime ticks -> Some ticks
    | _ -> None

let tryParseTimeValue text =
    tryParseTimeInputTicks text
    |> Option.map (roundTimeTicksToFsp 6)
    |> Option.bind tryTimeValue

let formatTimeValueFsp (fsp: int) (value: TimeValue) =
    let ticks = timeTicks value
    let sign = if ticks < 0L then "-" else ""
    let magnitude = timeMagnitude ticks
    let totalSeconds = magnitude / uint64 TimeSpan.TicksPerSecond
    let hours = totalSeconds / 3600UL
    let minutes = totalSeconds % 3600UL / 60UL
    let seconds = totalSeconds % 60UL
    let micros = magnitude % uint64 TimeSpan.TicksPerSecond / 10UL
    let baseText = sprintf "%s%02d:%02d:%02d" sign hours minutes seconds

    if fsp <= 0 then
        baseText
    else
        sprintf "%s.%s" baseText ((sprintf "%06d" micros).Substring(0, min 6 fsp))

let formatTimeValue (value: TimeValue) =
    let ticks = timeTicks value
    let micros = timeMagnitude ticks % uint64 TimeSpan.TicksPerSecond / 10UL
    let baseText = formatTimeValueFsp 0 value
    if micros = 0UL then baseText else baseText + "." + micros.ToString("D6").TrimEnd('0')

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
