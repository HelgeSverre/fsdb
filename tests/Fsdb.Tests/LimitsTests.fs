module Fsdb.Tests.LimitsTests

open System
open System.IO
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

          // An unchecked suffix multiply wraps, and the wrapped result can
          // land back inside the accepted range: 18014398509483008K is
          // 2^64 + 1 MiB, so it wraps to exactly 1 MiB and would be applied
          // as if that were the value asked for. The range check alone does
          // not catch it — only the wraps-to-negative cases fail that way.
          testCase "a size that overflows its suffix multiply is rejected, not wrapped into range"
          <| fun _ ->
              withSettings [] (fun () ->
                  for value in [ "18014398509483008K"; "9999999999G"; "9223372036854775807K" ] do
                      match applySetting "max_allowed_packet" value with
                      | Error _ -> ()
                      | Ok() -> failtestf "expected '%s' to overflow and be rejected, got %d" value maxAllowedPacket

                  Expect.equal maxAllowedPacket (64 * 1024 * 1024) "the knob is untouched by a rejected value")

          testCase "withSettings restores what it applied even when a later setting in the same call is bad"
          <| fun _ ->
              let before = maxConnections

              try
                  withSettings [ "max_connections", "7"; "not_a_setting", "1" ] id
              with _ ->
                  ()

              Expect.equal maxConnections before "the first setting was rolled back, not left applied"

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

          testCase "only [mysqld] and [server] are applied; other groups a real my.cnf carries are ignored"
          <| fun _ ->
              withSettings [] (fun () ->
                  let lines =
                      [ "# a comment"
                        "; another one"
                        "[client]"
                        "max_connections = 1"
                        ""
                        "[mysqld-8.4]" // a version-suffixed group fsdb has no version to match
                        "max_connections = 2"
                        ""
                        "[mysqld]"
                        "max-connections = 9" // my.cnf accepts dashes for underscores
                        "[server]"
                        "max_allowed_packet = 16M" ]

                  match applyLines "test.cnf" lines with
                  | Ok() ->
                      Expect.equal maxConnections 9 "[mysqld] applied, with the dash spelling accepted"
                      Expect.equal maxAllowedPacket (16 * 1024 * 1024) "[server] applied too, as mysqld reads it"
                  | Error e -> failtestf "expected the file to apply, got %s" e)

          testCase "a bad config reports every offending line with its number, not just the first"
          <| fun _ ->
              withSettings [] (fun () ->
                  let lines = [ "[mysqld]"; "bogus = 1"; "max_connections = 9"; "also_bogus = 2"; "[unterminated" ]

                  match applyLines "test.cnf" lines with
                  | Ok() -> failtest "expected the bogus keys to be errors"
                  | Error message ->
                      Expect.stringContains message "test.cnf:2" "first bad line, located"
                      Expect.stringContains message "test.cnf:4" "second bad line too — not just the first"
                      Expect.stringContains message "test.cnf:5" "and the unterminated group header"
                      Expect.equal maxConnections 9 "valid lines still applied")

          // A bare name is MySQL's boolean form, so it has to parse as one —
          // otherwise `skip-name-resolve` gets diagnosed as a syntax error
          // when what it really is, to fsdb, is an option we don't have.
          testCase "a bare option name is read as MySQL's boolean form, not as a syntax error"
          <| fun _ ->
              withSettings [] (fun () ->
                  match applyLines "test.cnf" [ "[mysqld]"; "skip-name-resolve" ] with
                  | Ok() -> failtest "fsdb has no such option, so it should still be reported"
                  | Error message ->
                      Expect.stringContains message "unknown setting 'skip_name_resolve'" "diagnosed as unknown, not malformed"
                      Expect.isFalse (message.Contains "expected") "not reported as a syntax error"

                  match applyLines "test.cnf" [ "[mysqld]"; "max_connections" ] with
                  | Ok() -> failtest "a knob that needs a value should not accept the boolean form"
                  | Error message -> Expect.stringContains message "needs a value" "says what's missing")

          testCase "loose- tolerates an option fsdb doesn't have, but not a bad value for one it does"
          <| fun _ ->
              withSettings [] (fun () ->
                  match applyLines "test.cnf" [ "[mysqld]"; "loose-innodb-buffer-pool-size = 2G"; "loose-skip-name-resolve" ] with
                  | Ok() -> ()
                  | Error e -> failtestf "loose- should skip options fsdb doesn't have, got %s" e

                  match applyLines "test.cnf" [ "[mysqld]"; "loose-max_connections = banana" ] with
                  | Ok() -> failtest "loose- must not excuse a bad value for a known option"
                  | Error message -> Expect.stringContains message "not a number" "still validated")

          testCase "comments may start mid-line, and a quoted value keeps its comment characters"
          <| fun _ ->
              withSettings [] (fun () ->
                  match applyLines "test.cnf" [ "[mysqld]"; "max_connections = 9 # trailing comment"; "wait_timeout = 60 ; and this form" ] with
                  | Ok() ->
                      Expect.equal maxConnections 9 "the comment is not part of the value"
                      Expect.equal waitTimeoutSeconds 60 "same for the ; form"
                  | Error e -> failtestf "expected mid-line comments to be stripped, got %s" e

                  // The value is nonsense for a numeric knob, but the point is
                  // that the parser handed the whole quoted string over rather
                  // than truncating it at the #.
                  match applyLines "test.cnf" [ "[mysqld]"; "max_connections = \"9 # not a comment\"" ] with
                  | Ok() -> failtest "expected the quoted text to reach applySetting whole"
                  | Error message -> Expect.stringContains message "9 # not a comment" "quoted # survived")

          testCase "quoted values expand MySQL's escape sequences"
          <| fun _ ->
              // Routed through the error message because every knob fsdb has
              // is numeric: the reported value is the value the parser built.
              match applyLines "test.cnf" [ "[mysqld]"; "max_connections = 'a\\tb\\sc\\\\d'" ] with
              | Ok() -> failtest "expected a parse failure naming the unescaped value"
              | Error message ->
                  Expect.stringContains message "a\tb c\\d" "\\t, \\s and \\\\ expanded inside quotes"

          testCase "!include and !includedir pull in other files, and a cycle terminates"
          <| fun _ ->
              withSettings [] (fun () ->
                  let dir = IO.Path.Combine(IO.Path.GetTempPath(), "fsdb-cnf-" + Guid.NewGuid().ToString "N")
                  let fragments = IO.Path.Combine(dir, "conf.d")
                  IO.Directory.CreateDirectory fragments |> ignore

                  try
                      IO.File.WriteAllText(
                          IO.Path.Combine(dir, "my.cnf"),
                          "[mysqld]\nmax_connections = 9\n!include included.cnf\n!includedir conf.d\n"
                      )

                      IO.File.WriteAllText(IO.Path.Combine(dir, "included.cnf"), "[mysqld]\nwait_timeout = 60\n")
                      IO.File.WriteAllText(IO.Path.Combine(fragments, "a.cnf"), "[mysqld]\nmax_allowed_packet = 16M\n")
                      // Not a .cnf, so !includedir must not read it.
                      IO.File.WriteAllText(IO.Path.Combine(fragments, "notes.txt"), "[mysqld]\nmax_connections = 1\n")

                      match loadDefaultsFile (IO.Path.Combine(dir, "my.cnf")) with
                      | Ok() ->
                          Expect.equal maxConnections 9 "the including file applied"
                          Expect.equal waitTimeoutSeconds 60 "!include applied"
                          Expect.equal maxAllowedPacket (16 * 1024 * 1024) "!includedir applied the .cnf fragment"
                      | Error e -> failtestf "expected the include tree to apply, got %s" e

                      // A file that includes itself must stop, not recurse to
                      // the depth limit reporting the same errors per level.
                      IO.File.WriteAllText(IO.Path.Combine(dir, "loop.cnf"), "[mysqld]\n!include loop.cnf\nwait_timeout = 61\n")

                      match loadDefaultsFile (IO.Path.Combine(dir, "loop.cnf")) with
                      | Ok() -> Expect.equal waitTimeoutSeconds 61 "the self-including file still applied its own lines"
                      | Error e -> failtestf "a self-include should be ignored, not an error: %s" e
                  finally
                      try
                          IO.Directory.Delete(dir, true)
                      with _ ->
                          ())

          testCase "a missing !include target is reported against the line that asked for it"
          <| fun _ ->
              withSettings [] (fun () ->
                  match applyLines "test.cnf" [ "[mysqld]"; "!include /nonexistent/nope.cnf" ] with
                  | Ok() -> failtest "expected the missing include to be reported"
                  | Error message ->
                      Expect.stringContains message "test.cnf:2" "located at the directive"
                      Expect.stringContains message "nope.cnf" "names the file it could not read")

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

          testCase "SET GLOBAL applies live limits and rejects an invalid batch atomically"
          <| fun _ ->
              withSettings [] (fun () ->
                  let store = Fsdb.Storage.create ()
                  let session = create 1 store
                  let session, setResult = handle session "SET GLOBAL max_connections = 17"
                  Expect.equal setResult (Affected 0UL) "the runtime limit is accepted"
                  Expect.equal maxConnections 17 "the server-facing knob changed"

                  match handle session "SELECT @@GLOBAL.max_connections" |> snd with
                  | ResultSet(_, [ [ Some "17" ] ]) -> ()
                  | other -> failtestf "expected the live global value, got %A" other

                  let _, badResult = handle session "SET GLOBAL max_connections = 19, @@GLOBAL.max_allowed_packet = 512"

                  match badResult with
                  | Err(1232, _) -> ()
                  | other -> failtestf "expected an invalid limit value to be rejected, got %A" other

                  Expect.equal maxConnections 17 "the valid assignment was not partially applied")

          testCase "global-only limits reject session SET instead of advertising a local change"
          <| fun _ ->
              withSettings [] (fun () ->
                  let session = create 1 (Fsdb.Storage.create ())

                  match handle session "SET SESSION max_connections = 17" |> snd with
                  | Err(1229, message) -> Expect.stringContains message "GLOBAL variable" "the required scope is clear"
                  | other -> failtestf "expected SET SESSION max_connections to fail, got %A" other

                  Expect.notEqual maxConnections 17 "the rejected SET did not change the live cap")

          testCase "cte_max_recursion_depth is scoped to the session"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let limited = create 1 store
              let limited, setResult = handle limited "SET SESSION cte_max_recursion_depth = 2"
              Expect.equal setResult (Affected 0UL) "the session limit is accepted"

              match handle limited "WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < 3) SELECT * FROM s" |> snd with
              | Err(3636, message) -> Expect.stringContains message "after 3 iterations" "the effective limit is named"
              | other -> failtestf "expected the limited session to stop at depth 2, got %A" other

              let unlimited, _ = handle (create 2 store) "SET SESSION cte_max_recursion_depth = 0"

              match handle unlimited "WITH RECURSIVE s AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM s WHERE n < 3) SELECT * FROM s" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ]) -> ()
              | other -> failtestf "expected zero to disable the recursion limit, got %A" other

              match handle limited "SELECT @@SESSION.cte_max_recursion_depth" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected the limited session to retain its own value, got %A" other

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
