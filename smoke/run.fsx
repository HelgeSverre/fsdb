open System
open System.Diagnostics
open System.IO
open System.Threading

type Options =
    { BuildOnly: bool
      SkipBuild: bool
      Targets: string list }

type Outcome =
    { Target: string
      Description: string
      Passed: bool }

let availableTargets =
    [ "gitea"
      "mediawiki"
      "drupal"
      "nextcloud"
      "shopware"
      "ghost"
      "moodle"
      "wordpress"
      "rails"
      "magento" ]

let requiredServices target =
    match target with
    | "nextcloud"
    | "shopware" -> [ "fsdb"; "redis" ]
    | "magento" -> [ "fsdb"; "opensearch" ]
    | _ -> [ "fsdb" ]

let usage () =
    printfn "usage: dotnet fsi --nologo --readline- --exec smoke/run.fsx -- [--build-only] [--no-build] [target ...]"
    printfn "targets: %s" (String.concat " " availableTargets)

let rec parse options arguments =
    match arguments with
    | [] -> Ok options
    | "--build-only" :: rest -> parse { options with BuildOnly = true } rest
    | "--no-build" :: rest -> parse { options with SkipBuild = true } rest
    | ("--help" | "-h") :: _ -> Error None
    | target :: rest when List.contains target availableTargets ->
        parse { options with Targets = options.Targets @ [ target ] } rest
    | unknown :: _ -> Error(Some(sprintf "unknown smoke target '%s'" unknown))

let startProcess redirectOutput executable arguments =
    let startInfo = ProcessStartInfo(executable, UseShellExecute = false)
    startInfo.RedirectStandardOutput <- redirectOutput
    startInfo.RedirectStandardError <- redirectOutput

    for argument in arguments do
        startInfo.ArgumentList.Add(argument)

    new Process(StartInfo = startInfo)

let runQuiet executable arguments =
    use child = startProcess true executable arguments
    child.Start() |> ignore
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    stdout.GetAwaiter().GetResult() |> ignore
    stderr.GetAwaiter().GetResult() |> ignore
    child.ExitCode

let run executable arguments =
    use child = startProcess false executable arguments
    child.Start() |> ignore
    child.WaitForExit()
    child.ExitCode

let runLogged logPath executable arguments =
    use writer = new StreamWriter(logPath, false)
    writer.AutoFlush <- true
    use child = startProcess true executable arguments
    let writeGate = obj ()

    let writeLine (line: string) =
        if not (isNull line) then
            lock writeGate (fun () ->
                Console.WriteLine(line)
                writer.WriteLine(line))

    child.OutputDataReceived.Add(fun event -> writeLine event.Data)
    child.ErrorDataReceived.Add(fun event -> writeLine event.Data)
    child.Start() |> ignore
    child.BeginOutputReadLine()
    child.BeginErrorReadLine()
    child.WaitForExit()
    child.ExitCode

let smokeDirectory = __SOURCE_DIRECTORY__
let arguments = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList

let options =
    match parse { BuildOnly = false; SkipBuild = false; Targets = [] } arguments with
    | Ok parsed ->
        if List.isEmpty parsed.Targets then
            { parsed with Targets = availableTargets }
        else
            parsed
    | Error message ->
        message |> Option.iter (eprintfn "error: %s")
        usage ()
        Environment.Exit(if Option.isSome message then 2 else 0)
        failwith "unreachable"

if runQuiet "docker" [ "info" ] <> 0 then
    eprintfn "error: Docker with a reachable daemon is required"
    Environment.Exit(1)

let campaign = sprintf "fsdb-smoke-%d-%d" (Environment.ProcessId) (Random.Shared.Next(100000, 999999))

let composeArguments =
    [ "compose"
      "--ansi"
      "never"
      "--project-name"
      campaign
      "--env-file"
      Path.Combine(smokeDirectory, "versions.env")
      "--file"
      Path.Combine(smokeDirectory, "compose.yaml") ]

