#r "nuget: MySqlConnector, 2.6.2"

open System
open System.Diagnostics
open System.Globalization
open MySqlConnector

let arguments = fsi.CommandLineArgs |> Array.skip 1

let engine, port, pass, mode =
    match arguments with
    | [| engine; port; pass |] -> engine, int port, int pass, ""
    | [| engine; port; pass; mode |] when List.contains mode [ "range-controls"; "prefix-controls"; "nullable-count-controls" ] ->
        engine, int port, int pass, mode
    | _ -> failwith "Usage: dotnet fsi native-feature-peers.fsx -- ENGINE PORT PASS [range-controls|prefix-controls|nullable-count-controls]"

CultureInfo.CurrentCulture <- CultureInfo.InvariantCulture

let connectionString =
    MySqlConnectionStringBuilder(
        Server = "127.0.0.1",
        Port = uint32 port,
        UserID = "root",
        SslMode = MySqlSslMode.Disabled,
        Pooling = false
    )

let connection = new MySqlConnection(connectionString.ConnectionString)

let command sql =
    new MySqlCommand(sql, connection, CommandTimeout = 120)

let execute sql =
    use statement = command sql
    statement.ExecuteNonQuery()

let write sql = execute sql |> ignore

let scalar sql =
    use statement = command sql
    Convert.ToInt64(statement.ExecuteScalar())

exception ResultMismatch of string

let require label expected actual =
    if actual <> expected then
        raise (ResultMismatch(sprintf "%s expected %A got %A" label expected actual))

let seed table count row =
    for batch in [ 1..250..count ] do
        let tuples =
            [ batch .. min count (batch + 249) ]
            |> List.map row
            |> String.concat ","

        write $"INSERT INTO {table} VALUES {tuples}"

let database = $"feature_peers_{pass}"

let seedAccounts () =
    write "CREATE TABLE parent (id INT PRIMARY KEY, value INT NOT NULL)"
    write "CREATE TABLE child (id INT PRIMARY KEY, parent_id INT NOT NULL, KEY ix_child(parent_id, id))"
    seed "parent" 500 (fun id -> $"({id},1)")
    seed "child" 10000 (fun id -> $"({id},{(id - 1) % 500 + 1})")

let correlatedEquality () =
    scalar "SELECT SUM((SELECT COUNT(*) FROM child c WHERE c.parent_id = p.id)) FROM parent p"
    |> require "correlated equality count" 10000L

let correlatedRangeCount argument hint predicate expected () =
    scalar $"SELECT SUM((SELECT COUNT({argument}) FROM child c {hint} WHERE c.parent_id = p.id AND {predicate})) FROM parent p"
    |> require "correlated range count" expected

let correlatedRange = correlatedRangeCount "*"

let between = "c.id BETWEEN p.id AND p.id + 1000"

let time lane iterations run verify =
    run ()

    for _ in 1..4 do
        run ()

    let samples =
        Array.init iterations (fun _ ->
            let started = Stopwatch.GetTimestamp()
            run ()
            Stopwatch.GetElapsedTime(started).TotalMilliseconds)
        |> Array.sort

    let percentile fraction =
        samples[int (ceil (fraction * float samples.Length)) - 1]

    verify ()
    printfn "%s,%d,%s,ok,%d,%.6f,%.6f," engine pass lane iterations (percentile 0.50) (percentile 0.95)

let measure lane iterations run = time lane iterations run ignore

let probe lane action =
    try
        action ()
    with
    | ResultMismatch detail ->
        eprintfn "%s pass %d %s: %s" engine pass lane detail
        printfn "%s,%d,%s,mismatch,0,,,%s" engine pass lane detail
    | :? MySqlException as error when error.Number <> 0 ->
        eprintfn "%s pass %d %s: %d %s %s" engine pass lane error.Number error.SqlState error.Message
        printfn "%s,%d,%s,rejected,0,,,%d/%s" engine pass lane error.Number error.SqlState

let readQueries () =
    let queries =
        [ "correlated-equality", correlatedEquality
          "correlated-range", correlatedRange "" between 1500L ]

    let ordered = if pass % 2 = 0 then List.rev queries else queries
    for lane, run in ordered do
        probe lane (fun () -> measure lane 60 run)

let readRangeControls () =
    let queries =
        [ "between", "", between, 1500L
          "between-force-child", "FORCE INDEX (ix_child)", between, 1500L
          "between-ignore-primary", "IGNORE INDEX (PRIMARY)", between, 1500L
          "split-range", "", "c.id >= p.id AND c.id <= p.id + 1000", 1500L
          "upper-only", "", "c.id <= p.id + 1000", 1500L
          "lower-only", "", "p.id <= c.id", 10000L ]

    let ordered = if pass % 2 = 0 then List.rev queries else queries
    for lane, hint, predicate, expected in ordered do
        probe lane (fun () -> measure lane 15 (correlatedRange hint predicate expected))

let readNullableCounts () =
    write "ALTER TABLE child ADD COLUMN bucket INT NULL"
    write "UPDATE child SET bucket=CASE WHEN MOD(id,5)=0 THEN NULL ELSE MOD(id,3) END"

    let queries =
        [ "count-rows", "*", between, 1500L
          "count-nullable", "c.bucket", between, 1200L
          "count-nullable-filtered", "c.bucket", $"c.bucket<>1 AND {between}", 800L ]

    let ordered = if pass % 2 = 0 then List.rev queries else queries
    for lane, argument, predicate, expected in ordered do
        probe lane (fun () -> measure lane 15 (correlatedRangeCount argument "" predicate expected))

