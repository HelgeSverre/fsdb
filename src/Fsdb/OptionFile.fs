/// MySQL option-file entries for server configuration.
module Fsdb.OptionFile

open System
open System.IO
open System.Collections.Generic

/// One option in a server-readable option-file group.
type Entry =
    { Name: string
      Value: string option
      Source: string
      Line: int }

/// Entries and syntax errors read from one or more option files.
type Parsed =
    { Entries: Entry list
      Errors: string list }

/// my.cnf treats `-` and `_` in an option name as the same character, and
/// option names are case-insensitive.
let normalizeName (name: string) = name.Trim().Replace('-', '_').ToLowerInvariant()

/// Cuts a `#` or `;` comment, which may start mid-line, without cutting one
/// inside a quoted value — MySQL's own rule is to quote a value containing a
/// comment character.
let private stripComment (line: string) : string =
    let mutable quote = '\000'
    let mutable cut = -1
    let mutable i = 0

    while cut < 0 && i < line.Length do
        let c = line.[i]

        if quote <> '\000' then
            if c = '\\' then i <- i + 1
            elif c = quote then quote <- '\000'
        elif c = '"' || c = '\'' then
            quote <- c
        elif c = '#' || c = ';' then
            cut <- i

        i <- i + 1

    if cut < 0 then line else line.Substring(0, cut)

/// Unwraps a quoted value and expands the escapes MySQL recognises inside
/// quotes. A backslash before anything else stays literal, backslash
/// included, which is also MySQL's behaviour.
let private unquote (raw: string) : string =
    let raw = raw.Trim()

    if raw.Length >= 2 && (raw.[0] = '"' || raw.[0] = '\'') && raw.[raw.Length - 1] = raw.[0] then
        let inner = raw.Substring(1, raw.Length - 2)
        let output = Text.StringBuilder()
        let mutable i = 0

        while i < inner.Length do
            if inner.[i] = '\\' && i + 1 < inner.Length then
                output.Append(
                    match inner.[i + 1] with
                    | 'b' -> "\b"
                    | 't' -> "\t"
                    | 'n' -> "\n"
                    | 'r' -> "\r"
                    | 's' -> " "
                    | '\\' -> "\\"
                    | other -> "\\" + string other
                )
                |> ignore

                i <- i + 2
            else
                output.Append inner.[i] |> ignore
                i <- i + 1

        output.ToString()
    else
        raw

let private maxIncludeDepth = 10

let private isServerGroup (name: string) =
    match normalizeName name with
    | "mysqld"
    | "mysqld_8.4"
    | "server" -> true
    | _ -> false

let rec private parseInto
    (errors: ResizeArray<string>)
    (entries: ResizeArray<Entry>)
    (visited: HashSet<string>)
    (depth: int)
    (source: string)
    (lines: string seq)
    : unit =
    let baseDir =
        match Path.GetDirectoryName source with
        | null
        | "" -> "."
        | directory -> directory

    let mutable applies = false

    lines
    |> Seq.iteri (fun index raw ->
        let fail message = errors.Add(sprintf "%s:%d: %s" source (index + 1) message)
        let line = (stripComment raw).Trim()

        if line = "" then
            ()
        elif line.StartsWith "[" then
            if line.EndsWith "]" then
                applies <- line.Substring(1, line.Length - 2) |> isServerGroup
            else
                fail (sprintf "unterminated group header '%s'" line)
        elif line.StartsWith "!" then
            let directive, rest =
                match line.IndexOf ' ' with
                | -1 -> line, ""
                | at -> line.Substring(0, at), line.Substring(at + 1).Trim()

            let resolve (path: string) = if Path.IsPathRooted path then path else Path.Combine(baseDir, path)

            match directive with
            | "!include"
            | "!includedir" when rest = "" -> fail (sprintf "%s needs a path" directive)
            | "!include" -> includeFile errors entries visited depth fail (resolve rest)
            | "!includedir" ->
                let directory = resolve rest

                if not (Directory.Exists directory) then
                    fail (sprintf "!includedir: no such directory '%s'" directory)
                else
                    Directory.GetFiles(directory, "*.cnf")
                    |> Array.sortWith (fun left right -> String.CompareOrdinal(left, right))
                    |> Array.iter (includeFile errors entries visited depth fail)
            | other -> fail (sprintf "unknown directive '%s'" other)
        elif applies then
            let name, value =
                match line.IndexOf '=' with
                | -1 -> line, None
                | at -> line.Substring(0, at), Some(unquote (line.Substring(at + 1)))

            entries.Add
                { Name = name.Trim()
                  Value = value
                  Source = source
                  Line = index + 1 })

