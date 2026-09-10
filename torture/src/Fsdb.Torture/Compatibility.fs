namespace Fsdb.Torture

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open MySqlConnector

type ContractProtocol =
    | TextProtocol
    | PreparedProtocol

type ContractOperation =
    | Execute
    | Query

type OracleExpectation =
    | OracleSuccess
    | OracleValueSuccessIgnoringLabels
    | OracleError of code: int * sqlState: string

type ContractAction =
    | Run of operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Send of pending: string * operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Reap of pending: string
    | PrepareHandle of handle: string * operation: ContractOperation * sql: string * parameters: obj array
    | InvokeHandle of handle: string
    | CloseHandle of handle: string
    | AwaitPending of pending: string

type ContractStep =
    { Name: string
      Connection: string
      Action: ContractAction
      Expectation: OracleExpectation }

type ContractCase =
    { Name: string
      Setup: string array
      Steps: ContractStep array
      Cleanup: string array
      Coverage: (string * string array) array }

[<CLIMutable>]
type ContractTargetOutcome =
    { Target: string
      Status: string
      AffectedRows: int
      Columns: string array
      ColumnTypes: string array
      Rows: string array
      DataSha256: string
      ErrorCode: int
      SqlState: string
      Message: string
      ElapsedMs: int64 }

[<CLIMutable>]
type ContractStepRecord =
    { Name: string
      Connection: string
      Action: string
      Sql: string
      MySql: ContractTargetOutcome
      Fsdb: ContractTargetOutcome
      Classification: string
      Detail: string
      Passed: bool }

[<CLIMutable>]
type ContractCaseRecord =
    { Name: string
      Steps: ContractStepRecord array
      MySqlStateRestored: bool
      FsdbStateRestored: bool
      StateDetail: string
      Passed: bool }

[<CLIMutable>]
type CompatibilityManifest =
    { SchemaVersion: int
      RunId: string
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Cases: ContractCaseRecord array
      Classification: string
      FailureSignature: string
      Passed: bool }

[<CLIMutable>]
type CompatibilityOptions =
    { TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<RequireQualifiedAccess>]
module Contract =
    let execute name sql : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Execute, TextProtocol, sql, [||])
          Expectation = OracleSuccess }

    let query name sql : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Query, TextProtocol, sql, [||])
          Expectation = OracleSuccess }

    let preparedQuery name sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Query, PreparedProtocol, sql, parameters)
          Expectation = OracleSuccess }

    let preparedExecute name sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Execute, PreparedProtocol, sql, parameters)
          Expectation = OracleSuccess }

    let fails code sqlState (step: ContractStep) : ContractStep =
        { step with Expectation = OracleError(code, sqlState) }

    let comparingValues (step: ContractStep) : ContractStep =
        { step with Expectation = OracleValueSuccessIgnoringLabels }

    let on connection (step: ContractStep) : ContractStep = { step with Connection = connection }

    let send pending (step: ContractStep) : ContractStep =
        match step.Action with
        | Run(operation, protocol, sql, parameters) -> { step with Action = Send(pending, operation, protocol, sql, parameters) }
        | Send _
        | Reap _
        | PrepareHandle _
        | InvokeHandle _
        | CloseHandle _
        | AwaitPending _ -> invalidArg (nameof step) "only an ordinary step can be sent"

    let reap name connection pending expectation : ContractStep =
        { Name = name
          Connection = connection
          Action = Reap pending
          Expectation = expectation }

    let prepare name handle operation sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = PrepareHandle(handle, operation, sql, parameters)
          Expectation = OracleSuccess }

    let invoke name handle expectation : ContractStep =
        { Name = name
          Connection = "main"
          Action = InvokeHandle handle
          Expectation = expectation }

    let close name handle : ContractStep =
        { Name = name
          Connection = "main"
          Action = CloseHandle handle
          Expectation = OracleSuccess }

    let awaitPending name pending : ContractStep =
        { Name = name
          Connection = "main"
          Action = AwaitPending pending
          Expectation = OracleSuccess }