let viewUpdates () =
    probe "joined-view-update" (fun () ->
        write "CREATE TABLE status (id INT PRIMARY KEY, enabled INT NOT NULL)"
        seed "status" 500 (fun id -> $"({id},{id % 2})")
        write "CREATE VIEW positive_parent AS SELECT id, value FROM parent WHERE value > 0 WITH CASCADED CHECK OPTION"
        write "CREATE VIEW active_parent AS SELECT p.id, p.value FROM positive_parent p JOIN status s ON s.id = p.id WHERE s.enabled = 1"
        scalar "SELECT IS_UPDATABLE = 'YES' FROM information_schema.views WHERE table_schema = DATABASE() AND table_name = 'active_parent'"
        |> require "joined view updatability" 1L
        scalar "SELECT COUNT(*) FROM active_parent" |> require "joined view rows" 250L

        let update () =
            execute "UPDATE active_parent SET value = value + 1 WHERE id = 251"
            |> require "joined view affected rows" 1

        time "joined-view-update" 60 update (fun () ->
            scalar "SELECT value FROM parent WHERE id = 251"
            |> require "joined view committed value" 66L))

let viewPrefixUpdates () =
    let sizes = if pass % 2 = 0 then [ 5000; 500 ] else [ 500; 5000 ]
    for size in sizes do
        let lane = $"joined-view-prefix-{size}"
        probe lane (fun () ->
            let table = $"partner_{size}"
            let view = $"active_parent_{size}"
            write "UPDATE parent SET value = 1 WHERE id = 251"
            write $"CREATE TABLE {table} (id INT NOT NULL, sequence_no INT NOT NULL, delta INT NOT NULL, PRIMARY KEY(id, sequence_no))"
            seed table size (fun n -> $"({n},10,2)")
            write $"INSERT INTO {table} VALUES (251,20,2)"
            write $"CREATE VIEW {view} AS SELECT p.id,p.value,s.delta FROM positive_parent p JOIN {table} s ON p.id=s.id WHERE s.delta>0"
            scalar $"SELECT COUNT(*) FROM {view} WHERE id = 251" |> require "prefix matches" 2L

            let update () =
                execute $"UPDATE {view} SET value = value + delta WHERE id = 251"
                |> require "joined prefix affected rows" 1

            time lane 60 update (fun () ->
                scalar "SELECT value FROM parent WHERE id = 251"
                |> require "joined prefix committed value" 131L))

let spatial () =
    probe "spatial-mbr" (fun () ->
        // MySQL requires an explicit SRID to consider the spatial index.
        let srid = if engine = "MariaDB" || engine = "Dolt" then "" else " SRID 0"
        write $"CREATE TABLE places (id INT PRIMARY KEY, g POINT NOT NULL{srid}, SPATIAL INDEX ix_g (g))"

        seed "places" 10000 (fun id -> $"({id},ST_GeomFromText('POINT({(id - 1) % 100} {(id - 1) / 100})'))")

        let query =
            "SELECT COUNT(*) FROM places WHERE MBRContains(ST_GeomFromText('POLYGON((10 20,10 22,12 22,12 20,10 20))'),g)"

        use explain = command ("EXPLAIN " + query)
        do
            use plan = explain.ExecuteReader()
            while plan.Read() do
                let cells = Array.init plan.FieldCount (fun i -> string (plan.GetValue(i)))
                eprintfn "%s spatial EXPLAIN %s" engine (String.concat " | " cells)

        measure "spatial-mbr" 150 (fun () -> scalar query |> require "spatial strict interior matches" 1L))

    probe "spatial-geographic" (fun () ->
        let query =
            "SELECT ROUND(ST_Distance(ST_GeomFromText('POINT(179 10)',4326,'axis-order=long-lat'),ST_GeomFromText('POINT(-179 10)',4326,'axis-order=long-lat')))"

        measure "spatial-geographic" 150 (fun () -> scalar query |> require "antimeridian distance in metres" 219278L))

let fullText () =
    probe "cjk-ngram" (fun () ->
        write "CREATE TABLE articles (id INT PRIMARY KEY, body VARCHAR(100) NOT NULL, FULLTEXT KEY ix_body(body) WITH PARSER ngram) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin"

        seed "articles" 10000 (fun id ->
            let body =
                match id % 100 with
                | 0 -> "東京駅鉄道の旅"
                | 1 -> "東京の鉄道駅"
                | 2 -> "北京地铁欢迎您"
                | 3 -> "서울역지하철"
                | _ -> "日常生活記事"

            $"({id},'{body}')")

        let search phrase expected () =
            let query = $"SELECT COALESCE(SUM(id), 0) FROM articles WHERE MATCH(body) AGAINST ('\"{phrase}\"' IN BOOLEAN MODE)"
            scalar query |> require phrase expected

        for language, phrase, expected in
            [ "japanese", "東京駅", 505000L
              "chinese", "北京地铁", 495200L
              "korean", "서울역", 495300L ] do
            let lane = $"cjk-ngram-{language}"
            probe lane (fun () -> measure lane 100 (search phrase expected)))

try
    connection.Open()
    write $"CREATE DATABASE {database}"
    try
        write $"USE {database}"
        seedAccounts ()

        let workloads =
            if mode = "range-controls" then [ readRangeControls ]
            elif mode = "nullable-count-controls" then [ readNullableCounts ]
            elif mode = "prefix-controls" then
                write "CREATE VIEW positive_parent AS SELECT id, value FROM parent WHERE value > 0 WITH CASCADED CHECK OPTION"
                [ viewPrefixUpdates ]
            else [ readQueries; viewUpdates; spatial; fullText ]

        let ordered = if pass % 2 = 0 then List.rev workloads else workloads
        for run in ordered do
            run ()
    finally
        write $"DROP DATABASE {database}"
finally
    connection.Dispose()