and private includeFile
    (errors: ResizeArray<string>)
    (entries: ResizeArray<Entry>)
    (visited: HashSet<string>)
    (depth: int)
    (fail: string -> unit)
    (path: string)
    : unit =
    if depth >= maxIncludeDepth then
        fail (sprintf "!include nested more than %d deep at '%s'" maxIncludeDepth path)
    else
        let full =
            try
                Path.GetFullPath path
            with _ ->
                path

        if visited.Add full then
            match (try Ok(File.ReadAllLines full) with ex -> Error ex.Message) with
            | Error message -> fail (sprintf "!include '%s': %s" path message)
            | Ok lines -> parseInto errors entries visited (depth + 1) full lines

let private parsed (errors: ResizeArray<string>) (entries: ResizeArray<Entry>) =
    { Entries = List.ofSeq entries
      Errors = List.ofSeq errors }

/// Reads server options from `lines` without applying them.
let parseLines (source: string) (lines: string seq) : Parsed =
    let errors = ResizeArray<string>()
    let entries = ResizeArray<Entry>()
    parseInto errors entries (HashSet<string>()) 0 source lines
    parsed errors entries

/// Reads server options from `lines` without applying them.
let readLines (source: string) (lines: string seq) : Result<Entry list, string> =
    let result = parseLines source lines
    if List.isEmpty result.Errors then Ok result.Entries else Error(String.concat "\n" result.Errors)

/// Reads one my.cnf-style file without applying its server options.
let parseFile (path: string) : Parsed =
    match (try Ok(File.ReadAllLines path) with ex -> Error ex.Message) with
    | Error message ->
        { Entries = []
          Errors = [ sprintf "%s: %s" path message ] }
    | Ok lines ->
        let errors = ResizeArray<string>()
        let entries = ResizeArray<Entry>()
        let visited = HashSet<string>()
        visited.Add(try Path.GetFullPath path with _ -> path) |> ignore
        parseInto errors entries visited 0 path lines
        parsed errors entries

/// Reads one my.cnf-style file without applying its server options.
let readFile (path: string) : Result<Entry list, string> =
    let result = parseFile path
    if List.isEmpty result.Errors then Ok result.Entries else Error(String.concat "\n" result.Errors)

/// Server option files in MySQL's Unix precedence order.
let defaultFilePaths () : string list =
    let mysqlHome = Environment.GetEnvironmentVariable "MYSQL_HOME"
    let userHome = Environment.GetFolderPath Environment.SpecialFolder.UserProfile

    [ "/etc/my.cnf"
      "/etc/mysql/my.cnf"
      if not (String.IsNullOrWhiteSpace mysqlHome) then
          Path.Combine(mysqlHome, "my.cnf")
      if not (String.IsNullOrWhiteSpace userHome) then
          Path.Combine(userHome, ".my.cnf") ]
    |> List.distinct

/// Reads existing option files from least to most specific.
let parseFiles (paths: string list) : Parsed =
    paths
    |> List.filter File.Exists
    |> List.fold
        (fun result path ->
            let parsed = parseFile path

            { Entries = result.Entries @ parsed.Entries
              Errors = result.Errors @ parsed.Errors })
        { Entries = []
          Errors = [] }

/// Reads existing option files from least to most specific.
let readFiles (paths: string list) : Result<Entry list, string> =
    let result = parseFiles paths
    if List.isEmpty result.Errors then Ok result.Entries else Error(String.concat "\n" result.Errors)
