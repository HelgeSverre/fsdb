#r "nuget: MySqlConnector, 2.6.2"

open System
open System.Diagnostics
open System.Globalization
open System.Text.RegularExpressions
open MySqlConnector

let arguments = fsi.CommandLineArgs |> Array.skip 1

let modes =
    [ "range-controls"
      "prefix-controls"
      "nullable-count-controls"
      "expression-count-controls"
      "exact-aggregate-controls"
      "projected-range-controls"
      "projected-filter-controls"
      "correlated-residual-controls"
      "computed-projection-controls"
      "overlay-controls"
      "edge-controls" ]

let engine, port, pass, mode =
    match arguments with
    | [| engine; port; pass |] -> engine, int port, int pass, ""
    | [| engine; port; pass; mode |] when List.contains mode modes ->
        engine, int port, int pass, mode
    | _ -> failwithf "Usage: dotnet fsi native-feature-peers.fsx -- ENGINE PORT PASS [%s]" (String.concat "|" modes)

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

exception ResultMismatch of string

let scalar sql =
    use statement = command sql
    match statement.ExecuteScalar() with
    | null
    | :? DBNull -> raise (ResultMismatch "expected a non-null scalar")
    | value -> Convert.ToInt64(value)

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

let correlatedRangeAggregate name argument hint predicate expected () =
    scalar $"SELECT SUM((SELECT {name}({argument}) FROM child c {hint} WHERE c.parent_id = p.id AND {predicate})) FROM parent p"
    |> require "correlated range aggregate" expected

let correlatedRangeCount = correlatedRangeAggregate "COUNT"

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

let seedNullableBuckets () =
    write "ALTER TABLE child ADD COLUMN bucket INT NULL"
    write "UPDATE child SET bucket=CASE WHEN MOD(id,5)=0 THEN NULL ELSE MOD(id,3) END"

let readProjectedRanges () =
    seedNullableBuckets ()

    let projection = "SELECT bucket AS payload, id AS sequence_no, parent_id AS parent_key FROM child"
    let nested = $"(SELECT sequence_no, parent_key, payload FROM ({projection}) reordered)"
    let filtered = $"({projection} WHERE bucket IS NOT NULL)"
    let projectedWhere suffix prefix source aggregate =
        $"{prefix} SELECT SUM((SELECT {aggregate} FROM {source} c WHERE c.parent_key = p.id AND c.sequence_no BETWEEN p.id AND p.id + 1000 {suffix})) FROM parent p"
    let projected = projectedWhere ""

    let cases =
        let nullTest = "filtered-derived-count", projected "" filtered "COUNT(*)", 1200L
        if mode = "correlated-residual-controls" then
            let cte = $"WITH child_rows AS ({projection})"
            let residual predicate = projectedWhere $"AND ({predicate})"
            [ "derived-count", projected "" nested "COUNT(*)", 1500L
              "derived-column-equality", residual "c.payload = c.parent_key" "" nested "COUNT(*)", 2L
              "nested-or-sum", residual "c.payload = 1 OR c.payload IS NULL" "" nested "SUM(c.sequence_no)", 525250L
              "cte-not-in-count", residual "c.payload NOT IN (0,2)" cte "child_rows" "COUNT(*)", 400L
              "outer-comparison-or-sum", residual "c.payload >= p.id OR c.payload IS NULL" "" nested "SUM(c.sequence_no)", 226754L ]
        elif mode = "computed-projection-controls" then
            let calculated = "(SELECT id + bucket AS payload, id AS sequence_no, parent_id AS parent_key FROM child)"
            let nested = $"(SELECT payload * 2 - 3 AS payload, sequence_no, parent_key FROM {calculated} calculated)"
            let cte = "WITH child_rows(payload, sequence_no, parent_key) AS (SELECT bucket * 2 - 1, id, parent_id FROM child)"
            [ "derived-count", projected "" filtered "COUNT(*)", 1200L
              "computed-count", projected "" calculated "COUNT(c.payload)", 1200L
              "nested-computed-sum", projected "" nested "SUM(c.payload)", 1798800L
              "computed-cte-sum", projected cte "child_rows" "SUM(c.payload)", 1200L
              "computed-where-count", projectedWhere "AND c.payload > 1000" "" calculated "COUNT(*)", 400L ]
        elif mode = "projected-filter-controls" then
            let equality = $"({projection} WHERE bucket = 1)"
            let nestedFilter =
                $"(SELECT sequence_no, parent_key, payload FROM ({projection} WHERE bucket <> 2 OR bucket IS NULL) reordered WHERE NOT (payload <> 1) OR payload IS NULL)"
            let cteFilter = $"WITH child_rows AS ({projection} WHERE NOT (bucket <=> 1) AND bucket BETWEEN 0 AND 2)"
            [ nullTest
              "derived-equality-filter", projected "" equality "COUNT(*)", 400L
              "nested-filtered-sum", projected "" nestedFilter "SUM(c.sequence_no)", 525250L
              "cte-filtered-count", projected cteFilter "child_rows" "COUNT(*)", 800L ]
        else
            [ "physical-count", "SELECT SUM((SELECT COUNT(*) FROM child c WHERE c.parent_id = p.id AND c.id BETWEEN p.id AND p.id + 1000)) FROM parent p", 1500L
              "derived-count", projected "" nested "COUNT(*)", 1500L
              "derived-count-payload", projected "" nested "COUNT(c.payload)", 1200L
              "derived-sum-sequence", projected "" nested "SUM(c.sequence_no)", 1125750L
              "cte-count", projected $"WITH child_rows AS ({projection})" "child_rows" "COUNT(*)", 1500L
              nullTest ]

    let ordered = if pass % 2 = 0 then List.rev cases else cases
    for lane, query, expected in ordered do
        probe lane (fun () -> measure lane 30 (fun () -> scalar query |> require lane expected))

