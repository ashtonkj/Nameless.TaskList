# Bulk Reprocess From Date Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Re-run the existing per-message pipeline over all messages since a date, as a non-blocking background job (`POST /messages/process-since` + a status `GET`), so the knowledge base can be rebuilt.

**Architecture:** A new `GetMessagesSince` query + `IMessageSource` method; a Core `BulkProcessor.runSince` that iterates the since-list in order and calls an injected per-message processor, tallying outcomes and reporting progress; and a web-host background-job layer (in-memory one-at-a-time registry) exposing start/status endpoints.

**Tech Stack:** F# / .NET 10, Npgsql, ASP.NET Core minimal API, `System.Collections.Concurrent` + `Task`, xUnit.

## Global Constraints

- Target framework `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-23-bulk-reprocess-design.md`.
- `Pipeline.processMessage`, `PipelineResult`, `/messages/process`, `/reindex`, the digests, and all Core pipeline logic are **unchanged** — this increment is additive.
- Messages are processed in **ascending timestamp order** (topics accumulate correctly); idempotency (the per-message file guard → `Skipped`) makes a run safe to re-run/resume.
- Every message since the date is fed through the pipeline (no text/media filter); the classifier decides noise.
- One bulk job at a time; job state is in-memory; per-message errors are counted and never abort the run.
- A `private` F# record serializes to `{}` (System.Text.Json / YamlDotNet) — any record returned to the HTTP layer (e.g. `BulkProgress`) must be public.
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope: image/media understanding, job persistence across restarts, parallelism, cancellation, live-DB tests for the new query.

Reference — current shapes:
- `Pipeline.PipelineDeps = { Messages: IMessageSource; Vault: IVault; Chat: IChatClient; Model: string; Embedder: IEmbedder; TopK: int; SimilarityFloor: float }`; `PipelineResult = NotFound | Skipped | ProcessedNoise | Processed of string * string list | LlmError of string`.
- `IMessageSource` has `GetMessage` and `GetRecent`. `ChatMessage` (in `Nameless.TaskList.Core`) has `.Id`, `.ChatJid`, `.Timestamp`, etc.
- `PostgresMessageSource` uses `new NpgsqlCommand(sql, conn)` + `cmd.ExecuteReader() :?> NpgsqlDataReader` (the downcast is required — F# resolves the inherited `DbDataReader` overload) + a `mapChat reader` helper.
- The single test `FakeMessages` lives in `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`.
- Host handlers live in `src/Nameless.TaskList/ProcessMessage.fs` (`ProcessMessageHandler`/`ReindexHandler`/`DigestHandler`); routes in `Program.fs`; the endpoint test helper `statusOf` executes an `IResult` against a `DefaultHttpContext` with logging+routing services.
- Core compile order: `… Prompts.fs, Similarity.fs, Pipeline.fs, Indexer.fs, Adapters.fs, Weights.fs, Digest.fs`.

---

### Task 1: `GetMessagesSince` query + port + adapter + fake

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add `Queries.GetMessagesSince`)
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IMessageSource.GetMessagesSince`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `PostgresMessageSource.GetMessagesSince`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (`FakeMessages` gains the method)

**Interfaces:**
- Produces: `IMessageSource.GetMessagesSince : chatJid: string option * since: System.DateTime -> ChatMessage list`.

This is a port-extension ripple: adding the abstract member breaks `PostgresMessageSource` and `FakeMessages` until both implement it, so they change together.

- [ ] **Step 1: Add the SQL query**

In `src/Nameless.TaskList.Core/Library.fs`, after `GetPreviousMessagesByChatIdAndJid`, add (it reuses the full column SELECT of `GetMessageByIdAndChatJid` — copy that SELECT list verbatim and change only the FROM/WHERE/ORDER):

```fsharp
    let GetMessagesSince =
        """
SELECT
    m.id,
    m.chat_jid,
    ch.name AS chat_name,
    CASE
        WHEN ch.jid LIKE '%@g.us' THEN ch.name
        ELSE COALESCE(
                NULLIF(cn.full_name,    ''),
                NULLIF(cn.first_name,   ''),
                NULLIF(cn.push_name,    ''),
                NULLIF(cn.business_name,''),
                ch.name
             )
        END AS normalized_chat_name,
    (m.chat_jid LIKE '%@g.us') AS is_group,
    m.sender,
    CASE
        WHEN m.is_from_me THEN 'Me'
        ELSE COALESCE(
                NULLIF(c.full_name,    ''),
                NULLIF(c.first_name,   ''),
                NULLIF(c.push_name,    ''),
                NULLIF(c.business_name,''),
                m.sender
             )
        END AS sender_name,
    c.push_name     AS sender_push_name,
    c.full_name     AS sender_saved_name,
    c.business_name AS sender_business_name,
    m.is_from_me,
    m.content,
    m.media_type,
    m.filename,
    m.album_id,
    m.album_index,
    m.timestamp
FROM messages m
         LEFT JOIN chats ch             ON ch.jid = m.chat_jid
         LEFT JOIN whatsmeow_contacts c  ON split_part(c.their_jid, '@', 1)  = m.sender
         LEFT JOIN whatsmeow_contacts cn ON split_part(cn.their_jid, '@', 1) = split_part(m.chat_jid, '@', 1)
WHERE m.chat_jid NOT LIKE 'status%'
  AND m.timestamp >= @Since
  AND (@ChatJid IS NULL OR m.chat_jid = @ChatJid)
ORDER BY m.timestamp ASC;
        """