let compose arguments = run "docker" (composeArguments @ arguments)
let composeQuiet arguments = runQuiet "docker" (composeArguments @ arguments)

let cleanup () =
    composeQuiet [ "down"; "--volumes"; "--remove-orphans" ] |> ignore

Console.CancelKeyPress.Add(fun event ->
    event.Cancel <- true
    cleanup ()
    Environment.Exit(130))

let runIdentifier =
    sprintf "%s-%d" (DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")) Environment.ProcessId

let resultsDirectory = Path.Combine(smokeDirectory, "results", runIdentifier)
Directory.CreateDirectory(resultsDirectory) |> ignore
let mutable campaignExitCode = 0

try
    if options.BuildOnly then
        let status = compose ([ "build"; "fsdb" ] @ options.Targets)

        if status = 0 then
            printfn "built: %s" (String.concat " " options.Targets)

        cleanup ()
        Environment.Exit(status)

    let mutable outcomes = []

    for target in options.Targets do
        cleanup ()
        let buildLog = Path.Combine(resultsDirectory, target + "-build.log")
        let runLog = Path.Combine(resultsDirectory, target + ".log")
        let serverLog = Path.Combine(resultsDirectory, target + "-fsdb.log")

        let buildStatus =
            if options.SkipBuild then
                0
            else
                runLogged buildLog "docker" (composeArguments @ [ "build"; "fsdb"; target ])

        let serviceArguments =
            [ "up"; "--detach" ]
            @ (if options.SkipBuild then [ "--no-build" ] else [])
            @ requiredServices target

        if buildStatus <> 0 then
            outcomes <-
                { Target = target
                  Description = "build failed"
                  Passed = false }
                :: outcomes
        elif compose serviceArguments <> 0 then
            outcomes <-
                { Target = target
                  Description = "services failed to start"
                  Passed = false }
                :: outcomes
        else
            let mutable ready = false
            let mutable attempt = 0

            while not ready && attempt < 60 do
                ready <-
                    composeQuiet
                        [ "run"
                          "--rm"
                          "--no-deps"
                          "probe"
                          "--protocol=tcp"
                          "--host=fsdb"
                          "--user=root"
                          "ping"
                          "--silent" ] = 0

                if not ready then
                    Thread.Sleep(1000)

                attempt <- attempt + 1

            if not ready then
                runLogged serverLog "docker" (composeArguments @ [ "logs"; "--no-color"; "fsdb" ])
                |> ignore

                outcomes <-
                    { Target = target
                      Description = "fsdb failed to become ready"
                      Passed = false }
                    :: outcomes
            else
                let runStatus =
                    let targetEnvironment =
                        if target = "drupal" then
                            [ yield "--env"
                              yield "SMOKE_RUN_ID=" + runIdentifier

                              for name in [ "DRUPAL_CONCURRENCY"; "DRUPAL_HTTP_WORKERS"; "DRUPAL_TEST_CLASSES" ] do
                                  match Environment.GetEnvironmentVariable(name) with
                                  | null
                                  | "" -> ()
                                  | value ->
                                      yield "--env"
                                      yield name + "=" + value ]
                        else
                            []

                    runLogged runLog "docker" (composeArguments @ [ "run"; "--rm"; "--no-deps" ] @ targetEnvironment @ [ target ])

                runLogged serverLog "docker" (composeArguments @ [ "logs"; "--no-color"; "fsdb" ])
                |> ignore

                outcomes <-
                    { Target = target
                      Description = if runStatus = 0 then "passed" else sprintf "failed (%d)" runStatus
                      Passed = runStatus = 0 }
                    :: outcomes

    printfn ""
    printfn "External compatibility smoke summary"

    for outcome in List.rev outcomes do
        printfn "  %s: %s" outcome.Target outcome.Description

    printfn "logs: %s" resultsDirectory

    if outcomes |> List.exists (fun outcome -> not outcome.Passed) then
        campaignExitCode <- 1
finally
    cleanup ()

Environment.Exit(campaignExitCode)