[<RequireQualifiedAccess>]
module ContractCatalog =
    type private FunctionContractSpec =
        { Name: string
          Functions: string array
          TextExpression: string
          PreparedExpression: string
          Parameters: obj array
          ResultTypes: string array
          NullPrepared: (string array * string * obj array) option
          WrongArityExpressions: (string * string) array }

    let private functionProbe (name: string) (names: string array) sql =
        [| Contract.query (name + "-text") sql |> Contract.comparingValues
           Contract.preparedQuery (name + "-prepared") sql [||] |> Contract.comparingValues |],
        (names
         |> Array.map (fun functionName ->
             "function:" + functionName.ToLowerInvariant(),
             [| "parser"; "text-differential"; "prepared-protocol" |]))

    let private functionError name (functionName: string) code sqlState sql =
        Contract.query name sql |> Contract.fails code sqlState,
        ("function:" + functionName.ToLowerInvariant(), [| "error-contract" |])

    let private generatedFunctionContracts specs =
        let steps =
            specs
            |> Array.collect (fun spec ->
                [| yield Contract.query (spec.Name + "-text") ("SELECT " + spec.TextExpression) |> Contract.comparingValues
                   yield
                       Contract.preparedQuery
                           (spec.Name + "-prepared")
                           ("SELECT " + spec.PreparedExpression)
                           spec.Parameters
                       |> Contract.comparingValues

                   match spec.NullPrepared with
                   | Some(_, expression, parameters) ->
                       yield
                           Contract.preparedQuery (spec.Name + "-null") ("SELECT " + expression) parameters
                           |> Contract.comparingValues
                   | None -> ()

                   for functionName, expression in spec.WrongArityExpressions do
                       let errorName = spec.Name + "-" + functionName.ToLowerInvariant() + "-error"
                       yield Contract.query (errorName + "-text") ("SELECT " + expression) |> Contract.fails 1582 "42000"
                       yield
                           Contract.preparedQuery (errorName + "-prepared") ("SELECT " + expression) [||]
                           |> Contract.fails 1582 "42000" |])

        let coverage =
            specs
            |> Array.collect (fun spec ->
                let functionsWithErrors = spec.WrongArityExpressions |> Array.map fst |> Set.ofArray
                let functionsWithNulls =
                    spec.NullPrepared
                    |> Option.map (fun (names, _, _) -> Set.ofArray names)
                    |> Option.defaultValue Set.empty
                let functionsWithResultTypes = Set.ofArray spec.ResultTypes

                spec.Functions
                |> Array.map (fun name ->
                    let axes =
                        [| yield "parser"
                           yield "text-differential"
                           yield "prepared-protocol"
                           if functionsWithResultTypes.Contains name then
                               yield "result-type"

                           if functionsWithNulls.Contains name then
                               yield "null-semantics"

                           if functionsWithErrors.Contains name then
                               yield "error-contract" |]

                    "function:" + name.ToLowerInvariant(), axes))

        steps, coverage

    let private comments =
        { Name = "comments-and-precedence"
          Setup = [||]
          Steps =
            [| Contract.query "double-dash-without-space" "SELECT 1--1 AS value"
               Contract.query "ordinary-block-comment" "SELECT 1 /* between operands */ + 2 AS value"
               Contract.query "double-dash-with-space" "SELECT 1-- comment\n AS value"
               Contract.query "hash-comment" "SELECT 1 # comment\n + 2 AS value"
               Contract.query "executable-version-comment" "SELECT /*!80000 1 + */ 2 AS value"
               Contract.query "future-version-comment" "SELECT /*!99999 1 + */ 2 AS value"
               Contract.query
                   "comments-through-cte"
                   "WITH /* after WITH */ cte AS (SELECT 1 AS n) SELECT /* projection */ n FROM cte" |]
          Cleanup = [||]
          Coverage =
            [| "statement:select", [| "parser"; "text-differential" |]
               "syntax:comments", [| "parser"; "text-differential" |] |] }

    let private exactErrors =
        { Name = "syntax-error-contracts"
          Setup = [||]
          Steps =
            [| Contract.query "unterminated-expression" "SELECT (1" |> Contract.fails 1064 "42000"
               Contract.query "missing-table-reference" "SELECT 1 FROM" |> Contract.fails 1064 "42000"
               Contract.query "dash-is-an-operator" "SELECT 1--missing" |> Contract.fails 1054 "42S22" |]
          Cleanup = [||]
          Coverage = [| "statement:select", [| "error-contract" |]; "syntax:comments", [| "error-contract" |] |] }

    let private prepared =
        { Name = "prepared-binary-protocol"
          Setup = [| "DROP TABLE IF EXISTS contract_prepared"; "CREATE TABLE contract_prepared (id INT PRIMARY KEY, label VARCHAR(20))"; "INSERT INTO contract_prepared VALUES (1, 'one'), (2, 'two')" |]
          Steps =
            [| Contract.preparedQuery
                   "typed-parameters"
                   "SELECT id, label FROM contract_prepared WHERE id >= ? AND label <> ? ORDER BY id"
                   [| box 1; box "one" |]
               Contract.preparedQuery
                   "prepared-cte"
                   "WITH selected AS (SELECT id FROM contract_prepared WHERE id = ?) SELECT id FROM selected"
                   [| box 2 |]
               Contract.preparedQuery
                   "prepared-comments"
                   "SELECT /* before */ id FROM contract_prepared WHERE id /* operator */ = /* parameter */ ?"
                   [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_prepared" |]
          Coverage =
            [| "statement:select", [| "prepared-protocol" |]
               "column-type:t_int", [| "prepared-protocol" |]
               "column-type:t_varchar", [| "prepared-protocol" |] |] }

    let private columnTypes =
        { Name = "column-type-roundtrip"
          Setup = [| "DROP TABLE IF EXISTS contract_types"; TypeMatrix.createTable "contract_types"; TypeMatrix.insert "contract_types" |]
          Steps =
            [| Contract.query "text-type-row" (TypeMatrix.select "contract_types" "1")
               Contract.preparedQuery
                   "prepared-numeric-row"
                   (TypeMatrix.selectColumns
                       "contract_types"
                       "c_tiny, c_bool, c_small, c_medium, c_int, c_big, c_bit, c_decimal, c_double, c_float, c_year"
                       "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-text-binary-row"
                   (TypeMatrix.selectColumns
                       "contract_types"
                       "c_char, c_varchar, c_tinytext, c_text, c_mediumtext, c_longtext, c_binary, c_varbinary, c_tinyblob, c_blob, c_mediumblob, c_longblob, c_enum, c_set, c_json"
                       "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-temporal-row"
                   (TypeMatrix.selectColumns "contract_types" "c_date, c_datetime, c_timestamp, c_time" "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-geometry-row"
                   (TypeMatrix.selectColumns "contract_types" "c_geometry" "?")
                   [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_types" |]
          Coverage =
            TypeMatrix.capabilities
            |> Array.map (fun name -> "column-type:" + name, [| "parser"; "text-differential"; "prepared-protocol" |]) }

    let private generatedFunctionFamilies =
        let spec name functions text prepared parameters nullPrepared wrongArityExpressions =
            { Name = name
              Functions = functions
              TextExpression = text
              PreparedExpression = prepared
              Parameters = parameters
              ResultTypes = functions
              NullPrepared = nullPrepared
              WrongArityExpressions = wrongArityExpressions }

        let shapeSpec name functions text prepared parameters nullPrepared wrongArityExpressions =
            { spec name functions text prepared parameters nullPrepared wrongArityExpressions with ResultTypes = [||] }

        let nullValue = box DBNull.Value

        let establishedSpecs =
            [| spec
                   "aes-roundtrip"
                   [| "AES_ENCRYPT"; "AES_DECRYPT" |]
                   "AES_ENCRYPT('hello', 'secret'), AES_DECRYPT(AES_ENCRYPT('hello', 'secret'), 'secret')"
                   "AES_ENCRYPT(?, ?), AES_DECRYPT(AES_ENCRYPT(?, ?), ?)"
                   [| box "hello"; box "secret"; box "hello"; box "secret"; box "secret" |]
                   (Some([| "AES_ENCRYPT" |], "AES_ENCRYPT(?, ?)", [| nullValue; box "secret" |]))
                   [| "AES_ENCRYPT", "AES_ENCRYPT('hello')"; "AES_DECRYPT", "AES_DECRYPT('hello')" |]
               spec
                   "uuid-binary-roundtrip"
                   [| "UUID_TO_BIN"; "BIN_TO_UUID" |]
                   "UUID_TO_BIN('6ccd780c-baba-1026-9564-5b8c656024db', 1), BIN_TO_UUID(UUID_TO_BIN('6ccd780c-baba-1026-9564-5b8c656024db', 1), 1)"
                   "UUID_TO_BIN(?, ?), BIN_TO_UUID(UUID_TO_BIN(?, ?), ?)"
                   [| box "6ccd780c-baba-1026-9564-5b8c656024db"
                      box 1
                      box "6ccd780c-baba-1026-9564-5b8c656024db"
                      box 1
                      box 1 |]
                   (Some([| "UUID_TO_BIN" |], "UUID_TO_BIN(?)", [| nullValue |]))
                   [| "UUID_TO_BIN", "UUID_TO_BIN()"; "BIN_TO_UUID", "BIN_TO_UUID()" |]
               spec
                   "uuid-validation"
                   [| "IS_UUID" |]
                   "IS_UUID('6ccd780c-baba-1026-9564-5b8c656024db')"
                   "IS_UUID(?)"
                   [| box "6ccd780c-baba-1026-9564-5b8c656024db" |]
                   (Some([| "IS_UUID" |], "IS_UUID(?)", [| nullValue |]))
                   [| "IS_UUID", "IS_UUID()" |]
               spec
                   "ipv4-roundtrip"
                   [| "INET_ATON"; "INET_NTOA" |]
                   "INET_ATON('192.0.2.7'), INET_NTOA(3221225991)"
                   "INET_ATON(?), INET_NTOA(?)"
                   [| box "192.0.2.7"; box 3221225991L |]
                   (Some([| "INET_ATON" |], "INET_ATON(?)", [| nullValue |]))
                   [| "INET_ATON", "INET_ATON()"; "INET_NTOA", "INET_NTOA()" |]
               spec
                   "ipv6-roundtrip"
                   [| "INET6_ATON"; "INET6_NTOA" |]
                   "INET6_NTOA(INET6_ATON('2001:db8::7'))"
                   "INET6_NTOA(INET6_ATON(?))"
                   [| box "2001:db8::7" |]
                   (Some([| "INET6_ATON" |], "INET6_ATON(?)", [| nullValue |]))
                   [| "INET6_ATON", "INET6_ATON()"; "INET6_NTOA", "INET6_NTOA()" |]
               spec
                   "ip-predicates"
                   [| "IS_IPV4"; "IS_IPV6"; "IS_IPV4_COMPAT"; "IS_IPV4_MAPPED" |]
                   "IS_IPV4('192.0.2.7'), IS_IPV6('2001:db8::7'), IS_IPV4_COMPAT(INET6_ATON('::192.0.2.7')), IS_IPV4_MAPPED(INET6_ATON('::ffff:192.0.2.7'))"
                   "IS_IPV4(?), IS_IPV6(?), IS_IPV4_COMPAT(INET6_ATON(?)), IS_IPV4_MAPPED(INET6_ATON(?))"
                   [| box "192.0.2.7"; box "2001:db8::7"; box "::192.0.2.7"; box "::ffff:192.0.2.7" |]
                   (Some([| "IS_IPV4" |], "IS_IPV4(?)", [| nullValue |]))
                   [| "IS_IPV4", "IS_IPV4()"
                      "IS_IPV6", "IS_IPV6()"
                      "IS_IPV4_COMPAT", "IS_IPV4_COMPAT()"
                      "IS_IPV4_MAPPED", "IS_IPV4_MAPPED()" |]
               spec
                   "json-value"
                   [| "JSON_VALUE" |]
                   "JSON_VALUE('{\"a\":7}', '$.a')"
                   "JSON_VALUE(JSON_EXTRACT(?, '$'), '$.a')"
                   [| box "{\"a\":7}" |]
                   (Some([| "JSON_VALUE" |], "JSON_VALUE(JSON_EXTRACT(?, '$'), '$.a')", [| nullValue |]))
                   [||]
               spec
                   "json-member-of"
                   [| "JSON_MEMBER_OF" |]
                   "2 MEMBER OF('[1,2]')"
                   "? MEMBER OF(?)"
                   [| box 2; box "[1,2]" |]
                   (Some([| "JSON_MEMBER_OF" |], "? MEMBER OF(?)", [| nullValue; box "[1,2]" |]))
                   [||]
               spec
                   "json-search"
                   [| "JSON_SEARCH" |]
                   "JSON_SEARCH('{\"a\":\"needle\"}', 'one', 'needle')"
                   "JSON_SEARCH(?, 'one', ?)"
                   [| box "{\"a\":\"needle\"}"; box "needle" |]
                   (Some([| "JSON_SEARCH" |], "JSON_SEARCH(?, 'one', ?)", [| nullValue; box "needle" |]))
                   [| "JSON_SEARCH", "JSON_SEARCH('{}', 'one')" |]
               spec
                   "calendar-names"
                   [| "DAYNAME"; "MONTHNAME" |]
                   "DAYNAME('2024-02-29'), MONTHNAME('2024-02-29')"
                   "DAYNAME(?), MONTHNAME(?)"
                   [| box "2024-02-29"; box "2024-02-29" |]
                   (Some([| "DAYNAME"; "MONTHNAME" |], "DAYNAME(?), MONTHNAME(?)", [| nullValue; nullValue |]))
                   [| "DAYNAME", "DAYNAME()"; "MONTHNAME", "MONTHNAME()" |]
               spec
                   "temporal-formats"
                   [| "GET_FORMAT" |]
                   "GET_FORMAT(DATE, 'EUR')"
                   "GET_FORMAT(DATE, ?)"
                   [| box "EUR" |]
                   (Some([| "GET_FORMAT" |], "GET_FORMAT(DATE, ?)", [| nullValue |]))
                   [||]
               spec
                   "period-arithmetic"
                   [| "PERIOD_ADD"; "PERIOD_DIFF" |]
                   "PERIOD_ADD(202312, 2), PERIOD_DIFF(202402, 202312)"
                   "PERIOD_ADD(?, ?), PERIOD_DIFF(?, ?)"
                   [| box 202312; box 2; box 202402; box 202312 |]
                   (Some([| "PERIOD_ADD" |], "PERIOD_ADD(?, ?)", [| nullValue; box 2 |]))
                   [| "PERIOD_ADD", "PERIOD_ADD(202312)"; "PERIOD_DIFF", "PERIOD_DIFF(202402)" |]
               shapeSpec
                   "cotangent"
                   [| "COT" |]
                   "ABS(COT(1) - 0.6420926159343306) < 0.000000000001"
                   "ABS(COT(?) - 0.6420926159343306) < 0.000000000001"
                   [| box 1 |]
                   (Some([| "COT" |], "COT(?)", [| nullValue |]))
                   [| "COT", "COT()" |]
               spec
                   "named-constant"
                   [| "NAME_CONST" |]
                   "NAME_CONST('answer', 42)"
                   "NAME_CONST('answer', 42) + ?"
                   [| box 0 |]
                   (Some([| "NAME_CONST" |], "NAME_CONST('answer', NULL)", [||]))
                   [| "NAME_CONST", "NAME_CONST('answer')" |]
               shapeSpec
                   "random-bytes-shape"
                   [| "RANDOM_BYTES" |]
                   "LENGTH(RANDOM_BYTES(8))"
                   "LENGTH(RANDOM_BYTES(?))"
                   [| box 8 |]
                   None
                   [| "RANDOM_BYTES", "RANDOM_BYTES()" |]
               shapeSpec
                   "random-shape"
                   [| "RAND" |]
                   "RAND(7) BETWEEN 0 AND 1"
                   "RAND(?) BETWEEN 0 AND 1"
                   [| box 7 |]
                   None
                   [| "RAND", "RAND(1, 2)" |]
               shapeSpec
                   "uuid-shape"
                   [| "UUID"; "UUID_SHORT" |]
                   "IS_UUID(UUID()), UUID_SHORT() > 0"
                   "IS_UUID(UUID()), UUID_SHORT() > ?"
                   [| box 0 |]
                   None
                   [| "UUID", "UUID(1)"; "UUID_SHORT", "UUID_SHORT(1)" |]
               shapeSpec
                   "current-temporal-aliases"
                   [| "CURDATE"; "CURRENT_DATE"; "CURTIME"; "CURRENT_TIME"; "NOW"; "CURRENT_TIMESTAMP"; "LOCALTIME"
                      "LOCALTIMESTAMP"; "UTC_DATE"; "UTC_TIME"; "UTC_TIMESTAMP"; "SYSDATE" |]
                   "CURDATE() = CURRENT_DATE(), CURTIME() = CURRENT_TIME(), NOW() = CURRENT_TIMESTAMP(), LOCALTIME() = LOCALTIMESTAMP(), UTC_DATE() = DATE(UTC_TIMESTAMP()), UTC_TIME() = TIME(UTC_TIMESTAMP()), SYSDATE() IS NOT NULL"
                   "CURDATE() = CURRENT_DATE(), CURTIME() = CURRENT_TIME(), NOW() = CURRENT_TIMESTAMP(), LOCALTIME() = LOCALTIMESTAMP(), UTC_DATE() = DATE(UTC_TIMESTAMP()), UTC_TIME() = TIME(UTC_TIMESTAMP()), SYSDATE() IS NOT NULL"
                   [||]
                   None
                   [||] |]

        let numericSpecs =
            [| spec
                   "numeric-unary"
                   [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "SQRT"; "SIGN" |]
                   "ABS(-2), CEIL(1.2), CEILING(1.2), FLOOR(1.8), SQRT(4), SIGN(-2)"
                   "ABS(?), CEIL(?), CEILING(?), FLOOR(?), SQRT(?), SIGN(?)"
                   [| box -2; box 1.2M; box 1.2M; box 1.8M; box 4; box -2 |]
                   (Some(
                       [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "SQRT"; "SIGN" |],
                       "ABS(?), CEIL(?), CEILING(?), FLOOR(?), SQRT(?), SIGN(?)",
                       Array.create 6 nullValue
                   ))
                   [| "ABS", "ABS()"
                      "CEIL", "CEIL()"
                      "CEILING", "CEILING()"
                      "FLOOR", "FLOOR()"
                      "SQRT", "SQRT()"
                      "SIGN", "SIGN()" |]
               spec
                   "numeric-transcendental"
                   [| "LOG"; "LN"; "LOG2"; "LOG10"; "EXP"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"
                      "ATAN2"; "DEGREES"; "RADIANS" |]
                   "LOG(1), LN(1), LOG2(8), LOG10(100), EXP(0), SIN(0), COS(0), TAN(0), ASIN(0), ACOS(1), ATAN(0), ATAN2(0, 1), DEGREES(0), RADIANS(0)"
                   "LOG(?), LN(?), LOG2(?), LOG10(?), EXP(?), SIN(?), COS(?), TAN(?), ASIN(?), ACOS(?), ATAN(?), ATAN2(?, ?), DEGREES(?), RADIANS(?)"
                   [| box 1
                      box 1
                      box 8
                      box 100
                      box 0
                      box 0
                      box 0
                      box 0
                      box 0
                      box 1
                      box 0
                      box 0
                      box 1
                      box 0
                      box 0 |]
                   (Some(
                       [| "LOG"; "LN"; "LOG2"; "LOG10"; "EXP"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"
                          "ATAN2"; "DEGREES"; "RADIANS" |],
                       "LOG(?), LN(?), LOG2(?), LOG10(?), EXP(?), SIN(?), COS(?), TAN(?), ASIN(?), ACOS(?), ATAN(?), ATAN2(?, ?), DEGREES(?), RADIANS(?)",
                       Array.create 15 nullValue
                   ))
                   [| "LN", "LN()"
                      "LOG2", "LOG2()"
                      "LOG10", "LOG10()"
                      "EXP", "EXP()"
                      "SIN", "SIN()"
                      "COS", "COS()"
                      "TAN", "TAN()"
                      "ASIN", "ASIN()"
                      "ACOS", "ACOS()"
                      "ATAN", "ATAN()"
                      "ATAN2", "ATAN2()"
                      "DEGREES", "DEGREES()"
                      "RADIANS", "RADIANS()" |]
               spec
                   "numeric-rounding-and-power"
                   [| "POW"; "POWER"; "ROUND"; "TRUNCATE"; "MOD" |]
                   "POW(2, 3), POWER(2, 3), ROUND(1.25, 1), TRUNCATE(1.29, 1), MOD(7, 3)"
                   "POW(2, ?), POWER(2, ?), ROUND(1.25, ?), TRUNCATE(1.29, ?), MOD(7, ?)"
                   [| box 3; box 3; box 1; box 1; box 3 |]
                   (Some(
                       [| "POW"; "POWER"; "ROUND"; "TRUNCATE"; "MOD" |],
                       "POW(?, ?), POWER(?, ?), ROUND(?, ?), TRUNCATE(?, ?), MOD(?, ?)",
                       Array.create 10 nullValue
                   ))
                   [| "POW", "POW(2)"
                      "POWER", "POWER(2)"
                      "ROUND", "ROUND()" |]
               spec
                   "numeric-selection"
                   [| "GREATEST"; "LEAST"; "NULLIF" |]
                   "GREATEST(1, 3, 2), LEAST(1, 3, 2), NULLIF(1, 1)"
                   "GREATEST(?, ?, ?), LEAST(?, ?, ?), NULLIF(?, ?)"
                   [| box 1; box 3; box 2; box 1; box 3; box 2; box 1; box 1 |]
                   (Some(
                       [| "GREATEST"; "LEAST"; "NULLIF" |],
                       "GREATEST(?, 2), LEAST(?, 2), NULLIF(?, 1)",
                       Array.create 3 nullValue
                   ))
                   [| "GREATEST", "GREATEST()"; "LEAST", "LEAST()" |]
               spec
                   "numeric-null-predicate"
                   [| "ISNULL" |]
                   "ISNULL(NULL)"
                   "ISNULL(?)"
                   [| box 1 |]
                   (Some([| "ISNULL" |], "ISNULL(?)", [| nullValue |]))
                   [| "ISNULL", "ISNULL()" |]
               spec
                   "numeric-base-and-bits"
                   [| "CONV"; "BIN"; "BIT_COUNT"; "OCT"; "CRC32" |]
                   "CONV('ff', 16, 10), BIN(5), BIT_COUNT(7), OCT(8), CRC32('abc')"
                   "CONV(?, ?, ?), BIN(?), BIT_COUNT(?), OCT(?), CRC32(?)"
                   [| box "ff"; box 16; box 10; box 5; box 7; box 8; box "abc" |]
                   (Some(
                       [| "CONV"; "BIN"; "BIT_COUNT"; "OCT"; "CRC32" |],
                       "CONV(?, 16, 10), BIN(?), BIT_COUNT(?), OCT(?), CRC32(?)",
                       Array.create 5 nullValue
                   ))
                   [| "CONV", "CONV('ff', 16)"
                      "BIN", "BIN()"
                      "BIT_COUNT", "BIT_COUNT()"
                      "OCT", "OCT()"
                      "CRC32", "CRC32()" |]
               spec
                   "cotangent-null-result"
                   [| "COT" |]
                   "COT(NULL)"
                   "COT(?)"
                   [| nullValue |]
                   (Some([| "COT" |], "COT(?)", [| nullValue |]))
                   [||] |]

        let stringSpecs =
            [| spec
                   "string-case-and-length"
                   [| "UPPER"; "LOWER"; "LENGTH"; "CHAR_LENGTH" |]
                   "UPPER('Abc'), LOWER('AbC'), LENGTH('blå'), CHAR_LENGTH('blå')"
                   "UPPER(?), LOWER(?), LENGTH(?), CHAR_LENGTH(?)"
                   [| box "Abc"; box "AbC"; box "blå"; box "blå" |]
                   (Some(
                       [| "UPPER"; "LOWER"; "LENGTH"; "CHAR_LENGTH" |],
                       "UPPER(?), LOWER(?), LENGTH(?), CHAR_LENGTH(?)",
                       [| nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "UPPER", "UPPER()"
                      "LOWER", "LOWER()"
                      "LENGTH", "LENGTH()"
                      "CHAR_LENGTH", "CHAR_LENGTH()" |]
               spec
                   "string-byte-functions"
                   [| "ASCII"; "ORD"; "HEX"; "UNHEX"; "MD5"; "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64" |]
                   "ASCII('A'), ORD('Å'), HEX('A'), UNHEX('41'), MD5('abc'), SHA1('abc'), SHA2('abc', 256), TO_BASE64('abc'), FROM_BASE64('YWJj')"
                   "ASCII(?), ORD(?), HEX(?), UNHEX(?), MD5(?), SHA1(?), SHA2(?, ?), TO_BASE64(?), FROM_BASE64(?)"
                   [| box "A"
                      box "Å"
                      box "A"
                      box "41"
                      box "abc"
                      box "abc"
                      box "abc"
                      box 256
                      box "abc"
                      box "YWJj" |]
                   (Some(
                       [| "ASCII"; "ORD"; "HEX"; "UNHEX"; "MD5"; "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64" |],
                       "ASCII(?), ORD(?), HEX(?), UNHEX(?), MD5(?), SHA1(?), SHA2(?, 256), TO_BASE64(?), FROM_BASE64(?)",
                       [| nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue |]
                   ))
                   [| "ORD", "ORD()"
                      "HEX", "HEX()"
                      "UNHEX", "UNHEX()"
                      "MD5", "MD5()"
                      "SHA1", "SHA1()"
                      "SHA2", "SHA2('abc')"
                      "TO_BASE64", "TO_BASE64()"
                      "FROM_BASE64", "FROM_BASE64()" |]
               spec
                   "string-slicing-and-search"
                   [| "LEFT"; "RIGHT"; "SUBSTRING"; "INSTR"; "LOCATE"; "REPLACE"; "SUBSTRING_INDEX" |]
                   "LEFT('abcd', 2), RIGHT('abcd', 2), SUBSTRING('abcd', 2, 2), INSTR('abcd', 'bc'), LOCATE('bc', 'abcd'), REPLACE('abcd', 'bc', 'X'), SUBSTRING_INDEX('a,b,c', ',', 2)"
                   "LEFT(?, ?), RIGHT(?, ?), SUBSTRING(?, ?, ?), INSTR(?, ?), LOCATE(?, ?), REPLACE(?, ?, ?), SUBSTRING_INDEX(?, ?, ?)"
                   [| box "abcd"
                      box 2
                      box "abcd"
                      box 2
                      box "abcd"
                      box 2
                      box 2
                      box "abcd"
                      box "bc"
                      box "bc"
                      box "abcd"
                      box "abcd"
                      box "bc"
                      box "X"
                      box "a,b,c"
                      box ","
                      box 2 |]
                   (Some(
                       [| "LEFT"; "RIGHT"; "SUBSTRING"; "INSTR"; "LOCATE"; "REPLACE"; "SUBSTRING_INDEX" |],
                       "LEFT(?, 2), RIGHT(?, 2), SUBSTRING(?, 2, 2), INSTR(?, 'b'), LOCATE('b', ?), REPLACE(?, 'b', 'x'), SUBSTRING_INDEX(?, ',', 2)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "INSTR", "INSTR('abc')"
                      "LOCATE", "LOCATE('a')"
                      "SUBSTRING_INDEX", "SUBSTRING_INDEX('a,b', ',')" |]
               spec
                   "string-padding-and-repeat"
                   [| "REVERSE"; "REPEAT"; "SPACE"; "LPAD"; "RPAD"; "LTRIM"; "RTRIM" |]
                   "REVERSE('abc'), REPEAT('ab', 2), SPACE(3), LPAD('a', 3, '0'), RPAD('a', 3, '0'), LTRIM('  a'), RTRIM('a  ')"
                   "REVERSE(?), REPEAT(?, ?), SPACE(?), LPAD(?, ?, ?), RPAD(?, ?, ?), LTRIM(?), RTRIM(?)"
                   [| box "abc"
                      box "ab"
                      box 2
                      box 3
                      box "a"
                      box 3
                      box "0"
                      box "a"
                      box 3
                      box "0"
                      box "  a"
                      box "a  " |]
                   (Some(
                       [| "REVERSE"; "REPEAT"; "SPACE"; "LPAD"; "RPAD"; "LTRIM"; "RTRIM" |],
                       "REVERSE(?), REPEAT(?, 2), SPACE(?), LPAD(?, 3, '0'), RPAD(?, 3, '0'), LTRIM(?), RTRIM(?)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "SPACE", "SPACE()"
                      "LPAD", "LPAD('a', 3)"
                      "RPAD", "RPAD('a', 3)"
                      "LTRIM", "LTRIM()"
                      "RTRIM", "RTRIM()" |]
               spec
                   "concat-null-contracts"
                   [| "CONCAT" |]
                   "CONCAT('a', 'b')"
                   "CONCAT(?, ?)"
                   [| box "a"; box "b" |]
                   (Some([| "CONCAT" |], "CONCAT(?, 'b')", [| nullValue |]))
                   [| "CONCAT", "CONCAT()" |]
               spec
                   "concat-ws-null-contracts"
                   [| "CONCAT_WS" |]
                   "CONCAT_WS('-', 'a', 'b')"
                   "CONCAT_WS(?, ?, ?)"
                   [| box "-"; box "a"; box "b" |]
                   (Some([| "CONCAT_WS" |], "CONCAT_WS('-', ?, 'b')", [| nullValue |]))
                   [| "CONCAT_WS", "CONCAT_WS('-')" |] |]

        let jsonSpecs =
            [| spec
                   "json-inspection"
                   [| "JSON_TYPE"; "JSON_VALID"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE" |]
                   "JSON_TYPE('{\"a\":[1,2]}'), JSON_VALID('{\"a\":1}'), JSON_DEPTH('{\"a\":[1,2]}'), JSON_LENGTH('{\"a\":[1,2]}'), JSON_STORAGE_SIZE('{\"a\":1}'), JSON_STORAGE_FREE('{\"a\":1}')"
                   "JSON_TYPE(?), JSON_VALID(?), JSON_DEPTH(?), JSON_LENGTH(?), JSON_STORAGE_SIZE(?), JSON_STORAGE_FREE(?)"
                   [| box "{\"a\":[1,2]}"
                      box "{\"a\":1}"
                      box "{\"a\":[1,2]}"
                      box "{\"a\":[1,2]}"
                      box "{\"a\":1}"
                      box "{\"a\":1}" |]
                   (Some(
                       [| "JSON_TYPE"; "JSON_VALID"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE" |],
                       "JSON_TYPE(?), JSON_VALID(?), JSON_DEPTH(?), JSON_LENGTH(?), JSON_STORAGE_SIZE(?), JSON_STORAGE_FREE(?)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "JSON_TYPE", "JSON_TYPE()"
                      "JSON_VALID", "JSON_VALID()"
                      "JSON_DEPTH", "JSON_DEPTH()"
                      "JSON_LENGTH", "JSON_LENGTH()"
                      "JSON_STORAGE_SIZE", "JSON_STORAGE_SIZE()"
                      "JSON_STORAGE_FREE", "JSON_STORAGE_FREE()" |]
               spec
                   "json-text-results"
                   [| "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_PRETTY"; "JSON_KEYS" |]
                   "JSON_UNQUOTE('\"hello\"'), JSON_QUOTE('hello'), JSON_PRETTY('{\"a\":1}'), JSON_KEYS('{\"a\":1}')"
                   "JSON_UNQUOTE(?), JSON_QUOTE(?), JSON_PRETTY(?), JSON_KEYS(?)"
                   [| box "\"hello\""; box "hello"; box "{\"a\":1}"; box "{\"a\":1}" |]
                   (Some(
                       [| "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_PRETTY"; "JSON_KEYS" |],
                       "JSON_UNQUOTE(?), JSON_QUOTE(?), JSON_PRETTY(?), JSON_KEYS(?)",
                       [| nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "JSON_UNQUOTE", "JSON_UNQUOTE()"
                      "JSON_QUOTE", "JSON_QUOTE()"
                      "JSON_PRETTY", "JSON_PRETTY()"
                      "JSON_KEYS", "JSON_KEYS()" |]
               spec
                   "json-predicates"
                   [| "JSON_CONTAINS"; "JSON_OVERLAPS" |]
                   "JSON_CONTAINS('[1,2]', '2'), JSON_OVERLAPS('[1,2]', '[2,3]')"
                   "JSON_CONTAINS(?, ?), JSON_OVERLAPS(?, ?)"
                   [| box "[1,2]"; box "2"; box "[1,2]"; box "[2,3]" |]
                   (Some(
                       [| "JSON_CONTAINS"; "JSON_OVERLAPS" |],
                       "JSON_CONTAINS(?, '2'), JSON_OVERLAPS(?, '[2,3]')",
                       [| nullValue; nullValue |]
                   ))
                   [| "JSON_CONTAINS", "JSON_CONTAINS('[1,2]')"; "JSON_OVERLAPS", "JSON_OVERLAPS('[1,2]')" |]
               spec
                   "json-schema-validation"
                   [| "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |]
                   "JSON_SCHEMA_VALID('{\"type\":\"integer\"}', '7'), JSON_SCHEMA_VALIDATION_REPORT('{\"type\":\"integer\"}', '7')"
                   "JSON_SCHEMA_VALID(JSON_EXTRACT(?, '$'), JSON_EXTRACT(?, '$')), JSON_SCHEMA_VALIDATION_REPORT(JSON_EXTRACT(?, '$'), JSON_EXTRACT(?, '$'))"
                   [| box "{\"type\":\"integer\"}"
                      box "7"
                      box "{\"type\":\"integer\"}"
                      box "7" |]
                   (Some(
                       [| "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |],
                       "JSON_SCHEMA_VALID(JSON_EXTRACT(?, '$'), JSON_EXTRACT('7', '$')), JSON_SCHEMA_VALIDATION_REPORT(JSON_EXTRACT(?, '$'), JSON_EXTRACT('7', '$'))",
                       [| nullValue; nullValue |]
                   ))
                   [| "JSON_SCHEMA_VALID", "JSON_SCHEMA_VALID('{}')"
                      "JSON_SCHEMA_VALIDATION_REPORT", "JSON_SCHEMA_VALIDATION_REPORT('{}')" |] |]

        let temporalSpecs =
            [| spec
                   "temporal-components"
                   [| "DATE"
                      "TIME"
                      "YEAR"
                      "MONTH"
                      "DAY"
                      "DAYOFMONTH"
                      "HOUR"
                      "MINUTE"
                      "SECOND"
                      "MICROSECOND" |]
                   "DATE('2024-02-29 12:34:56.123456'), TIME('2024-02-29 12:34:56.123456'), YEAR('2024-02-29'), MONTH('2024-02-29'), DAY('2024-02-29'), DAYOFMONTH('2024-02-29'), HOUR('12:34:56.123456'), MINUTE('12:34:56.123456'), SECOND('12:34:56.123456'), MICROSECOND('12:34:56.123456')"
                   "DATE(?), TIME(?), YEAR(?), MONTH(?), DAY(?), DAYOFMONTH(?), HOUR(?), MINUTE(?), SECOND(?), MICROSECOND(?)"
                   [| box "2024-02-29 12:34:56.123456"
                      box "2024-02-29 12:34:56.123456"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "12:34:56.123456"
                      box "12:34:56.123456"
                      box "12:34:56.123456"
                      box "12:34:56.123456" |]
                   (Some(
                       [| "DATE"
                          "TIME"
                          "YEAR"
                          "MONTH"
                          "DAY"
                          "DAYOFMONTH"
                          "HOUR"
                          "MINUTE"
                          "SECOND"
                          "MICROSECOND" |],
                       "DATE(?), TIME(?), YEAR(?), MONTH(?), DAY(?), DAYOFMONTH(?), HOUR(?), MINUTE(?), SECOND(?), MICROSECOND(?)",
                       Array.create 10 nullValue
                   ))
                   [| "DAYOFMONTH", "DAYOFMONTH()" |]
               spec
                   "temporal-calendar"
                   [| "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"; "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "LAST_DAY" |]
                   "DAYOFWEEK('2024-02-29'), DAYOFYEAR('2024-02-29'), WEEKDAY('2024-02-29'), WEEK('2024-02-29', 3), WEEKOFYEAR('2024-02-29'), YEARWEEK('2024-02-29', 3), QUARTER('2024-02-29'), LAST_DAY('2024-02-10')"
                   "DAYOFWEEK(?), DAYOFYEAR(?), WEEKDAY(?), WEEK(?, ?), WEEKOFYEAR(?), YEARWEEK(?, ?), QUARTER(?), LAST_DAY(?)"
                   [| box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box 3
                      box "2024-02-29"
                      box "2024-02-29"
                      box 3
                      box "2024-02-29"
                      box "2024-02-10" |]
                   (Some(
                       [| "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"; "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "LAST_DAY" |],
                       "DAYOFWEEK(?), DAYOFYEAR(?), WEEKDAY(?), WEEK(?, 3), WEEKOFYEAR(?), YEARWEEK(?, 3), QUARTER(?), LAST_DAY(?)",
                       Array.create 8 nullValue
                   ))
                   [| "DAYOFWEEK", "DAYOFWEEK()"
                      "DAYOFYEAR", "DAYOFYEAR()"
                      "WEEKDAY", "WEEKDAY()"
                      "WEEKOFYEAR", "WEEKOFYEAR()"
                      "YEARWEEK", "YEARWEEK()"
                      "LAST_DAY", "LAST_DAY()" |]
               spec
                   "temporal-arithmetic"
                   [| "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME" |]
                   "DATEDIFF('2024-03-02', '2024-02-29'), TIMEDIFF('12:00:01.5', '10:00:00'), ADDTIME('10:00:00', '01:02:03'), SUBTIME('10:00:00', '01:02:03'), MAKEDATE(2024, 60), MAKETIME(12, 34, 56.5), SEC_TO_TIME(3661.5)"
                   "DATEDIFF(?, ?), TIMEDIFF(?, ?), ADDTIME(CAST(? AS TIME(6)), ?), SUBTIME(CAST(? AS TIME(6)), ?), MAKEDATE(?, ?), MAKETIME(?, ?, ?), SEC_TO_TIME(?)"
                   [| box "2024-03-02"
                      box "2024-02-29"
                      box "12:00:01.5"
                      box "10:00:00"
                      box (TimeSpan(10, 0, 0))
                      box (TimeSpan(1, 2, 3))
                      box (TimeSpan(10, 0, 0))
                      box (TimeSpan(1, 2, 3))
                      box 2024
                      box 60
                      box 12
                      box 34
                      box 56.5
                      box 3661.5 |]
                   (Some(
                       [| "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME" |],
                       "DATEDIFF(?, '2024-02-29'), TIMEDIFF(?, '10:00:00'), ADDTIME(CAST(? AS TIME), '01:02:03'), SUBTIME(CAST(? AS TIME), '01:02:03'), MAKEDATE(?, 60), MAKETIME(?, 34, 56), SEC_TO_TIME(?)",
                       Array.create 7 nullValue
                   ))
                   [| "DATEDIFF", "DATEDIFF('2024-03-02')"
                      "TIMEDIFF", "TIMEDIFF('12:00:01')"
                      "ADDTIME", "ADDTIME('10:00:00')"
                      "SUBTIME", "SUBTIME('10:00:00')"
                      "MAKEDATE", "MAKEDATE(2024)"
                      "MAKETIME", "MAKETIME(12, 34)"
                      "SEC_TO_TIME", "SEC_TO_TIME()" |]
               spec
                   "temporal-conversion"
                   [| "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"; "FROM_UNIXTIME"; "TIME_FORMAT"; "STR_TO_DATE"; "CONVERT_TZ" |]
                   "TO_DAYS('2024-02-29'), FROM_DAYS(739310), UNIX_TIMESTAMP('2024-02-29 12:00:00'), FROM_UNIXTIME(1709208000), TIME_FORMAT('12:34:56.123456', '%H:%i:%s.%f'), STR_TO_DATE('2024-02-29', '%Y-%m-%d'), CONVERT_TZ('2024-02-29 12:00:00', '+00:00', '+02:00')"
                   "TO_DAYS(?), FROM_DAYS(?), UNIX_TIMESTAMP(CAST(? AS DATETIME)), FROM_UNIXTIME(?), TIME_FORMAT(?, ?), STR_TO_DATE(?, '%Y-%m-%d'), CONVERT_TZ(?, ?, ?)"
                   [| box "2024-02-29"
                      box 739310
                      box "2024-02-29 12:00:00"
                      box 1709208000
                      box "12:34:56.123456"
                      box "%H:%i:%s.%f"
                      box "2024-02-29"
                      box "2024-02-29 12:00:00"
                      box "+00:00"
                      box "+02:00" |]
                   (Some(
                       [| "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"; "FROM_UNIXTIME"; "TIME_FORMAT"; "STR_TO_DATE"; "CONVERT_TZ" |],
                       "TO_DAYS(?), FROM_DAYS(?), UNIX_TIMESTAMP(CAST(? AS DATETIME)), FROM_UNIXTIME(?), TIME_FORMAT(?, '%H:%i:%s'), STR_TO_DATE(?, '%Y-%m-%d'), CONVERT_TZ(?, '+00:00', '+02:00')",
                       Array.create 7 nullValue
                   ))
                   [| "TO_DAYS", "TO_DAYS()"
                      "FROM_DAYS", "FROM_DAYS()"
                      "UNIX_TIMESTAMP", "UNIX_TIMESTAMP(1, 2)"
                      "FROM_UNIXTIME", "FROM_UNIXTIME()"
                      "TIME_FORMAT", "TIME_FORMAT('12:34:56')"
                      "STR_TO_DATE", "STR_TO_DATE('2024-02-29')"
                      "CONVERT_TZ", "CONVERT_TZ('2024-02-29', '+00:00')" |] |]

        let spatialSpecs =
            [| spec
                   "spatial-construction-and-serialization"
                   [| "ST_GEOMFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKB" |]
                   "ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_POINTFROMTEXT('POINT(1 2)'), ST_ASTEXT(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_ASWKB(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?), ST_ASTEXT(ST_GEOMFROMTEXT(?)), ST_ASWKB(ST_POINTFROMTEXT(?))"
                   [| box "LINESTRING(0 0, 2 2)"; box "POINT(1 2)"; box "LINESTRING(0 0, 2 2)"; box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKB" |],
                       "ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?), ST_ASTEXT(?), ST_ASWKB(?)",
                       Array.create 4 nullValue
                   ))
                   [| "ST_GEOMFROMTEXT", "ST_GEOMFROMTEXT()"
                      "ST_POINTFROMTEXT", "ST_POINTFROMTEXT()"
                      "ST_ASTEXT", "ST_ASTEXT()"
                      "ST_ASWKB", "ST_ASWKB()" |]
               spec
                   "spatial-inspection"
                   [| "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_SRID"; "ST_X"; "ST_Y" |]
                   "ST_GEOMETRYTYPE(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_DIMENSION(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_ISEMPTY(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION EMPTY')), ST_ISVALID(ST_GEOMFROMTEXT('POLYGON((0 0, 2 0, 2 2, 0 0))')), ST_SRID(ST_GEOMFROMTEXT('POINT(1 2)')), ST_X(ST_POINTFROMTEXT('POINT(1 2)')), ST_Y(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMETRYTYPE(ST_GEOMFROMTEXT(?)), ST_DIMENSION(ST_GEOMFROMTEXT(?)), ST_ISEMPTY(ST_GEOMFROMTEXT(?)), ST_ISVALID(ST_GEOMFROMTEXT(?)), ST_SRID(ST_GEOMFROMTEXT(?)), ST_X(ST_POINTFROMTEXT(?)), ST_Y(ST_POINTFROMTEXT(?))"
                   [| box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 0, 2 2)"
                      box "GEOMETRYCOLLECTION EMPTY"
                      box "POLYGON((0 0, 2 0, 2 2, 0 0))"
                      box "POINT(1 2)"
                      box "POINT(1 2)"
                      box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_SRID"; "ST_X"; "ST_Y" |],
                       "ST_GEOMETRYTYPE(?), ST_DIMENSION(?), ST_ISEMPTY(?), ST_ISVALID(?), ST_SRID(?), ST_X(?), ST_Y(?)",
                       Array.create 7 nullValue
                   ))
                   [| "ST_GEOMETRYTYPE", "ST_GEOMETRYTYPE()"
                      "ST_DIMENSION", "ST_DIMENSION()"
                      "ST_ISEMPTY", "ST_ISEMPTY()"
                      "ST_ISVALID", "ST_ISVALID()"
                      "ST_SRID", "ST_SRID()"
                      "ST_X", "ST_X()"
                      "ST_Y", "ST_Y()" |]
               spec
                   "spatial-property-accessors"
                   [| "ST_ISCLOSED"
                      "ST_NUMPOINTS"
                      "ST_STARTPOINT"
                      "ST_ENDPOINT"
                      "ST_POINTN"
                      "ST_NUMINTERIORRING"
                      "ST_NUMINTERIORRINGS"
                      "ST_EXTERIORRING"
                      "ST_INTERIORRINGN"
                      "ST_NUMGEOMETRIES"
                      "ST_GEOMETRYN" |]
                   "ST_ISCLOSED(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,0 0)')), ST_NUMPOINTS(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)')), ST_ASTEXT(ST_STARTPOINT(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'))), ST_ASTEXT(ST_ENDPOINT(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'))), ST_ASTEXT(ST_POINTN(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'), 2)), ST_NUMINTERIORRING(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))')), ST_NUMINTERIORRINGS(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))')), ST_ASTEXT(ST_EXTERIORRING(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))'))), ST_ASTEXT(ST_INTERIORRINGN(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))'), 1)), ST_NUMGEOMETRIES(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))')), ST_ASTEXT(ST_GEOMETRYN(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))'), 2))"
                   "ST_ISCLOSED(ST_GEOMFROMTEXT(?)), ST_NUMPOINTS(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_STARTPOINT(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_ENDPOINT(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_POINTN(ST_GEOMFROMTEXT(?), ?)), ST_NUMINTERIORRINGS(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_EXTERIORRING(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_INTERIORRINGN(ST_GEOMFROMTEXT(?), ?)), ST_NUMGEOMETRIES(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_GEOMETRYN(ST_GEOMFROMTEXT(?), ?))"
                   [| box "LINESTRING(0 0,1 1,0 0)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box 2
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box 1
                      box "MULTIPOINT((1 2),(3 4))"
                      box "MULTIPOINT((1 2),(3 4))"
                      box 2 |]
                   (Some(
                       [| "ST_ISCLOSED"
                          "ST_NUMPOINTS"
                          "ST_STARTPOINT"
                          "ST_ENDPOINT"
                          "ST_POINTN"
                          "ST_NUMINTERIORRINGS"
                          "ST_EXTERIORRING"
                          "ST_INTERIORRINGN"
                          "ST_NUMGEOMETRIES"
                          "ST_GEOMETRYN" |],
                       "ST_ISCLOSED(?), ST_NUMPOINTS(?), ST_STARTPOINT(?), ST_ENDPOINT(?), ST_POINTN(?, 1), ST_NUMINTERIORRINGS(?), ST_EXTERIORRING(?), ST_INTERIORRINGN(?, 1), ST_NUMGEOMETRIES(?), ST_GEOMETRYN(?, 1)",
                       Array.create 10 nullValue
                   ))
                   [| "ST_ISCLOSED", "ST_ISCLOSED()"
                      "ST_NUMPOINTS", "ST_NUMPOINTS()"
                      "ST_STARTPOINT", "ST_STARTPOINT()"
                      "ST_ENDPOINT", "ST_ENDPOINT()"
                      "ST_POINTN", "ST_POINTN(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1)'))"
                      "ST_NUMINTERIORRING", "ST_NUMINTERIORRING()"
                      "ST_NUMINTERIORRINGS", "ST_NUMINTERIORRINGS()"
                      "ST_EXTERIORRING", "ST_EXTERIORRING()"
                      "ST_INTERIORRINGN", "ST_INTERIORRINGN(ST_GEOMFROMTEXT('POLYGON((0 0,1 0,1 1,0 0))'))"
                      "ST_NUMGEOMETRIES", "ST_NUMGEOMETRIES()"
                      "ST_GEOMETRYN", "ST_GEOMETRYN(ST_GEOMFROMTEXT('MULTIPOINT((1 2))'))" |]
               spec
                   "spatial-relations"
                   [| "ST_DISTANCE"
                      "ST_EQUALS"
                      "ST_CONTAINS"
                      "ST_WITHIN"
                      "ST_INTERSECTS"
                      "ST_DISJOINT"
                      "ST_TOUCHES"
                      "MBRCONTAINS"
                      "MBRWITHIN"
                      "MBRINTERSECTS" |]
                   "ST_DISTANCE(ST_POINTFROMTEXT('POINT(0 0)'), ST_POINTFROMTEXT('POINT(3 4)')), ST_EQUALS(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_CONTAINS(ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))'), ST_POINTFROMTEXT('POINT(2 2)')), ST_WITHIN(ST_POINTFROMTEXT('POINT(2 2)'), ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), ST_INTERSECTS(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)')), ST_DISJOINT(ST_POINTFROMTEXT('POINT(0 0)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_TOUCHES(ST_GEOMFROMTEXT('LINESTRING(0 0, 1 0)'), ST_GEOMFROMTEXT('LINESTRING(1 0, 2 0)')), MBRCONTAINS(ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))'), ST_POINTFROMTEXT('POINT(2 2)')), MBRWITHIN(ST_POINTFROMTEXT('POINT(2 2)'), ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), MBRINTERSECTS(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)'))"
                   "ST_DISTANCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_EQUALS(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_CONTAINS(ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_WITHIN(ST_POINTFROMTEXT(?), ST_GEOMFROMTEXT(?)), ST_INTERSECTS(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?)), ST_DISJOINT(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_TOUCHES(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?)), MBRCONTAINS(ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?)), MBRWITHIN(ST_POINTFROMTEXT(?), ST_GEOMFROMTEXT(?)), MBRINTERSECTS(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?))"
                   [| box "POINT(0 0)"
                      box "POINT(3 4)"
                      box "POINT(1 1)"
                      box "POINT(1 1)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "POINT(2 2)"
                      box "POINT(2 2)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 2, 2 0)"
                      box "POINT(0 0)"
                      box "POINT(1 1)"
                      box "LINESTRING(0 0, 1 0)"
                      box "LINESTRING(1 0, 2 0)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "POINT(2 2)"
                      box "POINT(2 2)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 2, 2 0)" |]
                   (Some(
                       [| "ST_DISTANCE"
                          "ST_EQUALS"
                          "ST_CONTAINS"
                          "ST_WITHIN"
                          "ST_INTERSECTS"
                          "ST_DISJOINT"
                          "ST_TOUCHES"
                          "MBRCONTAINS"
                          "MBRWITHIN"
                          "MBRINTERSECTS" |],
                       "ST_DISTANCE(?, ST_POINTFROMTEXT('POINT(0 0)')), ST_EQUALS(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_CONTAINS(?, ST_POINTFROMTEXT('POINT(2 2)')), ST_WITHIN(?, ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), ST_INTERSECTS(?, ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)')), ST_DISJOINT(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_TOUCHES(?, ST_GEOMFROMTEXT('LINESTRING(1 0, 2 0)')), MBRCONTAINS(?, ST_POINTFROMTEXT('POINT(2 2)')), MBRWITHIN(?, ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), MBRINTERSECTS(?, ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)'))",
                       Array.create 10 nullValue
                   ))
                   [| "ST_DISTANCE", "ST_DISTANCE(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_EQUALS", "ST_EQUALS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_CONTAINS", "ST_CONTAINS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_WITHIN", "ST_WITHIN(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_INTERSECTS", "ST_INTERSECTS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_DISJOINT", "ST_DISJOINT(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_TOUCHES", "ST_TOUCHES(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRCONTAINS", "MBRCONTAINS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRWITHIN", "MBRWITHIN(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRINTERSECTS", "MBRINTERSECTS(ST_POINTFROMTEXT('POINT(0 0)'))" |] |]

        let spatialResultSpecs =
            [| spec
                   "spatial-overlay-results"
                   [| "ST_INTERSECTION"; "ST_UNION"; "ST_DIFFERENCE"; "ST_SYMDIFFERENCE"; "ST_CONVEXHULL"; "ST_ENVELOPE" |]
                   "ST_INTERSECTION(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_UNION(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_DIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_SYMDIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_CONVEXHULL(ST_POINTFROMTEXT('POINT(1 1)')), ST_ENVELOPE(ST_POINTFROMTEXT('POINT(1 1)'))"
                   "ST_INTERSECTION(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_UNION(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_DIFFERENCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_SYMDIFFERENCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_CONVEXHULL(ST_POINTFROMTEXT(?)), ST_ENVELOPE(ST_POINTFROMTEXT(?))"
                   (Array.create 10 (box "POINT(1 1)"))
                   (Some(
                       [| "ST_INTERSECTION"; "ST_UNION"; "ST_DIFFERENCE"; "ST_SYMDIFFERENCE"; "ST_CONVEXHULL"; "ST_ENVELOPE" |],
                       "ST_INTERSECTION(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_UNION(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_DIFFERENCE(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_SYMDIFFERENCE(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_CONVEXHULL(?), ST_ENVELOPE(?)",
                       Array.create 6 nullValue
                   ))
                   [| "ST_INTERSECTION", "ST_INTERSECTION(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_UNION", "ST_UNION(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_DIFFERENCE", "ST_DIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_SYMDIFFERENCE", "ST_SYMDIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_CONVEXHULL", "ST_CONVEXHULL()"
                      "ST_ENVELOPE", "ST_ENVELOPE()" |]
               shapeSpec
                   "spatial-buffer-shapes"
                   [| "ST_BUFFER"; "ST_BUFFER_STRATEGY" |]
                   "ST_CONTAINS(ST_BUFFER(ST_POINTFROMTEXT('POINT(0 0)'), 2), ST_POINTFROMTEXT('POINT(0 0)')), LENGTH(ST_BUFFER_STRATEGY('point_square')) > 0"
                   "ST_CONTAINS(ST_BUFFER(ST_POINTFROMTEXT(?), ?), ST_POINTFROMTEXT(?)), LENGTH(ST_BUFFER_STRATEGY(?)) > 0"
                   [| box "POINT(0 0)"; box 2; box "POINT(0 0)"; box "point_square" |]
                   (Some(
                       [| "ST_BUFFER"; "ST_BUFFER_STRATEGY" |],
                       "ST_BUFFER(?, 2), ST_BUFFER_STRATEGY(?)",
                       [| nullValue; nullValue |]
                   ))
                   [| "ST_BUFFER", "ST_BUFFER(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_BUFFER_STRATEGY", "ST_BUFFER_STRATEGY()" |] |]

        let temporalSyntaxSpecs =
            [| spec
                   "temporal-syntax-functions"
                   [| "DATE_ADD"; "DATE_SUB"; "TIMESTAMP"; "TIMESTAMPADD"; "TIMESTAMPDIFF"; "DATE_FORMAT" |]
                   "DATE_ADD('2024-02-28', INTERVAL 1 DAY), DATE_SUB('2024-03-01', INTERVAL 1 DAY), TIMESTAMP('2024-02-29 12:34:56'), TIMESTAMPADD(DAY, 2, '2024-02-28'), TIMESTAMPDIFF(DAY, '2024-02-28', '2024-03-01'), DATE_FORMAT('2024-02-29 12:34:56', '%Y-%m-%d')"
                   "DATE_ADD(CAST(? AS DATE), INTERVAL ? DAY), DATE_SUB(CAST(? AS DATE), INTERVAL ? DAY), TIMESTAMP(?), TIMESTAMPADD(DAY, ?, CAST(? AS DATE)), TIMESTAMPDIFF(DAY, ?, ?), DATE_FORMAT(?, ?)"
                   [| box "2024-02-28"
                      box 1
                      box "2024-03-01"
                      box 1
                      box "2024-02-29 12:34:56"
                      box 2
                      box "2024-02-28"
                      box "2024-02-28"
                      box "2024-03-01"
                      box "2024-02-29 12:34:56"
                      box "%Y-%m-%d" |]
                   (Some(
                       [| "DATE_ADD"; "DATE_SUB"; "TIMESTAMP"; "TIMESTAMPADD"; "TIMESTAMPDIFF"; "DATE_FORMAT" |],
                       "DATE_ADD(CAST(? AS DATE), INTERVAL 1 DAY), DATE_SUB(CAST(? AS DATE), INTERVAL 1 DAY), TIMESTAMP(?), TIMESTAMPADD(DAY, 2, CAST(? AS DATE)), TIMESTAMPDIFF(DAY, ?, '2024-03-01'), DATE_FORMAT(?, '%Y-%m-%d')",
                       Array.create 6 nullValue
                   ))
                   [||] |]

        let spatialAliasSpecs =
            [| spec
                   "spatial-text-aliases"
                   [| "ST_GEOMETRYFROMTEXT"; "ST_LINESTRINGFROMTEXT"; "ST_POLYGONFROMTEXT"; "ST_ASWKT"; "ST_ASBINARY" |]
                   "ST_GEOMETRYFROMTEXT('POINT(1 2)'), ST_LINESTRINGFROMTEXT('LINESTRING(0 0, 2 2)'), ST_POLYGONFROMTEXT('POLYGON((0 0, 2 0, 0 2, 0 0))'), ST_ASWKT(ST_POINTFROMTEXT('POINT(1 2)')), ST_ASBINARY(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMETRYFROMTEXT(?), ST_LINESTRINGFROMTEXT(?), ST_POLYGONFROMTEXT(?), ST_ASWKT(ST_POINTFROMTEXT(?)), ST_ASBINARY(ST_POINTFROMTEXT(?))"
                   [| box "POINT(1 2)"
                      box "LINESTRING(0 0, 2 2)"
                      box "POLYGON((0 0, 2 0, 0 2, 0 0))"
                      box "POINT(1 2)"
                      box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMETRYFROMTEXT"; "ST_LINESTRINGFROMTEXT"; "ST_POLYGONFROMTEXT"; "ST_ASWKT"; "ST_ASBINARY" |],
                       "ST_GEOMETRYFROMTEXT(?), ST_LINESTRINGFROMTEXT(?), ST_POLYGONFROMTEXT(?), ST_ASWKT(?), ST_ASBINARY(?)",
                       Array.create 5 nullValue
                   ))
                   [| "ST_GEOMETRYFROMTEXT", "ST_GEOMETRYFROMTEXT()"
                      "ST_LINESTRINGFROMTEXT", "ST_LINESTRINGFROMTEXT()"
                      "ST_POLYGONFROMTEXT", "ST_POLYGONFROMTEXT()"
                      "ST_ASWKT", "ST_ASWKT()"
                      "ST_ASBINARY", "ST_ASBINARY()" |]
               spec
                   "spatial-wkb-construction"
                   [| "ST_GEOMFROMWKB"; "ST_GEOMETRYFROMWKB"; "ST_POINTFROMWKB" |]
                   "ST_GEOMFROMWKB(X'0101000000000000000000F03F0000000000000040'), ST_GEOMETRYFROMWKB(X'0101000000000000000000F03F0000000000000040'), ST_POINTFROMWKB(X'0101000000000000000000F03F0000000000000040')"
                   "ST_GEOMFROMWKB(?), ST_GEOMETRYFROMWKB(?), ST_POINTFROMWKB(?)"
                   (Array.create 3 (box (Convert.FromHexString "0101000000000000000000F03F0000000000000040")))
                   (Some(
                       [| "ST_GEOMFROMWKB"; "ST_GEOMETRYFROMWKB"; "ST_POINTFROMWKB" |],
                       "ST_GEOMFROMWKB(?), ST_GEOMETRYFROMWKB(?), ST_POINTFROMWKB(?)",
                       Array.create 3 nullValue
                   ))
                   [| "ST_GEOMFROMWKB", "ST_GEOMFROMWKB()"
                      "ST_GEOMETRYFROMWKB", "ST_GEOMETRYFROMWKB()"
                      "ST_POINTFROMWKB", "ST_POINTFROMWKB()" |] |]

        let specs =
            Array.concat
                [ establishedSpecs
                  numericSpecs
                  stringSpecs
                  jsonSpecs
                  temporalSpecs
                  temporalSyntaxSpecs
                  spatialSpecs
                  spatialResultSpecs
                  spatialAliasSpecs ]

        let steps, coverage = generatedFunctionContracts specs

        { Name = "generated-function-contracts"
          Setup = [| "SET SESSION time_zone = '+00:00'" |]
          Steps = steps
          Cleanup = [||]
          Coverage = coverage }

    let private functionFamilies =
        let numericSteps, numericCoverage =
            functionProbe
                "numeric-functions"
                [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "POW"; "POWER"; "SQRT"; "LOG"; "LN"; "LOG2"; "LOG10"
                   "EXP"; "PI"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"; "ATAN2"; "DEGREES"; "RADIANS"
                   "SIGN"; "TRUNCATE"; "ROUND"; "MOD"; "GREATEST"; "LEAST"; "NULLIF"; "ISNULL"; "CONV"; "BIN"
                   "BIT_COUNT"; "OCT"; "CRC32" |]
                """SELECT ABS(-2), CEIL(1.2), CEILING(1.2), FLOOR(1.8), POW(2, 3), POWER(2, 3), SQRT(4),
                          LOG(EXP(1)), LN(EXP(1)), LOG2(8), LOG10(100), EXP(0), PI() > 3, SIN(0), COS(0), TAN(0),
                          ASIN(0), ACOS(1), ATAN(0), ATAN2(0, 1), DEGREES(PI()), RADIANS(180) > 3,
                          SIGN(-2), TRUNCATE(1.29, 1), ROUND(1.25, 1), MOD(7, 3), GREATEST(1, 3, 2),
                          LEAST(1, 3, 2), NULLIF(1, 1), ISNULL(NULL), CONV('ff', 16, 10), BIN(5), BIT_COUNT(7),
                          OCT(8), CRC32('abc')"""

        let stringSteps, stringCoverage =
            functionProbe
                "string-functions"
                [| "CONCAT"; "CONCAT_WS"; "UPPER"; "UCASE"; "LOWER"; "LCASE"; "LENGTH"; "OCTET_LENGTH"
                   "BIT_LENGTH"; "CHAR_LENGTH"; "CHARACTER_LENGTH"; "ASCII"; "ORD"; "HEX"; "UNHEX"; "LEFT"
                   "RIGHT"; "SUBSTRING"; "SUBSTR"; "MID"; "REPLACE"; "REVERSE"; "REPEAT"; "SPACE"; "LPAD"
                   "RPAD"; "LTRIM"; "RTRIM"; "TRIM"; "INSTR"; "LOCATE"; "POSITION"; "SUBSTRING_INDEX"; "ELT"
                   "FIELD"; "FIND_IN_SET"; "MAKE_SET"; "EXPORT_SET"; "FORMAT"; "QUOTE"; "STRCMP"; "MD5"; "SHA"
                   "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64"; "COMPRESS"; "UNCOMPRESS"; "UNCOMPRESSED_LENGTH"
                   "SOUNDEX"; "CHAR" |]
                """SELECT CONCAT('a', 'b'), CONCAT_WS('-', 'a', 'b'), UPPER('ab'), UCASE('ab'), LOWER('AB'), LCASE('AB'),
                          LENGTH('blå'), OCTET_LENGTH('blå'), BIT_LENGTH('A'), CHAR_LENGTH('blå'), CHARACTER_LENGTH('blå'),
                          ASCII('A'), ORD('A'), HEX('A'), HEX(UNHEX('41')), LEFT('abcd', 2), RIGHT('abcd', 2),
                          SUBSTRING('abcd', 2, 2), SUBSTR('abcd', 2, 2), MID('abcd', 2, 2), REPLACE('abc', 'b', 'x'),
                          REVERSE('abc'), REPEAT('ab', 2), LENGTH(SPACE(3)), LPAD('a', 3, '0'), RPAD('a', 3, '0'),
                          LTRIM('  a'), RTRIM('a  '), TRIM('  a  '), INSTR('abc', 'b'), LOCATE('b', 'abc'),
                          POSITION('b' IN 'abc'), SUBSTRING_INDEX('a,b,c', ',', 2), ELT(2, 'a', 'b'),
                          FIELD('b', 'a', 'b'), FIND_IN_SET('b', 'a,b'), MAKE_SET(3, 'a', 'b'),
                          EXPORT_SET(5, 'Y', 'N', ',', 4), FORMAT(1234.5, 2, 'en_US'), QUOTE('a''b'), STRCMP('a', 'b'),
                          MD5('abc'), SHA('abc'), SHA1('abc'), SHA2('abc', 256), TO_BASE64('abc'),
                          HEX(FROM_BASE64('YWJj')), UNCOMPRESSED_LENGTH(COMPRESS('abc')), HEX(UNCOMPRESS(COMPRESS('abc'))),
                          SOUNDEX('Robert'), CHAR(65, 66)"""

        let temporalSteps, temporalCoverage =
            functionProbe
                "temporal-functions"
                [| "DATE"; "TIME"; "YEAR"; "MONTH"; "DAY"; "DAYOFMONTH"; "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"
                   "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "HOUR"; "MINUTE"; "SECOND"; "MICROSECOND"
                   "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "DATE_ADD"; "ADDDATE"; "DATE_SUB"; "SUBDATE"
                   "LAST_DAY"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME"; "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"
                   "FROM_UNIXTIME"; "DATE_FORMAT"; "TIME_FORMAT"; "STR_TO_DATE"; "TIMESTAMPADD"; "TIMESTAMPDIFF" |]
                """SELECT DATE('2024-02-29 12:34:56'), TIME('12:34:56.123456'), YEAR('2024-02-29'), MONTH('2024-02-29'),
                          DAY('2024-02-29'), DAYOFMONTH('2024-02-29'), DAYOFWEEK('2024-02-29'), DAYOFYEAR('2024-02-29'),
                          WEEKDAY('2024-02-29'), WEEK('2024-02-29', 3), WEEKOFYEAR('2024-02-29'), YEARWEEK('2024-02-29', 3),
                          QUARTER('2024-05-01'), HOUR('-34:20:30.123456'), MINUTE('-34:20:30.123456'),
                          SECOND('-34:20:30.123456'), MICROSECOND('-34:20:30.123456'), DATEDIFF('2024-03-02', '2024-02-29'),
                          TIMEDIFF('12:00:01', '12:00:00'), ADDTIME('12:00:00', '01:02:03'), SUBTIME('12:00:00', '01:02:03'),
                          DATE_ADD('2024-02-29', INTERVAL 1 DAY), ADDDATE('2024-02-29', INTERVAL 1 DAY),
                          DATE_SUB('2024-03-01', INTERVAL 1 DAY), SUBDATE('2024-03-01', INTERVAL 1 DAY),
                          LAST_DAY('2024-02-10'), MAKEDATE(2024, 60), MAKETIME(12, 34, 56), SEC_TO_TIME(3661),
                          TO_DAYS('2024-02-29'), FROM_DAYS(739310), UNIX_TIMESTAMP('2024-01-01 00:00:00'),
                          FROM_UNIXTIME(1704067200), DATE_FORMAT('2024-02-29', '%Y-%m-%d'), TIME_FORMAT('12:34:56', '%H:%i:%s'),
                          STR_TO_DATE('2024-02-29', '%Y-%m-%d'), TIMESTAMPADD(DAY, 1, '2024-02-29'),
                          TIMESTAMPDIFF(DAY, '2024-02-29', '2024-03-02')"""

        let jsonSteps, jsonCoverage =
            functionProbe
                "json-functions"
                [| "JSON_ARRAY"; "JSON_OBJECT"; "JSON_VALID"; "JSON_TYPE"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_EXTRACT"
                   "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_KEYS"; "JSON_CONTAINS"; "JSON_CONTAINS_PATH"; "JSON_OVERLAPS"
                   "JSON_INSERT"; "JSON_REPLACE"; "JSON_SET"; "JSON_REMOVE"; "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"
                   "JSON_MERGE_PATCH"; "JSON_MERGE_PRESERVE"; "JSON_PRETTY"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE"
                   "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |]
                """SELECT JSON_ARRAY(1, 'a'), JSON_OBJECT('a', 1), JSON_VALID('{\"a\":1}'), JSON_TYPE('{\"a\":1}'),
                          JSON_DEPTH('{\"a\":[1]}'), JSON_LENGTH('{\"a\":1}'), JSON_EXTRACT('{\"a\":1}', '$.a'),
                          JSON_UNQUOTE('\"a\"'), JSON_QUOTE('a'), JSON_KEYS('{\"a\":1}'),
                          JSON_CONTAINS('[1,2]', '2'), JSON_CONTAINS_PATH('{\"a\":1}', 'one', '$.a'),
                          JSON_OVERLAPS('[1,2]', '[2,3]'), JSON_INSERT('{\"a\":1}', '$.b', 2),
                          JSON_REPLACE('{\"a\":1}', '$.a', 2), JSON_SET('{\"a\":1}', '$.b', 2),
                          JSON_REMOVE('{\"a\":1,\"b\":2}', '$.b'), JSON_ARRAY_APPEND('[1]', '$', 2),
                          JSON_ARRAY_INSERT('[1,3]', '$[1]', 2), JSON_MERGE_PATCH('{\"a\":1}', '{\"b\":2}'),
                          JSON_MERGE_PRESERVE('[1]', '[2]'), JSON_PRETTY('{\"a\":1}'),
                          JSON_STORAGE_SIZE('{\"a\":1}'), JSON_STORAGE_FREE('{\"a\":1}'),
                          JSON_SCHEMA_VALID('{\"type\":\"integer\"}', '1'),
                          JSON_SCHEMA_VALIDATION_REPORT('{\"type\":\"integer\"}', '\"x\"')"""

        let spatialSteps, spatialCoverage =
            functionProbe
                "spatial-functions"
                [| "ST_GEOMFROMTEXT"; "ST_GEOMETRYFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKT"; "ST_ASBINARY"
                   "ST_ASWKB"; "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_X"; "ST_Y"
                   "ST_DISTANCE"; "ST_CONTAINS"; "ST_WITHIN"; "ST_INTERSECTS"; "ST_DISJOINT"
                   "ST_TOUCHES"; "ST_EQUALS"; "ST_ENVELOPE"; "ST_CONVEXHULL"; "MBRCONTAINS"; "MBRWITHIN"; "MBRINTERSECTS" |]
                """SELECT ST_AsText(ST_GeomFromText('POINT(1 2)')), ST_AsText(ST_GeometryFromText('POINT(1 2)')),
                          ST_AsText(ST_PointFromText('POINT(1 2)')), ST_AsText(ST_GeomFromText('POINT(1 2)')),
                          ST_AsWKT(ST_GeomFromText('POINT(1 2)')), HEX(ST_AsBinary(ST_GeomFromText('POINT(1 2)'))),
                          HEX(ST_AsWKB(ST_GeomFromText('POINT(1 2)'))), ST_GeometryType(ST_GeomFromText('POINT(1 2)')),
                          ST_Dimension(ST_GeomFromText('POINT(1 2)')), ST_IsEmpty(ST_GeomFromText('POINT(1 2)')),
                          ST_IsValid(ST_GeomFromText('POINT(1 2)')), ST_X(ST_GeomFromText('POINT(1 2)')),
                          ST_Y(ST_GeomFromText('POINT(1 2)')),
                          ST_Distance(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(3 4)')),
                          ST_Contains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')),
                          ST_Within(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')),
                          ST_Intersects(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)')),
                          ST_Disjoint(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(2 2)')),
                          ST_Touches(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('LINESTRING(0 0,1 0)')),
                          ST_Equals(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)')),
                          ST_AsText(ST_Envelope(ST_GeomFromText('LINESTRING(0 0,2 2)'))),
                          ST_AsText(ST_ConvexHull(ST_GeomFromText('MULTIPOINT((0 0),(2 0),(0 2))'))),
                          MBRContains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')),
                          MBRWithin(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')),
                          MBRIntersects(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)'))"""

        let typedSpatialConstructorSteps, typedSpatialConstructorCoverage =
            functionProbe
                "typed-spatial-constructors"
                [| "ST_LINEFROMTEXT"
                   "ST_LINESTRINGFROMTEXT"
                   "ST_POLYFROMTEXT"
                   "ST_POLYGONFROMTEXT"
                   "ST_MPOINTFROMTEXT"
                   "ST_MULTIPOINTFROMTEXT"
                   "ST_MLINEFROMTEXT"
                   "ST_MULTILINESTRINGFROMTEXT"
                   "ST_MPOLYFROMTEXT"
                   "ST_MULTIPOLYGONFROMTEXT"
                   "ST_GEOMCOLLFROMTEXT"
                   "ST_GEOMCOLLFROMTXT"
                   "ST_GEOMETRYCOLLECTIONFROMTEXT"
                   "ST_LINEFROMWKB"
                   "ST_LINESTRINGFROMWKB"
                   "ST_POLYFROMWKB"
                   "ST_POLYGONFROMWKB"
                   "ST_MPOINTFROMWKB"
                   "ST_MULTIPOINTFROMWKB"
                   "ST_MLINEFROMWKB"
                   "ST_MULTILINESTRINGFROMWKB"
                   "ST_MPOLYFROMWKB"
                   "ST_MULTIPOLYGONFROMWKB"
                   "ST_GEOMCOLLFROMWKB"
                   "ST_GEOMETRYCOLLECTIONFROMWKB" |]
                """SELECT ST_LineFromText('LINESTRING(0 0,1 1)'), ST_LineStringFromText('LINESTRING(0 0,1 1)'),
                          ST_PolyFromText('POLYGON((0 0,1 0,1 1,0 0))'), ST_PolygonFromText('POLYGON((0 0,1 0,1 1,0 0))'),
                          ST_MPointFromText('MULTIPOINT((0 0),(1 1))'), ST_MultiPointFromText('MULTIPOINT((0 0),(1 1))'),
                          ST_MLineFromText('MULTILINESTRING((0 0,1 1))'), ST_MultiLineStringFromText('MULTILINESTRING((0 0,1 1))'),
                          ST_MPolyFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'), ST_MultiPolygonFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'),
                          ST_GeomCollFromText('GEOMETRYCOLLECTION(POINT(0 0))'), ST_GeomCollFromTxt('GEOMETRYCOLLECTION(POINT(0 0))'),
                          ST_GeometryCollectionFromText('GEOMETRYCOLLECTION(POINT(0 0))'),
                          ST_LineFromWKB(ST_AsWKB(ST_GeomFromText('LINESTRING(0 0,1 1)'))),
                          ST_LineStringFromWKB(ST_AsWKB(ST_GeomFromText('LINESTRING(0 0,1 1)'))),
                          ST_PolyFromWKB(ST_AsWKB(ST_GeomFromText('POLYGON((0 0,1 0,1 1,0 0))'))),
                          ST_PolygonFromWKB(ST_AsWKB(ST_GeomFromText('POLYGON((0 0,1 0,1 1,0 0))'))),
                          ST_MPointFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOINT((0 0),(1 1))'))),
                          ST_MultiPointFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOINT((0 0),(1 1))'))),
                          ST_MLineFromWKB(ST_AsWKB(ST_GeomFromText('MULTILINESTRING((0 0,1 1))'))),
                          ST_MultiLineStringFromWKB(ST_AsWKB(ST_GeomFromText('MULTILINESTRING((0 0,1 1))'))),
                          ST_MPolyFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'))),
                          ST_MultiPolygonFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'))),
                          ST_GeomCollFromWKB(ST_AsWKB(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))'))),
                          ST_GeometryCollectionFromWKB(ST_AsWKB(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))')))"""

        let spatialPropertyEdgeSteps =
            [| Contract.query
                   "spatial-property-family-and-index-rules"
                   "SELECT ST_IsClosed(ST_GeomFromText('POINT(1 2)')), ST_NumPoints(ST_GeomFromText('MULTILINESTRING((0 0,1 1))')), ST_NumGeometries(ST_GeomFromText('POINT(1 2)')), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), -1)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), 1.5)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), '1.9')), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), CAST(3.9 AS DOUBLE)))"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spatial-property-contextual-index-coercion"
                   "SELECT ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?))"
                   [| box 1.5; box 1.5M; box "1.9" |]
               |> Contract.comparingValues
               Contract.query
                   "spatial-property-srid-retention"
                   "SELECT ST_Srid(ST_PointN(ST_GeomFromText('LINESTRING(10 20,11 21)', 4326, 'axis-order=long-lat'), 1)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 20,11 21)', 4326, 'axis-order=long-lat'), 1))"
               |> Contract.comparingValues |]

        let aggregateSteps, aggregateCoverage =
            functionProbe
                "aggregate-functions"
                [| "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "STD"; "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"; "VARIANCE"
                   "VAR_POP"; "VAR_SAMP"; "BIT_AND"; "BIT_OR"; "BIT_XOR" |]
                """SELECT COUNT(*), SUM(n), AVG(n), MIN(n), MAX(n), STD(n), STDDEV(n), STDDEV_POP(n), STDDEV_SAMP(n),
                          VARIANCE(n), VAR_POP(n), VAR_SAMP(n), BIT_AND(n), BIT_OR(n), BIT_XOR(n)
                   FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3) AS valueset"""

        let errorSpecs =
            [| "abs-arity", "ABS", 1582, "42000", "SELECT ABS()"
               "pow-arity", "POW", 1582, "42000", "SELECT POW(2)"
               "char-length-arity", "CHAR_LENGTH", 1582, "42000", "SELECT CHAR_LENGTH()"
               "json-extract-arity", "JSON_EXTRACT", 1582, "42000", "SELECT JSON_EXTRACT('{}')"
               "regexp-like-arity", "REGEXP_LIKE", 1582, "42000", "SELECT REGEXP_LIKE('a')"
               "date-format-arity", "DATE_FORMAT", 1582, "42000", "SELECT DATE_FORMAT('2024-01-01')"
               "sec-to-time-arity", "SEC_TO_TIME", 1582, "42000", "SELECT SEC_TO_TIME()"
               "spatial-distance-arity", "ST_DISTANCE", 1582, "42000", "SELECT ST_DISTANCE(ST_GeomFromText('POINT(0 0)'))"
               "aggregate-sum-arity", "SUM", 1064, "42000", "SELECT SUM(1, 2)" |]

        let errorSteps, errorCoverage =
            errorSpecs
            |> Array.map (fun (name, functionName, code, sqlState, sql) -> functionError name functionName code sqlState sql)
            |> Array.unzip

        { Name = "function-family-contracts"
          Setup = [| "SET SESSION time_zone = '+00:00'" |]
          Steps =
            Array.concat
                [ numericSteps
                  stringSteps
                  temporalSteps
                  jsonSteps
                  spatialSteps
                  typedSpatialConstructorSteps
                  spatialPropertyEdgeSteps
                  aggregateSteps
                  errorSteps ]
          Cleanup = [||]
          Coverage =
            Array.concat
                [ numericCoverage
                  stringCoverage
                  temporalCoverage
                  jsonCoverage
                  spatialCoverage
                  typedSpatialConstructorCoverage
                  aggregateCoverage
                  errorCoverage ] }

    let private aggregateFunctions =
        let foldFunctions =
            [| "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "STD"; "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"; "VARIANCE"
               "VAR_POP"; "VAR_SAMP"; "BIT_AND"; "BIT_OR"; "BIT_XOR" |]

        let directFunctions = [| "GROUP_CONCAT"; "JSON_ARRAYAGG"; "JSON_OBJECTAGG" |]

        let projection =
            "COUNT(*), COUNT(n), SUM(n), AVG(n), MIN(n), MAX(n), STD(n), STDDEV(n), STDDEV_POP(n), "
            + "STDDEV_SAMP(n), VARIANCE(n), VAR_POP(n), VAR_SAMP(n), BIT_AND(bits), BIT_OR(bits), BIT_XOR(bits)"

        let errors =
            foldFunctions
            |> Array.map (fun name ->
                Contract.query
                    ("aggregate-" + name.ToLowerInvariant() + "-error")
                    (sprintf "SELECT %s(n, n) FROM contract_aggregates" name)
                |> Contract.fails 1064 "42000")

        let directErrors =
            [| "GROUP_CONCAT", "GROUP_CONCAT()"
               "JSON_ARRAYAGG", "JSON_ARRAYAGG(n, n)"
               "JSON_OBJECTAGG", "JSON_OBJECTAGG(n)" |]
            |> Array.map (fun (name, expression) ->
                Contract.query
                    ("aggregate-" + name.ToLowerInvariant() + "-error")
                    ("SELECT " + expression + " FROM contract_aggregates")
                |> Contract.fails 1064 "42000")

        { Name = "aggregate-function-contracts"
          Setup =
            [| "DROP TABLE IF EXISTS contract_aggregates"
               "CREATE TABLE contract_aggregates (grp INT NOT NULL, n INT, bits INT, label VARCHAR(10))"
               "INSERT INTO contract_aggregates VALUES (1, 1, 1, 'a'), (1, 2, 2, 'b'), (1, 3, 4, 'c'), (1, NULL, NULL, 'd'), (2, NULL, NULL, 'e')" |]
          Steps =
            Array.concat
                [ [| Contract.query
                         "aggregate-populated-text"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 1")
                     |> Contract.comparingValues
                     Contract.preparedQuery
                         "aggregate-populated-prepared"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = ?")
                         [| box 1 |]
                     |> Contract.comparingValues
                     Contract.query
                         "aggregate-all-null"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 2")
                     |> Contract.comparingValues
                     Contract.query
                         "aggregate-empty"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 99")
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-populated-text"
                         "SELECT GROUP_CONCAT(label ORDER BY label SEPARATOR ','), JSON_ARRAYAGG(n), JSON_OBJECTAGG(label, n) FROM contract_aggregates WHERE grp = 1 AND n = 2"
                     |> Contract.comparingValues
                     Contract.preparedQuery
                         "direct-aggregate-populated-prepared"
                         "SELECT GROUP_CONCAT(label ORDER BY label SEPARATOR ','), JSON_ARRAYAGG(n), JSON_OBJECTAGG(label, n) FROM contract_aggregates WHERE grp = ? AND n = 2"
                         [| box 1 |]
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-all-null"
                         "SELECT GROUP_CONCAT(n), JSON_ARRAYAGG(n), JSON_OBJECTAGG('key', n) FROM contract_aggregates WHERE grp = 2"
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-empty"
                         "SELECT GROUP_CONCAT(n), JSON_ARRAYAGG(n), JSON_OBJECTAGG('key', n) FROM contract_aggregates WHERE grp = 99"
                     |> Contract.comparingValues |]
                  errors
                  directErrors ]
          Cleanup = [| "DROP TABLE IF EXISTS contract_aggregates" |]
          Coverage =
            Array.append foldFunctions directFunctions
            |> Array.map (fun name ->
                "function:" + name.ToLowerInvariant(),
                [| "parser"; "text-differential"; "null-semantics"; "error-contract"; "prepared-protocol"; "result-type" |]) }

    let private geographicSpatial =
        let oslo = "POINT(59.9139 10.7522)"
        let london = "POINT(51.5074 -0.1278)"

        { Name = "geographic-spatial-contracts"
          Setup = [||]
          Steps =
            [| Contract.query
                   "geographic-distance-metres"
                   (sprintf
                       "SELECT ROUND(ST_Distance(ST_GeomFromText('%s', 4326), ST_GeomFromText('%s', 4326)), 3)"
                       oslo
                       london)
               |> Contract.comparingValues
               Contract.preparedQuery
                   "geographic-distance-unit-prepared"
                   "SELECT ROUND(ST_Distance(ST_GeomFromText(?, 4326), ST_GeomFromText(?, 4326), ?), 6)"
                   [| box oslo; box london; box "kilometre" |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-longitude-latitude-input"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(10.7522 59.9139)', 4326, 'axis-order=long-lat'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-wkb-longitude-latitude-input"
                   "SELECT ST_AsText(ST_GeomFromWKB(ST_AsWKB(ST_GeomFromText('POINT(10.7522 59.9139)')), 4326, 'axis-order=long-lat'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-text-output-axis-order"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), 'axis-order=long-lat'), ST_AsWKT(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ' axis-order = lat-long '), ST_AsText(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), '')"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "geographic-binary-output-axis-order-prepared"
                   "SELECT ST_AsText(ST_GeomFromWKB(ST_AsWKB(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ?))), ST_AsText(ST_GeomFromWKB(ST_AsBinary(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ?)))"
                   [| box "axis-order=long-lat"; box "axis-order=lat-long" |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-output-invalid-axis-order"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(0 0)', 4326), 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "geographic-output-axis-order-null"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(0 0)', 4326), NULL), ST_AsWKB(NULL, 'axis-order=long-lat')"
               |> Contract.comparingValues
               Contract.query
                   "geographic-output-axis-order-arity"
                   "SELECT ST_AsWKB(ST_GeomFromText('POINT(0 0)', 4326), 'axis-order=long-lat', 'extra')"
               |> Contract.fails 1582 "42000"
               Contract.preparedQuery
                   "geographic-null-distance"
                   "SELECT ST_Distance(ST_GeomFromText(?, 4326), ST_GeomFromText(?, 4326))"
                   [| box DBNull.Value; box london |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-mismatched-srid"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 0), ST_GeomFromText('POINT(0 0)', 4326))"
               |> Contract.fails 3033 "HY000"
               Contract.query
                   "geographic-latitude-domain"
                   "SELECT ST_GeomFromText('POINT(91 0)', 4326)"
               |> Contract.fails 3617 "22S03"
               Contract.query
                   "geographic-longitude-domain"
                   "SELECT ST_GeomFromText('POINT(0 181)', 4326)"
               |> Contract.fails 3616 "22S02"
               Contract.query
                   "geographic-unknown-srid"
                   "SELECT ST_GeomFromText('POINT(0 0)', 9999)"
               |> Contract.fails 3548 "SR001"
               Contract.query
                   "geographic-invalid-axis-order"
                   "SELECT ST_GeomFromText('POINT(0 0)', 4326, 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "planar-invalid-axis-order"
                   "SELECT ST_GeomFromText('POINT(0 0)', 0, 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "geographic-unknown-unit"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 4326), ST_GeomFromText('POINT(1 1)', 4326), 'bogus')"
               |> Contract.fails 3902 "SU001"
               Contract.query
                   "planar-distance-has-no-length-unit"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 0), ST_GeomFromText('POINT(1 1)', 0), 'metre')"
               |> Contract.fails 3882 "SU001"
               Contract.query
                   "spherical-distance-srid-zero"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 1)')), 6)"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spherical-distance-custom-radius-prepared"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText(?), ST_GeomFromText(?), ?), 9)"
                   [| box "POINT(0 0)"; box "POINT(0 1)"; box 1000.0 |]
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-wgs84"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)', 4326), ST_GeomFromText('POINT(0 1)', 4326)), 6)"
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-multipoint"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('MULTIPOINT(0 0,10 10)'), ST_GeomFromText('POINT(1 0)')), 6)"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spherical-distance-null"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText(?), ST_GeomFromText(?))"
                   [| box DBNull.Value; box "POINT(0 1)" |]
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-nonpositive-radius"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 1)'), 0)"
               |> Contract.fails 3706 "22003"
               Contract.query
                   "planar-linestring-length"
                   "SELECT ST_Length(ST_GeomFromText('LINESTRING(0 0,3 4)'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-linestring-length"
                   "SELECT ROUND(ST_Length(ST_GeomFromText('LINESTRING(59.9139 10.7522,51.5074 -0.1278)', 4326)), 3)"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "geographic-multilinestring-length-unit-prepared"
                   "SELECT ROUND(ST_Length(ST_GeomFromText(?, 4326), ?), 6)"
                   [| box "MULTILINESTRING((0 0,0 1),(0 0,1 0))"; box "kilometre" |]
               |> Contract.comparingValues
               Contract.query
                   "point-length-is-null"
                   "SELECT ST_Length(ST_GeomFromText('POINT(0 0)'))"
               |> Contract.comparingValues
               Contract.query
                   "point-length-with-unit-is-null"
                   "SELECT ST_Length(ST_GeomFromText('POINT(0 0)'), 'metre')"
               |> Contract.comparingValues
               Contract.query
                   "planar-length-has-no-unit"
                   "SELECT ST_Length(ST_GeomFromText('LINESTRING(0 0,1 1)'), 'metre')"
               |> Contract.fails 3882 "SU001"
               Contract.query
                   "spatial-length-arity"
                   "SELECT ST_Length()"
               |> Contract.fails 1582 "42000"
               Contract.query
                   "geographic-srs-catalog"
                   "SELECT SRS_NAME, SRS_ID, ORGANIZATION, ORGANIZATION_COORDSYS_ID, DESCRIPTION FROM information_schema.ST_SPATIAL_REFERENCE_SYSTEMS WHERE SRS_ID = 4326"
               |> Contract.comparingValues |]
          Cleanup = [||]
          Coverage =
            [| "function:st_distance",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract" |]
               "function:st_geomfromtext",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract" |]
               "function:st_geomfromwkb", [| "parser"; "text-differential"; "error-contract" |]
               "function:st_distance_sphere",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract"; "result-type" |]
               "function:st_length",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract"; "result-type" |]
               "table:information_schema.st_spatial_reference_systems", [| "text-differential" |] |] }

    let private preparedInvalidation =
        { Name = "prepared-ddl-invalidation"
          Setup = [| "DROP TABLE IF EXISTS contract_reprepare"; "CREATE TABLE contract_reprepare (id INT PRIMARY KEY)"; "INSERT INTO contract_reprepare VALUES (1)" |]
          Steps =
            [| Contract.prepare "prepare-select" "select-handle" Query "SELECT id FROM contract_reprepare ORDER BY id" [||]
               Contract.invoke "execute-before-alter" "select-handle" OracleSuccess
               Contract.execute "alter-under-handle" "ALTER TABLE contract_reprepare ADD COLUMN label VARCHAR(10) DEFAULT 'x'"
               Contract.invoke "execute-after-alter" "select-handle" OracleSuccess
               Contract.execute "drop-under-handle" "DROP TABLE contract_reprepare"
               Contract.invoke "execute-after-drop" "select-handle" (OracleError(1146, "42S02"))
               Contract.close "close-handle" "select-handle" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_reprepare" |]
          Coverage =
            [| "statement:select", [| "prepared-protocol"; "error-contract" |]
               "statement:alter_table", [| "prepared-protocol" |]
               "statement:drop_table", [| "prepared-protocol" |] |] }

    let private preparedDml =
        { Name = "prepared-dml"
          Setup =
            [| "DROP TABLE IF EXISTS contract_dml_target"
               "DROP TABLE IF EXISTS contract_dml_source"
               "CREATE TABLE contract_dml_target (id INT PRIMARY KEY, label VARCHAR(20))"
               "CREATE TABLE contract_dml_source (id INT PRIMARY KEY, label VARCHAR(20))"
               "INSERT INTO contract_dml_source VALUES (2, 'two'), (3, 'three')" |]
          Steps =
            [| Contract.preparedExecute "prepared-insert" "INSERT INTO contract_dml_target VALUES (?, ?)" [| box 1; box "one" |]
               Contract.preparedExecute
                   "prepared-insert-select"
                   "INSERT INTO contract_dml_target SELECT id, label FROM contract_dml_source WHERE id = ?"
                   [| box 2 |]
               Contract.preparedExecute
                   "prepared-update"
                   "UPDATE contract_dml_target SET label = ? WHERE id = ?"
                   [| box "ONE"; box 1 |]
               Contract.preparedExecute "prepared-replace" "REPLACE INTO contract_dml_target VALUES (?, ?)" [| box 1; box "replaced" |]
               Contract.preparedExecute
                   "prepared-replace-select"
                   "REPLACE INTO contract_dml_target SELECT id, label FROM contract_dml_source WHERE id = ?"
                   [| box 3 |]
               Contract.query "prepared-dml-state" "SELECT id, label FROM contract_dml_target ORDER BY id"
               Contract.preparedExecute "prepared-delete" "DELETE FROM contract_dml_target WHERE id = ?" [| box 2 |]
               Contract.preparedQuery "prepared-dml-final" "SELECT id, label FROM contract_dml_target WHERE id >= ? ORDER BY id" [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_dml_target"; "DROP TABLE IF EXISTS contract_dml_source" |]
          Coverage =
            [| for statement in [ "insert"; "insert_select"; "update"; "replace"; "replace_select"; "delete" ] do
                   yield "statement:" + statement, [| "prepared-protocol" |] |] }

    let private implicitCommit =
        { Name = "implicit-ddl-commit"
          Setup = [| "DROP TABLE IF EXISTS contract_commit"; "DROP TABLE IF EXISTS contract_ddl"; "CREATE TABLE contract_commit (id INT PRIMARY KEY)" |]
          Steps =
            [| Contract.execute "begin" "START TRANSACTION"
               Contract.execute "write-before-ddl" "INSERT INTO contract_commit VALUES (1)"
               Contract.execute "ddl-commits-transaction" "CREATE TABLE contract_ddl (id INT PRIMARY KEY)"
               Contract.execute "rollback-after-ddl" "ROLLBACK"
               Contract.query "write-survived" "SELECT id FROM contract_commit"
               Contract.query
                   "ddl-survived"
                   "SELECT COUNT(*) AS found FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'contract_ddl'" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_ddl"; "DROP TABLE IF EXISTS contract_commit" |]
          Coverage =
            [| "statement:create_table", [| "text-differential"; "concurrency" |]
               "statement:insert", [| "text-differential"; "concurrency" |] |] }

    let private semanticErrors =
        { Name = "semantic-error-contracts"
          Setup =
            [| "DROP TABLE IF EXISTS contract_errors"
               "CREATE TABLE contract_errors (id INT PRIMARY KEY, n INT NOT NULL)"
               "INSERT INTO contract_errors VALUES (1, 10), (2, 20)" |]
          Steps =
            [| Contract.execute "duplicate-key" "INSERT INTO contract_errors VALUES (1, 99)" |> Contract.fails 1062 "23000"
               Contract.query "unknown-column" "SELECT missing FROM contract_errors" |> Contract.fails 1054 "42S22"
               Contract.execute "wrong-value-count" "INSERT INTO contract_errors VALUES (3)" |> Contract.fails 1136 "21S01"
               Contract.query "scalar-subquery-cardinality" "SELECT (SELECT id FROM contract_errors)" |> Contract.fails 1242 "21000"
               Contract.query "row-arity" "SELECT (1, 2) = (1, 2, 3)" |> Contract.fails 1241 "21000"
               Contract.query "state-unchanged" "SELECT id, n FROM contract_errors ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_errors" |]
          Coverage =
            [| "statement:insert", [| "error-contract" |]
               "statement:select", [| "error-contract" |] |] }

    let private concurrentSessions =
        { Name = "named-connections-send-reap"
          Setup = [| "DROP TABLE IF EXISTS contract_parallel"; "CREATE TABLE contract_parallel (id INT PRIMARY KEY, n INT NOT NULL)" |]
          Steps =
            [| Contract.execute "first-write" "INSERT INTO contract_parallel VALUES (1, 10)"
               |> Contract.on "writer-one"
               |> Contract.send "first"
               Contract.execute "second-write" "INSERT INTO contract_parallel VALUES (2, 20)"
               |> Contract.on "writer-two"
               |> Contract.send "second"
               Contract.reap "first-complete" "writer-one" "first" OracleSuccess
               Contract.reap "second-complete" "writer-two" "second" OracleSuccess
               Contract.query "combined-state" "SELECT id, n FROM contract_parallel ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_parallel" |]
          Coverage = [| "statement:insert", [| "concurrency" |] |] }

    let private contendedSchedule =
        { Name = "contended-send-reap"
          Setup = [| "DROP TABLE IF EXISTS contract_lock"; "CREATE TABLE contract_lock (id INT PRIMARY KEY, n INT NOT NULL)"; "INSERT INTO contract_lock VALUES (1, 10)" |]
          Steps =
            [| Contract.execute "owner-begin" "START TRANSACTION" |> Contract.on "owner"
               Contract.execute "owner-locks-row" "UPDATE contract_lock SET n = n + 1 WHERE id = 1" |> Contract.on "owner"
               Contract.execute "waiter-begin" "START TRANSACTION" |> Contract.on "waiter"
               Contract.execute "waiter-update" "UPDATE contract_lock SET n = n + 1 WHERE id = 1"
               |> Contract.on "waiter"
               |> Contract.send "blocked-update"
               Contract.awaitPending "waiter-is-blocked" "blocked-update"
               Contract.execute "owner-commit" "COMMIT" |> Contract.on "owner"
               Contract.reap "waiter-completes" "waiter" "blocked-update" OracleSuccess
               Contract.execute "waiter-commit" "COMMIT" |> Contract.on "waiter"
               Contract.query "serialized-state" "SELECT id, n FROM contract_lock" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_lock" |]
          Coverage = [| "statement:update", [| "concurrency" |]; "statement:select", [| "concurrency" |] |] }

    let private contendedDeleteAndInsert =
        { Name = "contended-delete-and-insert"
          Setup =
            [| "DROP TABLE IF EXISTS contract_lock_mix"
               "CREATE TABLE contract_lock_mix (id INT PRIMARY KEY, n INT NOT NULL)"
               "INSERT INTO contract_lock_mix VALUES (1, 10)" |]
          Steps =
            [| Contract.execute "delete-owner-begin" "START TRANSACTION" |> Contract.on "delete-owner"
               Contract.execute "delete-owner-lock" "UPDATE contract_lock_mix SET n = n + 1 WHERE id = 1" |> Contract.on "delete-owner"
               Contract.execute "delete-waiter-begin" "START TRANSACTION" |> Contract.on "delete-waiter"
               Contract.execute "delete-waits" "DELETE FROM contract_lock_mix WHERE id = 1"
               |> Contract.on "delete-waiter"
               |> Contract.send "blocked-delete"
               Contract.awaitPending "delete-is-blocked" "blocked-delete"
               Contract.execute "delete-owner-commit" "COMMIT" |> Contract.on "delete-owner"
               Contract.reap "delete-completes" "delete-waiter" "blocked-delete" OracleSuccess
               Contract.execute "delete-waiter-commit" "COMMIT" |> Contract.on "delete-waiter"
               Contract.execute "insert-owner-begin" "START TRANSACTION" |> Contract.on "insert-owner"
               Contract.execute "insert-owner-reserves-key" "INSERT INTO contract_lock_mix VALUES (2, 20)" |> Contract.on "insert-owner"
               Contract.execute "insert-waiter-begin" "START TRANSACTION" |> Contract.on "insert-waiter"
               Contract.execute "insert-waits" "INSERT INTO contract_lock_mix VALUES (2, 21)"
               |> Contract.on "insert-waiter"
               |> Contract.send "blocked-insert"
               Contract.awaitPending "insert-is-blocked" "blocked-insert"
               Contract.execute "insert-owner-rolls-back" "ROLLBACK" |> Contract.on "insert-owner"
               Contract.reap "insert-completes" "insert-waiter" "blocked-insert" OracleSuccess
               Contract.execute "insert-waiter-commit" "COMMIT" |> Contract.on "insert-waiter"
               Contract.query "contended-mixed-state" "SELECT id, n FROM contract_lock_mix ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_lock_mix" |]
          Coverage =
            [| "statement:delete", [| "concurrency" |]
               "statement:insert", [| "concurrency" |] |] }

    let all =
        [| comments
           exactErrors
           semanticErrors
           prepared
           preparedDml
           columnTypes
           generatedFunctionFamilies
           functionFamilies
           aggregateFunctions
           geographicSpatial
           preparedInvalidation
           implicitCommit
           concurrentSessions
           contendedSchedule
           contendedDeleteAndInsert |]

    let coverage = all |> Array.collect _.Coverage

[<RequireQualifiedAccess>]
module CompatibilityRunner =
    type private PreparedContract =
        { Command: MySqlCommand
          Operation: ContractOperation }

    let private empty target status =
        { Target = target
          Status = status
          AffectedRows = 0
          Columns = [||]
          ColumnTypes = [||]
          Rows = [||]
          DataSha256 = ""
          ErrorCode = 0
          SqlState = ""
          Message = ""
          ElapsedMs = 0L }

    let private fromExecute (outcome: TargetOutcome) =
        { empty outcome.Target outcome.Status with
            AffectedRows = outcome.AffectedRows
            ErrorCode = outcome.ErrorCode
            SqlState = outcome.SqlState
            Message = outcome.Message
            ElapsedMs = outcome.ElapsedMs }

    let private fromQuery (outcome: ProbeOutcome) =
        { empty outcome.Target outcome.Status with
            Columns = outcome.Columns
            ColumnTypes = outcome.ColumnTypes
            Rows = outcome.Rows
            DataSha256 = outcome.DataSha256
            ErrorCode = outcome.ErrorCode
            SqlState = outcome.SqlState
            Message = outcome.Message
            ElapsedMs = outcome.ElapsedMs }

    let private runOperation target timeoutSeconds (connection: MySqlConnection) operation protocol sql parameters =
        task {
            match operation, protocol with
            | Execute, TextProtocol ->
                let! outcome = Database.execute target connection timeoutSeconds sql
                return fromExecute outcome
            | Execute, PreparedProtocol ->
                let! outcome = Database.executePreparedWith parameters target connection timeoutSeconds sql
                return fromExecute outcome
            | Query, TextProtocol ->
                let! outcome = Database.query target connection timeoutSeconds sql
                return fromQuery outcome
            | Query, PreparedProtocol ->
                let! outcome = Database.queryPreparedWith parameters target connection timeoutSeconds sql
                return fromQuery outcome
        }

    let private actionText =
        function
        | Run(_, TextProtocol, _, _) -> "text"
        | Run(_, PreparedProtocol, _, _) -> "prepared"
        | Send _ -> "send"
        | Reap _ -> "reap"
        | PrepareHandle _ -> "prepare"
        | InvokeHandle _ -> "execute-prepared"
        | CloseHandle _ -> "close-prepared"
        | AwaitPending _ -> "await-pending"

    let private actionSql =
        function
        | Run(_, _, sql, _)
        | Send(_, _, _, sql, _)
        | PrepareHandle(_, _, sql, _) -> sql
        | Reap _
        | InvokeHandle _
        | CloseHandle _ -> ""
        | AwaitPending _ -> ""

    let private stateQueries =
        [| "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME"
           "SELECT TRIGGER_NAME, EVENT_MANIPULATION, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() ORDER BY TRIGGER_NAME"
           "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE() ORDER BY ROUTINE_NAME"
           "SELECT EVENT_NAME, STATUS FROM information_schema.EVENTS WHERE EVENT_SCHEMA = DATABASE() ORDER BY EVENT_NAME" |]

    let private stateFingerprint target timeoutSeconds connection =
        task {
            let parts = ResizeArray<string>()

            for sql in stateQueries do
                let! outcome = Database.query target connection timeoutSeconds sql

                if not (ProbeOutcome.succeeded outcome) then
                    failwithf "%s state probe failed: %s" target outcome.Message

                parts.Add outcome.DataSha256

            return Hashing.combine parts
        }

    let private runTarget target connectionString timeoutSeconds (case: ContractCase) =
        task {
            let connections = Dictionary<string, MySqlConnection>(StringComparer.Ordinal)
            let pending = Dictionary<string, Task<ContractTargetOutcome>>(StringComparer.Ordinal)
            let prepared = Dictionary<string, PreparedContract>(StringComparer.Ordinal)

            let getConnection name =
                task {
                    match connections.TryGetValue name with
                    | true, connection -> return connection
                    | false, _ ->
                        let! connection = Database.openConnection connectionString
                        connections.Add(name, connection)
                        return connection
                }

            let outcomes = ResizeArray<ContractTargetOutcome>()

            try
                let! setup = getConnection "main"
                let! stateBefore = stateFingerprint target timeoutSeconds setup

                for sql in case.Setup do
                    let! outcome = Database.execute target setup timeoutSeconds sql

                    if not (TargetOutcome.succeeded outcome) then
                        failwithf "%s setup failed for %s: %s" target case.Name outcome.Message

                for step in case.Steps do
                    match step.Action with
                    | Run(operation, protocol, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        let! outcome = runOperation target timeoutSeconds connection operation protocol sql parameters
                        outcomes.Add outcome
                    | Send(name, operation, protocol, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        pending.Add(name, runOperation target timeoutSeconds connection operation protocol sql parameters)
                        outcomes.Add(empty target "sent")
                    | Reap name ->
                        match pending.TryGetValue name with
                        | true, operation ->
                            let! outcome = operation
                            pending.Remove name |> ignore
                            outcomes.Add outcome
                        | false, _ -> failwithf "contract %s reaps unknown operation %s" case.Name name
                    | PrepareHandle(name, operation, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        let command = connection.CreateCommand()
                        command.CommandText <- sql
                        command.CommandTimeout <- timeoutSeconds

                        parameters
                        |> Array.iteri (fun index value -> command.Parameters.AddWithValue(sprintf "@p%d" index, value) |> ignore)

                        let stopwatch = Stopwatch.StartNew()

                        try
                            do! command.PrepareAsync()
                            stopwatch.Stop()
                            prepared.Add(name, { Command = command; Operation = operation })
                            outcomes.Add({ empty target "success" with ElapsedMs = stopwatch.ElapsedMilliseconds })
                        with
                        | :? MySqlException as error ->
                            stopwatch.Stop()
                            command.Dispose()

                            outcomes.Add
                                { empty target "server_error" with
                                    ErrorCode = int error.ErrorCode
                                    SqlState = error.SqlState |> Option.ofObj |> Option.defaultValue ""
                                    Message = error.Message
                                    ElapsedMs = stopwatch.ElapsedMilliseconds }
                        | error ->
                            stopwatch.Stop()
                            command.Dispose()
                            outcomes.Add { empty target "driver_error" with Message = error.ToString(); ElapsedMs = stopwatch.ElapsedMilliseconds }
                    | InvokeHandle name ->
                        match prepared.TryGetValue name with
                        | true, handle ->
                            match handle.Operation with
                            | Execute ->
                                let! outcome = Database.executeCommand target timeoutSeconds handle.Command
                                outcomes.Add(fromExecute outcome)
                            | Query ->
                                let! outcome = Database.queryCommand target timeoutSeconds handle.Command
                                outcomes.Add(fromQuery outcome)
                        | false, _ -> failwithf "contract %s invokes unknown prepared handle %s" case.Name name
                    | CloseHandle name ->
                        match prepared.TryGetValue name with
                        | true, handle ->
                            handle.Command.Dispose()
                            prepared.Remove name |> ignore
                            outcomes.Add(empty target "success")
                        | false, _ -> failwithf "contract %s closes unknown prepared handle %s" case.Name name
                    | AwaitPending name ->
                        match pending.TryGetValue name with
                        | true, operation ->
                            do! Task.Delay 50

                            if operation.IsCompleted then
                                outcomes.Add
                                    { empty target "unexpected_completion" with
                                        Message = sprintf "operation %s completed before its lock owner released" name }
                            else
                                outcomes.Add(empty target "pending")
                        | false, _ -> failwithf "contract %s awaits unknown operation %s" case.Name name

                for operation in pending.Values do
                    let! _ = operation
                    ()

                let! cleanup = getConnection "main"

                for sql in case.Cleanup do
                    let! outcome = Database.execute target cleanup timeoutSeconds sql

                    if not (TargetOutcome.succeeded outcome) then
                        failwithf "%s cleanup failed for %s: %s" target case.Name outcome.Message

                let! stateAfter = stateFingerprint target timeoutSeconds cleanup
                return outcomes.ToArray(), stateBefore = stateAfter
            finally
                for handle in prepared.Values do
                    handle.Command.Dispose()

                for connection in connections.Values do
                    connection.Dispose()
        }

    let private expectationMatches expectation (outcome: ContractTargetOutcome) =
        match expectation with
        | OracleSuccess
        | OracleValueSuccessIgnoringLabels -> outcome.Status = "success"
        | OracleError(code, sqlState) -> outcome.Status = "server_error" && outcome.ErrorCode = code && outcome.SqlState = sqlState

    let private ignoresLabels = function
        | OracleValueSuccessIgnoringLabels -> true
        | _ -> false

    let private normalizeValueType = function
        | "TINYINT"
        | "SMALLINT"
        | "MEDIUMINT"
        | "INT"
        | "BIGINT"
        | "DECIMAL"
        | "FLOAT"
        | "DOUBLE" -> "NUMBER"
        | "CHAR"
        | "VARCHAR"
        | "TEXT" -> "TEXT"
        | columnType when columnType.StartsWith("CHAR(", StringComparison.Ordinal) -> "TEXT"
        | columnType -> columnType

    let private normalizeNumericRow (row: string) =
        JsonSerializer.Deserialize<string array>(row)
        |> Array.map (fun value ->
            let numericPayload =
                if value.StartsWith("integer:", StringComparison.Ordinal) then Some(value.Substring 8)
                elif value.StartsWith("decimal:", StringComparison.Ordinal) then Some(value.Substring 8)
                elif value.StartsWith("float:", StringComparison.Ordinal) then Some(value.Substring 6)
                else None

            match numericPayload with
            | Some payload ->
                match
                    Decimal.TryParse(
                        payload,
                        Globalization.NumberStyles.Float,
                        Globalization.CultureInfo.InvariantCulture
                    )
                with
                | true, number -> "number:" + number.ToString("G29", Globalization.CultureInfo.InvariantCulture)
                | false, _ -> "number:" + payload
            | None -> value)

    let private compare expectation (mysql: ContractTargetOutcome) (fsdb: ContractTargetOutcome) =
        if mysql.Status = "sent" && fsdb.Status = "sent" then
            "pass", "operation started on both targets"
        elif mysql.Status = "pending" && fsdb.Status = "pending" then
            "pass", "operation remained pending on both targets"
        elif not (expectationMatches expectation mysql) then
            "oracle_contract_drift", sprintf "oracle returned %s/%d/%s: %s" mysql.Status mysql.ErrorCode mysql.SqlState mysql.Message
        elif mysql.Status <> fsdb.Status then
            "status_mismatch", sprintf "mysql=%s fsdb=%s" mysql.Status fsdb.Status
        elif mysql.Status = "server_error" then
            if mysql.ErrorCode = fsdb.ErrorCode && mysql.SqlState = fsdb.SqlState then
                "pass", sprintf "error %d/%s matched" mysql.ErrorCode mysql.SqlState
            else
                "error_contract_mismatch", sprintf "mysql=%d/%s fsdb=%d/%s" mysql.ErrorCode mysql.SqlState fsdb.ErrorCode fsdb.SqlState
        elif mysql.Status <> "success" then
            "infrastructure", if mysql.Status <> "success" then mysql.Message else fsdb.Message
        elif not (ignoresLabels expectation) && mysql.Columns <> fsdb.Columns then
            "result_schema_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Columns fsdb.Columns
        elif expectation = OracleValueSuccessIgnoringLabels then
            let mysqlTypes = mysql.ColumnTypes |> Array.map normalizeValueType
            let fsdbTypes = fsdb.ColumnTypes |> Array.map normalizeValueType
            let mysqlRows = mysql.Rows |> Array.map normalizeNumericRow
            let fsdbRows = fsdb.Rows |> Array.map normalizeNumericRow

            if mysqlTypes <> fsdbTypes then
                "result_type_mismatch", sprintf "mysql=%A fsdb=%A" mysqlTypes fsdbTypes
            elif mysqlRows <> fsdbRows then
                "result_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Rows fsdb.Rows
            else
                "pass", "labels and compatible value type families ignored; values and remaining types matched"
        elif mysql.ColumnTypes.Length > 0 then
            let mysqlProbe =
                { ProbeOutcome.notRun "mysql" with
                    Status = mysql.Status
                    Columns = mysql.Columns
                    ColumnTypes = mysql.ColumnTypes
                    Rows = mysql.Rows
                    DataSha256 = mysql.DataSha256 }

            let fsdbProbe =
                { ProbeOutcome.notRun "fsdb" with
                    Status = fsdb.Status
                    Columns = fsdb.Columns
                    ColumnTypes = fsdb.ColumnTypes
                    Rows = fsdb.Rows
                    DataSha256 = fsdb.DataSha256 }

            match Runner.compareProbeTypes mysqlProbe fsdbProbe with
            | Some detail -> "result_type_mismatch", detail
            | None when
                (ignoresLabels expectation && mysql.Rows <> fsdb.Rows)
                || (not (ignoresLabels expectation) && mysql.DataSha256 <> fsdb.DataSha256)
                ->
                "result_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Rows fsdb.Rows
            | None -> "pass", "columns, types, and ordered rows matched"
        elif mysql.AffectedRows <> fsdb.AffectedRows then
            "affected_rows_mismatch", sprintf "mysql=%d fsdb=%d" mysql.AffectedRows fsdb.AffectedRows
        else
            "pass", sprintf "affected rows matched at %d" mysql.AffectedRows

    let private runCase mysqlConnection fsdbConnection timeoutSeconds case =
        task {
            let! mysql, mysqlStateRestored = runTarget "mysql" mysqlConnection timeoutSeconds case
            let! fsdb, fsdbStateRestored = runTarget "fsdb" fsdbConnection timeoutSeconds case

            let steps =
                Array.map3
                    (fun step mysqlOutcome fsdbOutcome ->
                        let classification, detail = compare step.Expectation mysqlOutcome fsdbOutcome

                        { Name = step.Name
                          Connection = step.Connection
                          Action = actionText step.Action
                          Sql = actionSql step.Action
                          MySql = mysqlOutcome
                          Fsdb = fsdbOutcome
                          Classification = classification
                          Detail = detail
                          Passed = classification = "pass" })
                    case.Steps
                    mysql
                    fsdb

            let stateDetail =
                match mysqlStateRestored, fsdbStateRestored with
                | true, true -> "database objects returned to their pre-case state"
                | false, true -> "MySQL contract cleanup leaked database objects"
                | true, false -> "fsdb contract cleanup leaked database objects"
                | false, false -> "both targets leaked database objects"

            return
                { Name = case.Name
                  Steps = steps
                  MySqlStateRestored = mysqlStateRestored
                  FsdbStateRestored = fsdbStateRestored
                  StateDetail = stateDetail
                  Passed = mysqlStateRestored && fsdbStateRestored && (steps |> Array.forall _.Passed) }
        }

    let run (options: CompatibilityOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let directory = Path.Combine(options.ArtifactRoot, runId, "contracts")
            Directory.CreateDirectory directory |> ignore
            let databaseName = sprintf "fsdb_contract_%d_%s" Environment.ProcessId ((Hashing.text runId).Substring(0, 10))
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location

            match! Database.createOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds with
            | Error error -> return Error error.Message
            | Ok oracleConnection ->
                Fsdb.Log.silence ()
                use subject = new FsdbSubject()
                let fsdbConnection = Runner.fsdbConnectionString subject.Port
                let mutable runResult = Error "compatibility run did not produce a result"

                try
                    use! versionConnection = Database.openConnection oracleConnection
                    let! mysqlVersion = Database.scalarString versionConnection options.TimeoutSeconds "SELECT VERSION()"
                    let records = ResizeArray<ContractCaseRecord>()

                    for case in ContractCatalog.all do
                        let! record = runCase oracleConnection fsdbConnection options.TimeoutSeconds case
                        records.Add record

                    let cases = records.ToArray()
                    let firstStepFailure = cases |> Array.collect _.Steps |> Array.tryFind (fun step -> not step.Passed)
                    let firstStateFailure = cases |> Array.tryFind (fun case -> not case.MySqlStateRestored || not case.FsdbStateRestored)

                    let classification =
                        match firstStepFailure, firstStateFailure with
                        | Some failure, _ -> failure.Classification
                        | None, Some _ -> "state_leak"
                        | None, None -> "pass"

                    let signature =
                        match firstStepFailure, firstStateFailure with
                        | Some step, _ -> Hashing.combine [ step.Name; step.Classification; Hashing.text step.Sql; step.Detail ]
                        | None, Some case -> Hashing.combine [ case.Name; "state_leak"; case.StateDetail ]
                        | None, None -> ""

                    let manifest =
                        { SchemaVersion = 1
                          RunId = runId
                          StartedUtc = started.ToString("O")
                          FinishedUtc = DateTimeOffset.UtcNow.ToString("O")
                          FsdbRevision = revision
                          FsdbDirty = dirty
                          FsdbAssemblySha256 = Hashing.file assemblyPath
                          MySqlVersion = mysqlVersion
                          Cases = cases
                          Classification = classification
                          FailureSignature = signature
                          Passed = cases |> Array.forall _.Passed }

                    Json.write (Path.Combine(directory, "manifest.json")) manifest
                    runResult <- Ok(manifest, directory)
                with error ->
                    runResult <- Error error.Message

                let! dropped = Database.dropOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds

                if not (TargetOutcome.succeeded dropped) then
                    return Error("could not remove oracle database: " + dropped.Message)
                else
                    return runResult
        }