```

- [ ] **Step 2: Add the port member**

In `src/Nameless.TaskList.Core/Ports.fs`, inside `IMessageSource`, after `GetRecent`:

```fsharp
    abstract member GetMessagesSince : chatJid: string option * since: System.DateTime -> ChatMessage list
```

- [ ] **Step 3: Run the build to see it fail**

Run: `dotnet build src/Nameless.TaskList.Core`
Expected: FAIL — `PostgresMessageSource` does not implement `GetMessagesSince`.

- [ ] **Step 4: Implement in `PostgresMessageSource`**

In `src/Nameless.TaskList.Core/Adapters.fs`, in the `PostgresMessageSource` interface block, after `GetRecent`:

```fsharp
            member _.GetMessagesSince(chatJid, since) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetMessagesSince, conn)
                cmd.Parameters.AddWithValue("Since", since) |> ignore
                let p = cmd.Parameters.Add("ChatJid", NpgsqlTypes.NpgsqlDbType.Text)
                p.Value <- (match chatJid with Some j -> box j | None -> box System.DBNull.Value)
                // F# resolves ExecuteReader() to the inherited DbDataReader overload, so the downcast is required.
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                [ while reader.Read() do yield mapChat reader ]
```

- [ ] **Step 5: Implement in `FakeMessages`**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, add to the `FakeMessages` interface block (the single-message fake never returns a since-list, so `[]` is correct):

```fsharp
        member _.GetMessagesSince(_chatJid, _since) = []
```

- [ ] **Step 6: Build + run all Core tests**

Run: `dotnet build` then `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: build clean; all existing Core tests pass (the new method is additive, exercised in Task 2).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Library.fs src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: add IMessageSource.GetMessagesSince (query + adapter)"
```

---

### Task 2: `BulkProcessor.runSince`

**Files:**
- Create: `src/Nameless.TaskList.Core/BulkProcessor.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Pipeline.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IMessageSource.GetMessagesSince`, `Pipeline.PipelineResult`.
- Produces (module `Nameless.TaskList.Core.BulkProcessor`):
  - `type BulkProgress = { Total: int; Processed: int; Noise: int; Skipped: int; Errors: int; Done: bool; Error: string }`
  - `isRunning : BulkProgress seq -> bool`
  - `runSince : IMessageSource -> processOne:(string -> string -> Pipeline.PipelineResult) -> since:System.DateTime -> chatJid:string option -> onProgress:(BulkProgress -> unit) -> BulkProgress`

- [ ] **Step 1: Register the test file**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add after the last `<Compile Include="...Tests.fs" />`:

```xml
        <Compile Include="BulkProcessorTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.BulkProcessorTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.BulkProcessor
