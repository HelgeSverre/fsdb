/// Bench dataset: the fixed schema and deterministic seed shared by every
/// workload, against whichever server ("fsdb" or "mysql") is under test.
module Fsdb.Benchmarks.Schema

open System
open System.Globalization
open MySqlConnector

let private envCount (name: string) (fallback: int) : int =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | s ->
        match Int32.TryParse s with
        | true, v when v > 0 -> v
        | _ -> fallback

// Overridable via FSDB_BENCH_USERS/FSDB_BENCH_ORDERS (`just bench-scale`) so
// O(n) vs O(log n) scaling stops hiding at the default 10k/50k.
let userCount = envCount "FSDB_BENCH_USERS" 10_000
let orderCount = envCount "FSDB_BENCH_ORDERS" 50_000

let private plans = [| "free"; "pro"; "enterprise" |]
let private statuses = [| "pending"; "paid"; "shipped"; "cancelled" |]

let portFor (target: string) =
    match target with
    | "fsdb"
    | "fsdb-wal" -> 3307
    | "mysql" -> 3316
    | "mysql-nofsync" -> 3317
    | other -> failwith $"unknown benchmark target: {other}"

// Pooling=false: fsdb doesn't implement COM_RESET_CONNECTION yet (a pooled
// Open() sends it, fsdb replies "Unknown command" and the client throws —
// see performance-design.md 1.7). M9-4's per-case server restart makes this
// bite even a single-connection benchmark process, since GlobalSetup now
// opens more than one connection against the same connection string
// (the readiness probe, then the seeding connection) before the benchmark
// proper ever runs.
let rootConnectionString (target: string) =
    $"Server=127.0.0.1;Port={portFor target};User=root;AllowPublicKeyRetrieval=true;SslMode=none;Pooling=false;"

let connectionString (target: string) =
    $"{rootConnectionString target}Database=fsdb_bench;"

// fsdb doesn't support semicolon-batched multi-statement commands, so every
// DDL/DML step here is its own round trip rather than one joined CommandText.
let private exec (conn: MySqlConnection) (sql: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// Drop and recreate the bench database on `target`.
let resetDatabase (target: string) =
    use root = new MySqlConnection(rootConnectionString target)
    root.Open()
    exec root "DROP DATABASE IF EXISTS fsdb_bench"
    exec root "CREATE DATABASE fsdb_bench"

let createSchema (conn: MySqlConnection) =
    exec
        conn
        """
        CREATE TABLE users (
            id INT PRIMARY KEY AUTO_INCREMENT,
            name VARCHAR(100) NOT NULL,
            email VARCHAR(150) NOT NULL UNIQUE,
            age INT NOT NULL,
            meta JSON NOT NULL,
            created_at DATETIME NOT NULL
        )
        """

    exec
        conn
        """
        CREATE TABLE orders (
            id INT PRIMARY KEY AUTO_INCREMENT,
            user_id INT NOT NULL,
            total DECIMAL(10,2) NOT NULL,
            status VARCHAR(20) NOT NULL,
            created_at DATETIME NOT NULL
        )
        """

/// Seed `userCount` users and `orderCount` orders via batched multi-row
/// INSERTs, drawing from a fixed seed so fsdb and MySQL see byte-identical
/// data.
let seed (conn: MySqlConnection) =
    let rng = Random(42)
    let batchSize = 500
    let epoch = DateTime(2020, 1, 1)

    let runBatch (columns: string) (sql: string) =
        exec conn $"INSERT INTO %s{columns} VALUES %s{sql}"

    let randomDate () =
        epoch.AddDays(float (rng.Next(365 * 5))).ToString("yyyy-MM-dd HH:mm:ss")

    for batchStart in 0 .. batchSize .. userCount - 1 do
        let batchEnd = min (batchStart + batchSize - 1) (userCount - 1)

        let rows =
            [ for i in batchStart..batchEnd ->
                let age = 18 + rng.Next(60)
                let plan = plans.[rng.Next(plans.Length)]
                $"('user_{i}','user_{i}@bench.test',{age},'{{\"plan\":\"{plan}\"}}','{randomDate ()}')" ]

        runBatch "users (name, email, age, meta, created_at)" (String.Join(",", rows))

    for batchStart in 0 .. batchSize .. orderCount - 1 do
        let batchEnd = min (batchStart + batchSize - 1) (orderCount - 1)

        let rows =
            [ for _ in batchStart..batchEnd ->
                let userId = 1 + rng.Next(userCount)
                let total = decimal (rng.Next(500, 100_000)) / 100m
                let status = statuses.[rng.Next(statuses.Length)]
                let totalStr = total.ToString(CultureInfo.InvariantCulture)
                $"({userId},{totalStr},'{status}','{randomDate ()}')" ]

        runBatch "orders (user_id, total, status, created_at)" (String.Join(",", rows))
