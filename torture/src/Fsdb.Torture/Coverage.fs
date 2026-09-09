namespace Fsdb.Torture

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection

[<CLIMutable>]
type CoverageEvidence =
    { Axis: string
      Source: string }

[<CLIMutable>]
type CapabilityCoverage =
    { Id: string
      Area: string
      Name: string
      ApplicableAxes: string array
      Evidence: CoverageEvidence array
      MissingAxes: string array }

[<CLIMutable>]
type CoverageManifest =
    { SchemaVersion: int
      GeneratedUtc: string
      Capabilities: CapabilityCoverage array }

[<RequireQualifiedAccess>]
module Coverage =
    let private snakeCase (value: string) =
        if value = value.ToUpperInvariant() then
            value.ToLowerInvariant()
        else
            Regex.Replace(value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()

    let private unionCaseNames ty =
        FSharpType.GetUnionCases ty
        |> Array.map _.Name
        |> Array.sort

    let private statementCaseName statement =
        let case, _ = FSharpValue.GetUnionFields(statement, typeof<Fsdb.Ast.Statement>)
        case.Name

    let private skipQuoted (sql: string) start quote =
        let mutable index = start + 1
        let mutable closed = false

        while index < sql.Length && not closed do
            if quote <> '`' && sql.[index] = '\\' && index + 1 < sql.Length then
                index <- index + 2
            elif sql.[index] = quote && index + 1 < sql.Length && sql.[index + 1] = quote then
                index <- index + 2
            elif sql.[index] = quote then
                closed <- true
                index <- index + 1
            else
                index <- index + 1

        index

    let private functionCalls (sql: string) =
        let calls = ResizeArray<string>()
        let mutable index = 0

        while index < sql.Length do
            match sql.[index] with
            | '\''
            | '"'
            | '`' as quote -> index <- skipQuoted sql index quote
            | '#' ->
                let newline = sql.IndexOf('\n', index + 1)
                index <- if newline < 0 then sql.Length else newline + 1
            | '-' when index + 2 < sql.Length && sql.[index + 1] = '-' && Char.IsWhiteSpace sql.[index + 2] ->
                let newline = sql.IndexOf('\n', index + 2)
                index <- if newline < 0 then sql.Length else newline + 1
            | '/' when index + 1 < sql.Length && sql.[index + 1] = '*' ->
                let ending = sql.IndexOf("*/", index + 2, StringComparison.Ordinal)
                index <- if ending < 0 then sql.Length else ending + 2
            | value when Char.IsLetter value || value = '_' ->
                let start = index
                index <- index + 1

                while index < sql.Length && (Char.IsLetterOrDigit sql.[index] || sql.[index] = '_' || sql.[index] = '$') do
                    index <- index + 1

                let mutable following = index

                while following < sql.Length && Char.IsWhiteSpace sql.[following] do
                    following <- following + 1

                if following < sql.Length && sql.[following] = '(' then
                    calls.Add(sql.Substring(start, index - start).ToUpperInvariant())
            | _ -> index <- index + 1

        calls |> Set.ofSeq

    let private corpusSql () =
        seq {
            yield!
                SyntaxFuzz.candidates 1UL 1 0
                |> Seq.filter _.Baseline
                |> Seq.map _.Sql

            for scenario in ScenarioName.all do
                yield! ScenarioProbes.all scenario |> Seq.map snd
        }

    let private baselineStatements () =
        SyntaxFuzz.candidates 1UL 1 0
        |> Seq.filter _.Baseline
        |> Seq.choose (fun candidate ->
            match Fsdb.Parser.parse candidate.Sql with
            | Ok statement -> Some(statementCaseName statement)
            | Error _ -> None)
        |> Set.ofSeq

    let private allSql, private allFunctionCalls =
        let sql = corpusSql () |> Seq.toArray
        sql, sql |> Seq.collect functionCalls |> Set.ofSeq

    let private protocolCapabilities () =
        let source = Path.Combine(Paths.repoRoot (), "src", "Fsdb", "Wire", "Protocol.fs")
        let supported = Fsdb.Protocol.serverCapabilities true ||| Fsdb.Protocol.ClientLocalFiles

        Regex.Matches(File.ReadAllText source, @"(?m)^let (Client[A-Za-z0-9]+) = 0x([0-9A-Fa-f]+)u$")
        |> Seq.cast<Match>
        |> Seq.choose (fun matched ->
            let bit = Convert.ToUInt32(matched.Groups.[2].Value, 16)
            if supported &&& bit <> 0u then Some matched.Groups.[1].Value else None)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

    let private evidence axis source = { Axis = axis; Source = source }

    let private item area name applicable evidenceItems =
        let distinctEvidence = evidenceItems |> Array.distinctBy (fun item -> item.Axis, item.Source)
        let covered = distinctEvidence |> Array.map _.Axis |> Set.ofArray

        { Id = area + ":" + snakeCase name
          Area = area
          Name = name
          ApplicableAxes = applicable
          Evidence = distinctEvidence
          MissingAxes = applicable |> Array.filter (covered.Contains >> not) }

    let private statementCapabilities () =
        let baselines = baselineStatements ()
        let dml = set [ "Insert"; "InsertSelect"; "Replace"; "ReplaceSelect"; "ReplaceSet"; "Update"; "Delete"; "Truncate"; "LoadData" ]
        let ddl = set [ "CreateDatabase"; "DropDatabase"; "AlterDatabase"; "CreateTable"; "CreateTableLike"; "CreateTableAs"; "DropTable"; "AlterTable"; "RenameTable"; "CreateIndex"; "DropIndexStmt"; "CreateTrigger"; "DropTrigger"; "CreateView"; "DropView" ]

        unionCaseNames typeof<Fsdb.Ast.Statement>
        |> Array.map (fun name ->
            let axes =
                [| yield "parser"
                   yield "text-differential"
                   yield "error-contract"

                   if name = "Select" || name = "Union" || dml.Contains name then
                       yield "prepared-protocol"

                   if dml.Contains name || ddl.Contains name then
                       yield "recovery"

                   if dml.Contains name then
                       yield "concurrency" |]

            let baselineEvidence =
                if baselines.Contains name then
                    [| evidence "parser" "syntax-baseline"; evidence "text-differential" "syntax-baseline"; evidence "error-contract" "syntax-mutations" |]
                else
                    [||]

            item "statement" name axes baselineEvidence)

    let private typeCapabilities () =
        let keyword =
            function
            | "TTinyInt" -> "TINYINT"
            | "TBool" -> "BOOLEAN"
            | "TSmallInt" -> "SMALLINT"
            | "TMediumInt" -> "MEDIUMINT"
            | "TInt" -> "INT"
            | "TBigInt" -> "BIGINT"
            | "TBit" -> "BIT"
            | "TChar" -> "CHAR"
            | "TVarchar" -> "VARCHAR"
            | "TTinyText" -> "TINYTEXT"
            | "TText" -> "TEXT"
            | "TMediumText" -> "MEDIUMTEXT"
            | "TLongText" -> "LONGTEXT"
            | "TBinary" -> "BINARY"
            | "TVarBinary" -> "VARBINARY"
            | "TTinyBlob" -> "TINYBLOB"
            | "TBlob" -> "BLOB"
            | "TMediumBlob" -> "MEDIUMBLOB"
            | "TLongBlob" -> "LONGBLOB"
            | "TEnum" -> "ENUM"
            | "TSet" -> "SET"
            | "TDecimal" -> "DECIMAL"
            | "TDouble" -> "DOUBLE"
            | "TFloat" -> "FLOAT"
            | "TDate" -> "DATE"
            | "TDateTime" -> "DATETIME"
            | "TTimestamp" -> "TIMESTAMP"
            | "TTime" -> "TIME"
            | "TYear" -> "YEAR"
            | "TJson" -> "JSON"
            | "TGeometry" -> "GEOMETRY"
            | "TVector" -> "VECTOR"
            | name -> name.ToUpperInvariant()

        unionCaseNames typeof<Fsdb.Ast.ColumnType>
        |> Array.map (fun name ->
            let seen = allSql |> Array.exists (fun sql -> Regex.IsMatch(sql, "\\b" + Regex.Escape(keyword name) + "\\b", RegexOptions.IgnoreCase))
            let textEvidence = if seen then [| evidence "parser" "torture-corpus"; evidence "text-differential" "torture-corpus" |] else [||]
            item "column-type" name [| "parser"; "text-differential"; "error-contract"; "prepared-protocol"; "recovery" |] textEvidence)

    let private functionCapabilities () =
        let scalars = Fsdb.Functions.builtins.Scalars |> Map.keys |> Set.ofSeq
        let aggregates = Fsdb.Functions.builtins.Aggregates |> Map.keys |> Set.ofSeq

        Set.union scalars aggregates
        |> Set.toArray
        |> Array.map (fun name ->
            let invoked = allFunctionCalls.Contains name
            let textEvidence = if invoked then [| evidence "parser" "torture-corpus"; evidence "text-differential" "torture-corpus" |] else [||]
            item "function" name [| "parser"; "text-differential"; "error-contract"; "prepared-protocol" |] textEvidence)

    let private protocolCapabilitiesItems () =
        protocolCapabilities ()
        |> Array.map (fun name -> item "protocol-capability" name [| "wire-success"; "malformed-input"; "connection-lifecycle" |] [||])

    let create () =
        { SchemaVersion = 1
          GeneratedUtc = DateTimeOffset.UtcNow.ToString("O")
          Capabilities =
            Array.concat [ statementCapabilities (); typeCapabilities (); functionCapabilities (); protocolCapabilitiesItems () ]
            |> Array.sortBy (fun capability -> capability.Area, capability.Name) }

    let summary (manifest: CoverageManifest) =
        manifest.Capabilities
        |> Array.groupBy _.Area
        |> Array.sortBy fst
        |> Array.map (fun (area, capabilities) ->
            let complete = capabilities |> Array.filter (fun capability -> Array.isEmpty capability.MissingAxes) |> Array.length
            area, capabilities.Length, complete)

    let write directory =
        Directory.CreateDirectory directory |> ignore
        let manifest = create ()
        Json.write (Path.Combine(directory, "coverage.json")) manifest
        manifest