open Xunit

// IMessageSource fake that returns a fixed since-list (ignores GetMessage/GetRecent).
type private FakeSince(msgs: ChatMessage list) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = None
        member _.GetRecent(_jid, _before, _ex) = []
        member _.GetMessagesSince(_chatJid, _since) = msgs

let private msg (id: string) : ChatMessage =
    { Id = id; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = "s"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = "x"; MediaType = null
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = DateTime(2026, 6, 1) }

[<Fact>]
let ``runSince processes messages in order and tallies outcomes`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c"; msg "d" ]) :> IMessageSource
    let seen = System.Collections.Generic.List<string>()
    // a -> Processed, b -> Skipped, c -> ProcessedNoise, d -> LlmError
    let processOne id _jid =
        seen.Add(id)
        match id with
        | "a" -> Processed("t", [])
        | "b" -> Skipped
        | "c" -> ProcessedNoise
        | _ -> LlmError "bad"
    let result = runSince src processOne (DateTime(2026,6,1)) None ignore
    Assert.Equal<string list>([ "a"; "b"; "c"; "d" ], List.ofSeq seen)
    Assert.Equal(4, result.Total)
    Assert.Equal(1, result.Processed)
    Assert.Equal(1, result.Skipped)
    Assert.Equal(1, result.Noise)
    Assert.Equal(1, result.Errors)   // LlmError counts as an error
    Assert.True(result.Done)

[<Fact>]
let ``runSince counts a thrown processOne as an error and continues`` () =
    let src = FakeSince([ msg "a"; msg "b" ]) :> IMessageSource
    let processOne id _jid = if id = "a" then failwith "boom" else Processed("t", [])
    let result = runSince src processOne (DateTime(2026,6,1)) None ignore
    Assert.Equal(1, result.Errors)
    Assert.Equal(1, result.Processed)   // b still processed
    Assert.True(result.Done)

[<Fact>]
let ``runSince reports progress once per message`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    let mutable calls = 0
    runSince src (fun _ _ -> Skipped) (DateTime(2026,6,1)) None (fun _ -> calls <- calls + 1) |> ignore
    Assert.True(calls >= 3)   // at least once per message (final report may add one)

[<Fact>]
let ``isRunning is true when any job is not done`` () =
    let done_ = { Total=1; Processed=1; Noise=0; Skipped=0; Errors=0; Done=true; Error="" }
    let running = { done_ with Done=false }
    Assert.False(isRunning [ done_ ])
    Assert.True(isRunning [ done_; running ])
    Assert.False(isRunning [])
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~BulkProcessorTests"`
Expected: FAIL — `BulkProcessor` not defined.

- [ ] **Step 4: Create BulkProcessor.fs**

Create `src/Nameless.TaskList.Core/BulkProcessor.fs`:

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