let readCountArguments () =
    seedNullableBuckets ()

    let queries =
        if mode = "expression-count-controls" then
            [ "count-rows", "*", between, 1500L
              "count-case", "CASE WHEN c.bucket<>1 THEN c.bucket END", between, 800L
              "count-distinct", "DISTINCT c.bucket", between, 1200L
              "count-distinct-case", "DISTINCT CASE WHEN c.bucket<>1 THEN c.bucket END", between, 800L ]
        else
            [ "count-rows", "*", between, 1500L
              "count-nullable", "c.bucket", between, 1200L
              "count-nullable-filtered", "c.bucket", $"c.bucket<>1 AND {between}", 800L ]

    let ordered = if pass % 2 = 0 then List.rev queries else queries
    for lane, argument, predicate, expected in ordered do
        probe lane (fun () -> measure lane 15 (correlatedRangeCount argument "" predicate expected))

let readExactAggregates () =
    seedNullableBuckets ()

    let queries =
        [ "count-rows", "COUNT", "*", between, 1500L
          "sum-case", "SUM", "CASE WHEN c.bucket<>1 THEN c.bucket END", between, 800L
          "avg", "AVG", "c.bucket", between, 400L
          "avg-nonnull", "AVG", "c.bucket", $"c.bucket IS NOT NULL AND {between}", 400L
          "min", "MIN", "c.bucket+1", between, 400L
          "max-distinct", "MAX", "DISTINCT c.bucket", between, 800L ]

    let ordered = if pass % 2 = 0 then List.rev queries else queries
    for lane, name, argument, predicate, expected in ordered do
        probe lane (fun () -> measure lane 15 (correlatedRangeAggregate name argument "" predicate expected))

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

let geographicTurns () =
    let geometry wkt = $"ST_GeomFromText('{wkt}',4326)"
    let mixedHole =
        geometry "POLYGON((63.49470848831635 -125.36064951331429,63.49470848831635 -123.07468354461439,63.75262171538674 -123.07468354461439,63.75262171538674 -125.36064951331429,63.49470848831635 -125.36064951331429),(63.55918679508395 -124.78915802113931,63.68814340861914 -124.78915802113931,63.68814340861914 -123.64617503678936,63.55918679508395 -123.64617503678936,63.55918679508395 -124.78915802113931))"

    let nearHole =
        geometry "POLYGON((5.41527870464324 12.88802131043172,5.41527870464324 12.96140523087901,5.67890365992841 12.96140523087901,5.67890365992841 12.88802131043172,5.41527870464324 12.88802131043172),(5.48118494346453 12.90636729054355,5.61299742110712 12.90636729054355,5.61299742110712 12.94305925076719,5.48118494346453 12.94305925076719,5.48118494346453 12.90636729054355))"

    let cases =
        [ "geographic-mixed-turn-forward", mixedHole, "63.55918679508395 -123.64617626538595,63.68814340861914 -123.64617626538595", 1010L
          "geographic-mixed-turn-reverse", mixedHole, "63.68814340861914 -123.64617626538595,63.55918679508395 -123.64617626538595", 1100L
          "geographic-near-hole-touch", nearHole, "5.48118494346453 12.94305729342880,5.61299742110712 12.94305729342880", 1010L
          "geographic-near-hole-cross", nearHole, "5.48118494346453 12.94305734342880,5.61299742110712 12.94305734342880", 1100L ]

    let ordered = if pass % 2 = 0 then List.rev cases else cases

    for lane, area, coordinates, expected in ordered do
        probe lane (fun () ->
            let line = geometry $"LINESTRING({coordinates})"
            // Encode intersects, crosses, touches and within as four decimal digits.
            let query = $"SELECT 1000*ST_Intersects({line},{area}) + 100*ST_Crosses({line},{area}) + 10*ST_Touches({line},{area}) + ST_Within({line},{area})"
            measure lane 150 (fun () -> scalar query |> require lane expected))

