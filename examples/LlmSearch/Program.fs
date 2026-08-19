/// LlmSearch — the "LLM stuff as extension points" sample: fsdb core knows
/// nothing about HTTP or models; this host registers `llm_complete`/
/// `llm_embed` closing over its own model config, exposes that config as
/// `fsdb.models`, and auto-embeds inserted docs via `Db.onCommit` (the
/// pgai-vectorizer pattern). Run against a local Ollama, or `--dry-run` to
/// stub HTTP with deterministic fake embeddings.
module LlmSearch.Program

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open Fsdb
open Fsdb.Value
open Fsdb.Functions

/// Host-side model registry: alias → OpenAI-compatible endpoint + model +
/// where the key lives. Keys stay in env vars, never in SQL text.
type Model =
    { Endpoint: string; ModelName: string; ApiKeyEnv: string option }

let models =
    Map
        [ "local", { Endpoint = "http://localhost:11434/v1"; ModelName = "nomic-embed-text"; ApiKeyEnv = None }
          "local-chat", { Endpoint = "http://localhost:11434/v1"; ModelName = "llama3.2"; ApiKeyEnv = None } ]

let http = new HttpClient()
let mutable dryRun = false

let private resolve (alias: string) =
    match Map.tryFind alias models with
    | Some m -> m
    | None -> raise (SqlError(1210, sprintf "no such model alias '%s' — see SELECT * FROM fsdb.models" alias))

/// POSTs `body` to `path` on the model's endpoint, synchronously — scalars
/// block the connection thread, so a sync send with the query's
/// cancellation token is the honest shape. Failures surface as SqlError
/// 1296 (ER_GET_ERRMSG), never the generic 1105.
let private post (ctx: QueryContext) (m: Model) (path: string) (body: JsonObject) : JsonNode =
    use req = new HttpRequestMessage(HttpMethod.Post, m.Endpoint + path)
    req.Content <- new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")

    m.ApiKeyEnv
    |> Option.bind (Environment.GetEnvironmentVariable >> Option.ofObj)
    |> Option.iter (fun key -> req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", key))

    try
        use resp = http.Send(req, ctx.Cancellation)
        let text = resp.Content.ReadAsStringAsync(ctx.Cancellation).GetAwaiter().GetResult()

        if not resp.IsSuccessStatusCode then
            raise (SqlError(1296, sprintf "Got error %d '%s' from %s" (int resp.StatusCode) text m.Endpoint))

        JsonNode.Parse text
    with
    | :? SqlError | :? OperationCanceledException -> reraise ()
    | ex -> raise (SqlError(1296, sprintf "Got error '%s' from %s" ex.Message m.Endpoint))

/// Shape drift (valid JSON missing the expected keys) must land on the same
/// SqlError 1296 as transport failures — a bare NullReferenceException here
/// would surface as the generic 1105.
let private shape (m: Model) (parse: unit -> 'a) : 'a =
    try
        parse ()
    with
    | :? SqlError -> reraise ()
    | ex -> raise (SqlError(1296, sprintf "Unexpected response shape from %s: %s" m.Endpoint ex.Message))

let private vectorOf (floats: float32[]) : Value =
    let bytes = Array.zeroCreate (floats.Length * 4)
    Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length)
    VBytes bytes

/// --dry-run stand-in: 768 floats seeded from an MD5 of (model, text) —
/// deterministic per input, no server needed.
let private fakeEmbedding (model: string) (text: string) : Value =
    let hash = Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(model + "\n" + text))
    let rng = Random(BitConverter.ToInt32(hash, 0))
    vectorOf (Array.init 768 (fun _ -> float32 (rng.NextDouble() * 2.0 - 1.0)))

/// Memo keyed (model, input): re-embedding the same text (every row of an
/// ORDER BY DISTANCE evaluates the query-text embed) costs one HTTP call.
let private embedMemo = ConcurrentDictionary<string * string, Value>()

let llmEmbed (ctx: QueryContext) (args: Value list) : Value =
    match args with
    // NULL-transparent like a builtin — the executor also probes every
    // expression against an all-NULL row before touching real rows.
    | [ VNull; _ ] | [ _; VNull ] -> VNull
    | [ VString alias; VString text ] ->
        let m = resolve alias

        embedMemo.GetOrAdd(
            (m.ModelName, text),
            fun _ ->
                if dryRun then
                    fakeEmbedding m.ModelName text
                else
                    let body = JsonObject()
                    body["model"] <- m.ModelName
                    body["input"] <- text
                    let resp = post ctx m "/embeddings" body

                    shape m (fun () ->
                        resp.["data"].[0].["embedding"].AsArray()
                        |> Seq.map (fun n -> n.GetValue<float32>())
                        |> Array.ofSeq
                        |> vectorOf)
        )
    | _ -> raise (SqlError(1582, "llm_embed expects (alias, text)"))