module BulkProcessor =

    // Public (returned to the HTTP layer): a private record would serialize to `{}`.
    type BulkProgress =
        { Total: int
          Processed: int
          Noise: int
          Skipped: int
          Errors: int
          Done: bool
          Error: string }

    /// True if any job in the set has not finished.
    let isRunning (progresses: BulkProgress seq) : bool =
        progresses |> Seq.exists (fun p -> not p.Done)

    /// Re-run `processOne` over every message since `since` (ascending), tallying outcomes
    /// and reporting progress after each. A per-message exception or LlmError/NotFound is
    /// counted as an error and does not abort the run.
    let runSince (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (since: System.DateTime) (chatJid: string option) (onProgress: BulkProgress -> unit) : BulkProgress =
        let msgs = messages.GetMessagesSince(chatJid, since)
        let mutable p =
            { Total = List.length msgs; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Done = false; Error = "" }
        for m in msgs do
            (try
                match processOne m.Id m.ChatJid with
                | Processed _ -> p <- { p with Processed = p.Processed + 1 }
                | ProcessedNoise -> p <- { p with Noise = p.Noise + 1 }
                | Skipped -> p <- { p with Skipped = p.Skipped + 1 }
                | LlmError _ | NotFound -> p <- { p with Errors = p.Errors + 1 }
             with _ -> p <- { p with Errors = p.Errors + 1 })
            onProgress p
        let final = { p with Done = true }
        onProgress final
        final
```

- [ ] **Step 5: Register BulkProcessor.fs in the Core project**

In `Nameless.TaskList.Core.fsproj`, add `BulkProcessor.fs` immediately **after** `Pipeline.fs`:

```xml
        <Compile Include="Pipeline.fs" />
        <Compile Include="BulkProcessor.fs" />
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~BulkProcessorTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run the full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/BulkProcessor.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add BulkProcessor.runSince (ordered reprocess with tallies)"
```

---

### Task 3: Background job registry + endpoints

**Files:**
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (DTO + `BulkJobs` + `BulkHandler`)
- Modify: `src/Nameless.TaskList/Program.fs` (the two routes)
- Modify: `tests/Nameless.TaskList.Tests/EndpointTests.fs` (mapping + isRunning tests)

**Interfaces:**
- Consumes: `BulkProcessor.runSince`/`isRunning`/`BulkProgress`, `IMessageSource`, `Pipeline.processMessage`/`PipelineResult`/`PipelineDeps`.
- Produces: `ProcessSinceRequest`, `BulkJobs.tryStart`/`get`, `BulkHandler.startToHttp`/`progressToHttp`, and the routes.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Tests/EndpointTests.fs`. Reuse the existing `statusOfResult : IResult -> int` helper already defined in this file (do NOT add a new one):

```fsharp
[<Fact>]
let ``bulk start Ok maps to 202`` () =
    Assert.Equal(202, statusOfResult (BulkHandler.startToHttp (Ok "job123")))

[<Fact>]
let ``bulk start Error maps to 409`` () =
    Assert.Equal(409, statusOfResult (BulkHandler.startToHttp (Error "already running")))

[<Fact>]
let ``bulk progress Some maps to 200 and None to 404`` () =
    let p : Nameless.TaskList.Core.BulkProcessor.BulkProgress =
        { Total = 3; Processed = 2; Noise = 0; Skipped = 1; Errors = 0; Done = true; Error = "" }
    Assert.Equal(200, statusOfResult (BulkHandler.progressToHttp (Some p)))
    Assert.Equal(404, statusOfResult (BulkHandler.progressToHttp None))
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: FAIL — `BulkHandler` not defined.

- [ ] **Step 3: Add the DTO, registry, and handler to ProcessMessage.fs**

In `src/Nameless.TaskList/ProcessMessage.fs`, after the existing handler modules, add:

```fsharp
[<CLIMutable>]
type ProcessSinceRequest = { Since: string; ChatJid: string }

module BulkJobs =
    open System.Collections.Concurrent
    open Nameless.TaskList.Core.Ports
    open Nameless.TaskList.Core.Pipeline
    open Nameless.TaskList.Core.BulkProcessor

    let private jobs = ConcurrentDictionary<string, BulkProgress>()

    /// Start a background bulk run, unless one is already running. Returns the new job id.
    let tryStart (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (since: System.DateTime) (chatJid: string option) : Result<string, string> =
        if isRunning jobs.Values then Error "a bulk job is already running"
        else
            let jobId = System.Guid.NewGuid().ToString("N")
            jobs.[jobId] <- { Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Done = false; Error = "" }
            System.Threading.Tasks.Task.Run(fun () ->
                try runSince messages processOne since chatJid (fun p -> jobs.[jobId] <- p) |> ignore
                with ex -> jobs.[jobId] <- { jobs.[jobId] with Done = true; Error = ex.Message }) |> ignore
            Ok jobId

    let get (jobId: string) : BulkProgress option =
        match jobs.TryGetValue jobId with
        | true, p -> Some p
        | _ -> None

module BulkHandler =
    let startToHttp (r: Result<string, string>) : IResult =
        match r with
        | Ok jobId -> Results.Json({| jobId = jobId |}, statusCode = 202)
        | Error msg -> Results.Json({| error = msg |}, statusCode = 409)

    let progressToHttp (p: Nameless.TaskList.Core.BulkProcessor.BulkProgress option) : IResult =
        match p with
        | Some prog -> Results.Ok(box prog)
        | None -> Results.NotFound()
```

- [ ] **Step 4: Run the endpoint tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: PASS — the 3 new mapping tests plus the existing endpoint tests.

- [ ] **Step 5: Map the routes in Program.fs**

In `src/Nameless.TaskList/Program.fs`, after the `/digest/weekly` route, add (the file already `open`s `Nameless.TaskList.Core.Ports`, `…Pipeline`, `…Adapters`, `Nameless.TaskList.Core`, and binds `cfg`):

```fsharp
        app.MapPost("/messages/process-since", System.Func<ProcessSinceRequest, IMessageSource, IVault, IChatClient, IEmbedder, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessSinceRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) ->
                match System.DateTime.TryParse(req.Since) with
                | false, _ -> Results.Json({| error = "invalid or missing 'since' date" |}, statusCode = 400)
                | true, since ->
                    let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
                    let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
                    let deps =
                        { Messages = messages; Vault = vault; Chat = chat
                          Model = cfg.["Ollama:Model"]; Embedder = embedder; TopK = topK; SimilarityFloor = floor }
                    let processOne id jid = processMessage deps id jid
                    let chatJid = if System.String.IsNullOrWhiteSpace req.ChatJid then None else Some req.ChatJid
                    BulkJobs.tryStart messages processOne since chatJid |> BulkHandler.startToHttp)) |> ignore

        app.MapGet("/messages/process-since/{jobId}", System.Func<string, Microsoft.AspNetCore.Http.IResult>(
            fun (jobId: string) -> BulkJobs.get jobId |> BulkHandler.progressToHttp)) |> ignore