let geographicOverlays () =
    let geometry wkt = $"ST_GeomFromText('{wkt}',4326)"
    let box = "POLYGON((0 0,0 4,4 4,4 0,0 0))"
    let nearPole = "POLYGON((85 0,85 4,89 4,89 0,85 0),(86 1,88 1,88 3,86 3,86 1))"
    let cases =
        [ "geographic-overlay-corner", box, "LINESTRING(-0.01 0.010001,0.010001 -0.01)", "LINESTRING", [ -0.01; 0.010001; 0.0000010557728948424692; 0. ]
          "geographic-overlay-polar-touch", nearPole, "LINESTRING(86 1.00001,88 1.00001)", "POINT", [ 86.; 1. ] ]

    let ordered = if pass % 2 = 0 then List.rev cases else cases
    for lane, area, line, kind, expected in ordered do
        probe lane (fun () ->
            let query = $"SELECT ST_AsText(ST_Intersection({geometry line},{geometry area}))"

            measure lane 150 (fun () ->
                use statement = command query
                let wkt = string (statement.ExecuteScalar())
                let shape = Regex.Match(wkt, @"^([A-Z]+)(?:\s*\(([^()]*)\)| EMPTY)$")
                let coordinates =
                    Regex.Matches(shape.Groups[2].Value, @"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")
                    |> Seq.map (fun value -> Double.Parse(value.Value, CultureInfo.InvariantCulture))
                    |> Seq.toList

                require lane kind shape.Groups[1].Value
                require lane expected.Length coordinates.Length
                List.iter2 (fun wanted actual ->
                    if abs (wanted - actual) > 1e-10 then
                        raise (ResultMismatch $"{lane} expected coordinate {wanted} got {actual}")) expected coordinates))

let fullText cases =
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

        for name, terms, expected in cases do
            let lane = $"cjk-ngram-{name}"
            let query = $"SELECT COALESCE(SUM(id), 0) FROM articles WHERE MATCH(body) AGAINST ('{terms}' IN BOOLEAN MODE)"
            probe lane (fun () -> measure lane 100 (fun () -> scalar query |> require terms expected)))

let fullTextPhrases () =
    fullText
        [ "japanese", "\"東京駅\"", 505000L
          "chinese", "\"北京地铁\"", 495200L
          "korean", "\"서울역\"", 495300L ]

let fullTextOperators () =
    let cases =
        [ "wildcard", "東京*", 1000100L
          "exclude-phrase", "+東京 -\"東京駅\"", 495100L ]

    fullText (if pass % 2 = 0 then List.rev cases else cases)

try
    connection.Open()
    write $"CREATE DATABASE {database}"
    try
        write $"USE {database}"
        if mode <> "edge-controls" && mode <> "overlay-controls" then seedAccounts ()

        let workloads =
            if mode = "range-controls" then [ readRangeControls ]
            elif List.contains mode [ "projected-range-controls"; "projected-filter-controls"; "correlated-residual-controls"; "computed-projection-controls" ] then
                [ readProjectedRanges ]
            elif mode = "nullable-count-controls" || mode = "expression-count-controls" then [ readCountArguments ]
            elif mode = "exact-aggregate-controls" then [ readExactAggregates ]
            elif mode = "prefix-controls" then
                write "CREATE VIEW positive_parent AS SELECT id, value FROM parent WHERE value > 0 WITH CASCADED CHECK OPTION"
                [ viewPrefixUpdates ]
            elif mode = "edge-controls" then [ geographicTurns; fullTextOperators ]
            elif mode = "overlay-controls" then [ geographicOverlays ]
            else [ readQueries; viewUpdates; spatial; fullTextPhrases ]

        let ordered = if pass % 2 = 0 then List.rev workloads else workloads
        for run in ordered do
            run ()
    finally
        write $"DROP DATABASE {database}"
finally
    connection.Dispose()
