module Fsdb.Tests.LimitsTests

open Expecto
open Fsdb.Limits
open Fsdb.Executor
open Fsdb.Session
open Fsdb.QueryHandler

/// Sequenced: every case here writes process-global knobs, so running them
/// alongside anything that reads one would be a coin flip.
let tests =
    testSequenced
    <| testList
        "Limits"
        [ testCase "size suffixes parse the way my.cnf's do"
          <| fun _ ->
              withSettings [] (fun () ->
                  for value, expected in
                      [ "64M", 64 * 1024 * 1024
                        "16K", 16 * 1024
                        "1G", 1024 * 1024 * 1024
                        "1024", 1024
                        " 8M ", 8 * 1024 * 1024 ] do
                      match applySetting "max_allowed_packet" value with
                      | Ok() -> Expect.equal maxAllowedPacket expected (sprintf "'%s' parsed" value)
                      | Error e -> failtestf "expected '%s' to parse, got %s" value e)

          testCase "a value that isn't a plain number with an optional K/M/G suffix is rejected, not guessed at"
          <| fun _ ->
              withSettings [] (fun () ->
                  for value in [ "M"; "64MB"; ""; "sixty-four"; "6 4" ] do
                      match applySetting "max_allowed_packet" value with
                      | Error _ -> ()
                      | Ok() -> failtestf "expected '%s' to be rejected, but it set the knob" value)

          testCase "an unknown setting names itself and lists what is known"
          <| fun _ ->
              match applySetting "max_connektions" "5" with
              | Error message ->
                  Expect.stringContains message "max_connektions" "names the offending key"
                  Expect.stringContains message "max_connections" "lists the real one so the typo is obvious"
              | Ok() -> failtest "expected an unknown setting to be an error, not a silent no-op"

          testCase "a value outside a knob's range is rejected rather than clamped"
          <| fun _ ->
              withSettings [] (fun () ->
                  match applySetting "max_allowed_packet" "512" with
                  | Error message -> Expect.stringContains message "out of range" "says why"
                  | Ok() -> failtest "expected 512 to be below the floor"

                  Expect.equal maxAllowedPacket (64 * 1024 * 1024) "a rejected value leaves the knob alone")

          // The suite runs ~1400 tests in one process. A knob that leaks out
          // of the test that set it silently changes whatever runs next.
          testCase "withSettings restores every knob it touched"
          <| fun _ ->
              let before = maxConnections

              let inside = withSettings [ "max_connections", "7" ] (fun () -> maxConnections)

              Expect.equal inside 7 "the setting applies inside the scope"
              Expect.equal maxConnections before "and is restored on the way out"

          testCase "withSettings restores even when the body throws"
          <| fun _ ->
              let before = maxConnections

              try
                  withSettings [ "max_connections", "9" ] (fun () -> failwith "boom")
              with _ ->
                  ()

              Expect.equal maxConnections before "restored despite the exception"

          testCase "only the [mysqld] section is applied; other sections a real my.cnf carries are ignored"
          <| fun _ ->
              withSettings [] (fun () ->
                  let lines =
                      [ "# a comment"
                        "; another one"
                        "[client]"
                        "max_connections = 1"
                        ""
                        "[mysqld]"
                        "max-connections = 9" // my.cnf accepts dashes for underscores
                        "max_allowed_packet = 16M" ]

                  match applyLines "test.cnf" lines with
                  | Ok() ->
                      Expect.equal maxConnections 9 "[mysqld] applied, with the dash spelling accepted"
                      Expect.equal maxAllowedPacket (16 * 1024 * 1024) "and the suffixed value"
                  | Error e -> failtestf "expected the file to apply, got %s" e)

          testCase "a bad config reports every offending line with its number, not just the first"
          <| fun _ ->
              withSettings [] (fun () ->
                  let lines = [ "[mysqld]"; "bogus = 1"; "max_connections = 9"; "also_bogus = 2"; "no equals sign" ]

                  match applyLines "test.cnf" lines with
                  | Ok() -> failtest "expected the bogus keys to be errors"
                  | Error message ->
                      Expect.stringContains message "test.cnf:2" "first bad line, located"
                      Expect.stringContains message "test.cnf:4" "second bad line too — not just the first"
                      Expect.stringContains message "test.cnf:5" "and the line that isn't key = value"
                      Expect.equal maxConnections 9 "valid lines still applied")

          testCase "a missing defaults file is an error carrying the path, not an unhandled exception"
          <| fun _ ->
              match loadDefaultsFile "/nonexistent/fsdb-does-not-exist.cnf" with
              | Error message -> Expect.stringContains message "fsdb-does-not-exist.cnf" "names the file"
              | Ok() -> failtest "expected a missing file to be an error"

          // Pins the whole precedence chain for a variable backed by a knob:
          // session override beats global override beats the configured
          // limit beats the compiled-in default.
          testCase "a configured limit is what SHOW VARIABLES reports, and a session SET still shadows it"
          <| fun _ ->
              withSettings [ "wait_timeout", "77"; "max_connections", "42" ] (fun () ->
                  let session = create 1 (Fsdb.Storage.create ())

                  match handle session "SELECT @@wait_timeout" |> snd with
                  | ResultSet(_, [ [ Some "77" ] ]) -> ()
                  | other -> failtestf "expected the configured limit, not the compiled-in default, got %A" other

                  match handle session "SHOW VARIABLES LIKE 'max_connections'" |> snd with
                  | ResultSet(_, [ [ Some "max_connections"; Some "42" ] ]) -> ()
                  | other -> failtestf "expected SHOW VARIABLES to report the configured value, got %A" other

                  let session, _ = handle session "SET SESSION wait_timeout = 99"

                  match handle session "SELECT @@wait_timeout" |> snd with
                  | ResultSet(_, [ [ Some "99" ] ]) -> ()
                  | other -> failtestf "expected the session override to win, got %A" other

                  match handle session "SELECT @@GLOBAL.wait_timeout" |> snd with
                  | ResultSet(_, [ [ Some "77" ] ]) -> ()
                  | other -> failtestf "expected GLOBAL to still read the configured limit, got %A" other)

          // `max_allowed_packet` is what the wire actually enforces
          // (`Packet.readPacketAsync`), so a client that reads the variable
          // and a client that gets 1153'd must see the same number. Two
          // copies of it is the bug this module exists to prevent.
          testCase "the advertised max_allowed_packet is the ceiling the wire enforces"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@max_allowed_packet" |> snd with
              | ResultSet(_, [ [ Some advertised ] ]) ->
                  Expect.equal advertised (string maxAllowedPacket) "advertised == enforced"
              | other -> failtestf "expected a single value, got %A" other

          testCase "wait_timeout advertises what the server actually enforces"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@wait_timeout, @@interactive_timeout" |> snd with
              | ResultSet(_, [ [ Some wait; Some interactive ] ]) ->
                  Expect.equal wait (string waitTimeoutSeconds) "not MySQL's 28800 while reaping at 300"
                  Expect.equal interactive wait "interactive_timeout mirrors it — CLIENT_INTERACTIVE is ignored"
              | other -> failtestf "expected both values, got %A" other ]