```

- [ ] **Step 6: Build the whole solution + run all tests**

Run: `dotnet build` then `dotnet test`
Expected: build clean; all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList/ProcessMessage.fs src/Nameless.TaskList/Program.fs tests/Nameless.TaskList.Tests
git commit -m "feat: add POST/GET /messages/process-since background job"
```

---

### Task 4: Whole-solution verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run all tests**

Run: `dotnet test`
Expected: all pass (Core grown by Task 2; endpoint by Task 3).

- [ ] **Step 3: Confirm scope**

Run: `git diff --stat main..HEAD`
Expected: changes only under `src/Nameless.TaskList.Core/`, `src/Nameless.TaskList/` (the new endpoints + handler), `tests/`, and `docs/`. `PipelineResult`, the `/messages/process` mapping, the Indexer, and the digest engine are unchanged.

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Task 1 → §3 (query + port + adapter). Task 2 → §4 (runner: ordering, tallies, error-continue, progress) and the §7 runner tests. Task 3 → §5 (registry + endpoints) and §6 (400/409/404, per-message-error-continue is in the runner). Task 4 → §2 scope guard.
- **Backward compatibility:** adding `GetMessagesSince` to the port is the only breaking change; Task 1 updates both implementers (`PostgresMessageSource`, `FakeMessages`) in the same commit. No existing test changes behavior.
- **Type consistency:** `BulkProgress`/`runSince`/`isRunning` (Task 2) are consumed by `BulkJobs`/`BulkHandler` (Task 3); the `ProcessSinceRequest` DTO and the two `toHttp` helpers match the routes. `GetMessagesSince` signature matches across Library/port/adapter/fake.
- **Determinism in tests:** the runner tests are fully synchronous (injected `processOne`). The one-at-a-time rule is tested via the pure `isRunning` predicate, not by racing the background `Task`; the 409 path is covered by `startToHttp (Error …)`.
- **`BulkProgress` is public** (returned by `progressToHttp`) — a private record would serialize to `{}`.
- **Deferred (do NOT build here):** image/media handling, job persistence, cancellation, parallelism, live-DB tests, and any change to `PipelineResult`/the existing routes/Indexer/Digest.
```