// Arity by pattern match: two shapes, one function — no registration-time arity.
let rec llmComplete (ctx: QueryContext) (args: Value list) : Value =
    match args with
    | args when List.contains VNull args && List.length args >= 2 -> VNull
    | [ VString alias; VString prompt ] -> llmComplete ctx [ VString alias; VString prompt; VString "{}" ]
    | [ VString alias; VString prompt; VString options ] ->
        let m = resolve alias

        if dryRun then
            VString(sprintf "[dry-run %s] %s" m.ModelName prompt)
        else
            let body =
                try JsonNode.Parse(options).AsObject()
                with _ -> raise (SqlError(1210, sprintf "llm_complete: json_options is not a JSON object: %s" options))
            body["model"] <- m.ModelName
            let msg = JsonObject()
            msg["role"] <- "user"
            msg["content"] <- prompt
            body["messages"] <- JsonArray(msg)
            let resp = post ctx m "/chat/completions" body
            VString(shape m (fun () -> resp.["choices"].[0].["message"].["content"].GetValue<string>()))
    | _ -> raise (SqlError(1582, "llm_complete expects (alias, prompt [, json_options])"))

/// Prints a result set as aligned columns; fails loudly on errors so the
/// demo can't silently show garbage.
let query (conn: Db.Connection) (sql: string) =
    printfn "> %s" sql

    match conn.Query sql with
    | Executor.ResultSet(cols, rows) ->
        let cells = List.map (List.map (Option.defaultValue "NULL")) rows
        let widths = cols |> List.mapi (fun i c -> cells |> List.fold (fun w r -> max w r[i].Length) c.Length)
        let line r = r |> List.mapi (fun i (c: string) -> c.PadRight widths[i]) |> String.concat "  "
        printfn "%s" (line cols)
        for row in cells do printfn "%s" (line row)
    | Executor.Affected n -> printfn "OK, %d rows affected" n
    | Executor.Err(code, msg) -> failwithf "query failed (%d): %s" code msg

    printfn ""

/// Setup/drain statements: quiet on success, loud on failure — an ignored
/// Err here would let the demo claim work it never did.
let exec (conn: Db.Connection) (sql: string) =
    match conn.Query sql with
    | Executor.Err(code, msg) -> failwithf "'%s' failed (%d): %s" sql code msg
    | _ -> ()

let escape (s: string) = s.Replace("'", "''")

[<EntryPoint>]
let main argv =
    dryRun <- Array.contains "--dry-run" argv

    // pgai-vectorizer pattern: the commit hook only *queues* (it runs sync
    // under the commit lock, and writing back from inside it would
    // deadlock); the host drains the queue with normal UPDATEs afterwards.
    let pending = ConcurrentQueue<int64 * string>()

    let rec collect event =
        match event with
        | Storage.RowsInserted("fsdb", "docs", rows) ->
            for row in rows do
                match row[0], row[1], row[2] with
                | VInt id, VString body, VNull -> pending.Enqueue(id, body)
                | _ -> ()
        | Storage.TransactionCommitted events -> List.iter collect events
        | _ -> ()

    let db =
        Db.create ()
        |> Db.registerFunction (ScalarFunction.create "llm_embed" llmEmbed |> ScalarFunction.effectful)
        |> Db.registerFunction (ScalarFunction.create "llm_complete" llmComplete |> ScalarFunction.effectful)
        |> Db.registerTable (
            VirtualTable.create
                "models"
                [ VirtualTable.text "alias"; VirtualTable.text "endpoint"; VirtualTable.text "model"; VirtualTable.text "api_key_env" ]
                (fun () ->
                    [ for KeyValue(alias, m) in models ->
                          [| VString alias
                             VString m.Endpoint
                             VString m.ModelName
                             (match m.ApiKeyEnv with Some e -> VString e | None -> VNull) |] ]))
        |> Db.onCommit collect

    let conn = Db.connect db
    query conn "SELECT * FROM fsdb.models"

    exec conn "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, embedding VECTOR(768))"

    [ "fsdb persists data with a write-ahead log plus periodic snapshots"
      "the parser is a hand-rolled recursive-descent parser in one F# file"
      "collations decide how strings compare and sort in the engine"
      "the wire protocol speaks MySQL so any client or ORM just connects"
      "aggregate functions fold every non-NULL row value into one result"
      "virtual tables expose host state to SQL without inserting rows" ]
    |> List.iteri (fun i body -> exec conn (sprintf "INSERT INTO docs (id, body) VALUES (%d, '%s')" i (escape body)))

    let mutable embedded = 0
    let mutable item = Unchecked.defaultof<int64 * string>

    while pending.TryDequeue &item do
        let id, _ = item
        exec conn (sprintf "UPDATE docs SET embedding = llm_embed('local', body) WHERE id = %d" id)
        embedded <- embedded + 1

    printfn "embedded %d docs via the onCommit queue\n" embedded

    let question = "how does fsdb store data durably?"

    query conn (
        sprintf
            "SELECT id, body, DISTANCE(embedding, llm_embed('local', '%s'), 'COSINE') AS distance FROM docs ORDER BY DISTANCE(embedding, llm_embed('local', '%s'), 'COSINE') LIMIT 5"
            (escape question) (escape question))

    query conn (sprintf "SELECT llm_complete('local-chat', 'One sentence: %s') AS answer" (escape question))
    0
