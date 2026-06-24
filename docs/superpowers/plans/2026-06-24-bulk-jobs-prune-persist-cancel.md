# BulkJobs: prune + persist + cancel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the bulk-reprocess job registry prune completed jobs, persist to disk (surviving restart via auto-resume), and support cancellation.

**Architecture:** Core gains a single durable `BulkJob` record (status-string instead of `Done: bool`), a pure `prune`, a cancellable `runSince`, and an `IJobStore` persistence port with a `FileSystemJobStore` JSON adapter. The web layer replaces the static `BulkJobs` module with an injectable `BulkJobRegistry` singleton that owns the in-memory dictionary, cancellation tokens, persistence, pruning, and startup resume. Endpoints delegate to the registry; a startup hook resumes any interrupted job.

**Tech Stack:** F# / .NET 10, ASP.NET minimal APIs, System.Text.Json, xUnit (2.9.2) + Xunit.SkippableFact.

## Global Constraints

- F# / .NET 10 solution `Nameless.TaskList.slnx`; `dotnet build` and `dotnet test` must pass at every commit.
- Records that cross a serialization boundary MUST be public (a `private` record serializes to `{}` under YamlDotNet/STJ).
- JSON persisted/serialized via System.Text.Json. F# `list` is NOT natively (de)serializable by STJ — serialize/deserialize as an **array** at the storage boundary.
- Status string values, used verbatim everywhere: `"running"`, `"done"`, `"cancelled"`, `"interrupted"`, `"error"`.
- `ChatJid` on a persisted job is a plain `string`; `""` means "all chats" (mirrors `ProcessSinceRequest`, where a blank `ChatJid` becomes `None`).
- Core compile order (`Nameless.TaskList.Core.fsproj`): `Ports.fs` (12) → `Pipeline.fs` (17) → `BulkProcessor.fs` (18) → `Adapters.fs` (20). Because `IJobStore` references `BulkJob`, it MUST be defined in `BulkProcessor.fs` (after `BulkJob`), not in `Ports.fs`.

## Deviation from the spec (intentional)

The spec said `onProgress` persists on every message. That would rewrite the JSON file once per message (thousands of writes per run) with no correctness benefit: auto-resume only needs the job's **params** (`Since`/`ChatJid`) and the fact it was `running`, because `runSince` re-derives progress from `GetMessagesSince` and the idempotency guard skips already-written messages. So: **the in-memory dictionary updates on every progress (live `GET`), but the file is persisted only at start, on each terminal transition, and on resume.** Everything else matches the spec.

---

### Task 1: Core — `BulkJob` model, `prune`, cancellable `runSince` (+ keep build green)

Replaces `BulkProgress` with a single durable `BulkJob` record carrying a `Status` string, adds the pure `prune`, and gives `runSince` a `CancellationToken`. Because the record is shared, the existing static `BulkJobs`/`BulkHandler` in the web project and both test files must move in lockstep so the solution still builds. The static module's adaptation here is transitional (removed in Task 4).

**Files:**
- Modify: `src/Nameless.TaskList.Core/BulkProcessor.fs` (whole module)
- Modify: `src/Nameless.TaskList/ProcessMessage.fs:34-75` (`BulkJobs` + `BulkHandler.progressToHttp`)
- Test: `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs` (rewrite)
- Test: `tests/Nameless.TaskList.Tests/EndpointTests.fs:70-107` (update `BulkProgress` → `BulkJob`)

**Interfaces:**
- Produces:
  - `type BulkJob` — `[<CLIMutable>]` record: `{ JobId: string; Since: System.DateTime; ChatJid: string; StartedAt: System.DateTime; Status: string; Total: int; Processed: int; Noise: int; Skipped: int; Errors: int; Error: string }`
  - `isRunning : BulkJob seq -> bool`
  - `prune : keep: int -> jobs: BulkJob list -> BulkJob list`
  - `runSince : IMessageSource -> (string -> string -> PipelineResult) -> BulkJob -> System.Threading.CancellationToken -> (BulkJob -> unit) -> BulkJob`

- [ ] **Step 1: Rewrite the Core tests (failing)**

Replace the entire contents of `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs` with:

```fsharp
module Nameless.TaskList.Core.Tests.BulkProcessorTests

open System
open System.Threading
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
        member _.GetMediaBytes(_id, _jid) = None

let private msg (id: string) : ChatMessage =
    { Id = id; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = "s"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = "x"; MediaType = null
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = DateTime(2026, 6, 1) }

let private newJob () : BulkJob =
    { JobId = "j1"; Since = DateTime(2026, 6, 1); ChatJid = ""; StartedAt = DateTime(2026, 6, 1)
      Status = "running"; Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }

[<Fact>]
let ``runSince processes messages in order and tallies outcomes`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c"; msg "d" ]) :> IMessageSource
    let seen = System.Collections.Generic.List<string>()
    let processOne id _jid =
        seen.Add(id)
        match id with
        | "a" -> Processed("t", [])
        | "b" -> Skipped
        | "c" -> ProcessedNoise
        | _ -> LlmError "bad"
    let result = runSince src processOne (newJob ()) CancellationToken.None ignore
    Assert.Equal<string list>([ "a"; "b"; "c"; "d" ], List.ofSeq seen)
    Assert.Equal(4, result.Total)
    Assert.Equal(1, result.Processed)
    Assert.Equal(1, result.Skipped)
    Assert.Equal(1, result.Noise)
    Assert.Equal(1, result.Errors)
    Assert.Equal("done", result.Status)

[<Fact>]
let ``runSince counts a thrown processOne as an error and continues`` () =
    let src = FakeSince([ msg "a"; msg "b" ]) :> IMessageSource
    let processOne id _jid = if id = "a" then failwith "boom" else Processed("t", [])
    let result = runSince src processOne (newJob ()) CancellationToken.None ignore
    Assert.Equal(1, result.Errors)
    Assert.Equal(1, result.Processed)
    Assert.Equal("done", result.Status)

[<Fact>]
let ``runSince reports progress once per message`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    let mutable calls = 0
    runSince src (fun _ _ -> Skipped) (newJob ()) CancellationToken.None (fun _ -> calls <- calls + 1) |> ignore
    Assert.True(calls >= 3)

[<Fact>]
let ``runSince stops early and reports cancelled when the token is already cancelled`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    use cts = new CancellationTokenSource()
    cts.Cancel()
    let result = runSince src (fun _ _ -> Skipped) (newJob ()) cts.Token ignore
    Assert.Equal(0, result.Processed)
    Assert.Equal(0, result.Skipped)
    Assert.Equal("cancelled", result.Status)

[<Fact>]
let ``runSince cancelled mid-run stops and reports partial counts`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    use cts = new CancellationTokenSource()
    // Cancel after the first message is processed.
    let processOne _id _jid = cts.Cancel(); Skipped
    let result = runSince src processOne (newJob ()) cts.Token ignore
    Assert.Equal(1, result.Skipped)
    Assert.Equal("cancelled", result.Status)

[<Fact>]
let ``isRunning is true when any job has running status`` () =
    let job s = { newJob () with Status = s }
    Assert.False(isRunning [ job "done" ])
    Assert.True(isRunning [ job "done"; job "running" ])
    Assert.False(isRunning [])

[<Fact>]
let ``prune keeps all running jobs plus the latest N non-running`` () =
    let job id status (started: DateTime) =
        { newJob () with JobId = id; Status = status; StartedAt = started }
    let jobs =
        [ job "r1" "running" (DateTime(2026, 1, 1))
          job "d1" "done" (DateTime(2026, 1, 2))
          job "d2" "done" (DateTime(2026, 1, 3))
          job "d3" "done" (DateTime(2026, 1, 4)) ]
    let kept = prune 2 jobs |> List.map (fun j -> j.JobId) |> Set.ofList
    Assert.True(Set.contains "r1" kept)   // running never evicted
    Assert.True(Set.contains "d3" kept)   // newest non-running
    Assert.True(Set.contains "d2" kept)
    Assert.False(Set.contains "d1" kept)  // oldest non-running evicted
```

- [ ] **Step 2: Run the Core tests to verify they fail to compile/fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~BulkProcessorTests"`
Expected: build error / FAIL — `BulkJob`, `prune`, and the new `runSince` arity don't exist yet.

- [ ] **Step 3: Rewrite the `BulkProcessor` module**

Replace the entire contents of `src/Nameless.TaskList.Core/BulkProcessor.fs` with:

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

module BulkProcessor =

    /// Durable record for a bulk run: identity + params + live progress + status.
    /// Public + CLIMutable so System.Text.Json round-trips it both ways (a private
    /// record serializes to `{}`).
    [<CLIMutable>]
    type BulkJob =
        { JobId: string
          Since: System.DateTime
          ChatJid: string          // "" = all chats
          StartedAt: System.DateTime
          Status: string           // running | done | cancelled | interrupted | error
          Total: int
          Processed: int
          Noise: int
          Skipped: int
          Errors: int
          Error: string }

    /// True if any job is still running.
    let isRunning (jobs: BulkJob seq) : bool =
        jobs |> Seq.exists (fun j -> j.Status = "running")

    /// Retain every running job plus the most-recent `keep` non-running jobs (by StartedAt).
    let prune (keep: int) (jobs: BulkJob list) : BulkJob list =
        let running, finished = jobs |> List.partition (fun j -> j.Status = "running")
        let keptFinished =
            finished
            |> List.sortByDescending (fun j -> j.StartedAt)
            |> List.truncate (max 0 keep)
        running @ keptFinished

    /// Re-run `processOne` over every message since `job.Since` (ascending), tallying
    /// outcomes and reporting progress after each. A per-message exception or
    /// LlmError/NotFound is counted as an error and does not abort the run. The loop
    /// stops between messages when `token` is cancelled, finishing with status
    /// "cancelled"; otherwise it finishes "done".
    let runSince (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (job: BulkJob) (token: System.Threading.CancellationToken)
                 (onProgress: BulkJob -> unit) : BulkJob =
        let chatJid = if System.String.IsNullOrWhiteSpace job.ChatJid then None else Some job.ChatJid
        let msgs = messages.GetMessagesSince(chatJid, job.Since)
        let mutable j = { job with Total = List.length msgs; Status = "running" }
        let mutable rest = msgs
        while not (List.isEmpty rest) && not token.IsCancellationRequested do
            let m = List.head rest
            rest <- List.tail rest
            (try
                match processOne m.Id m.ChatJid with
                | Processed _ -> j <- { j with Processed = j.Processed + 1 }
                | ProcessedNoise -> j <- { j with Noise = j.Noise + 1 }
                | Skipped -> j <- { j with Skipped = j.Skipped + 1 }
                | LlmError _ | NotFound -> j <- { j with Errors = j.Errors + 1 }
             with _ -> j <- { j with Errors = j.Errors + 1 })
            onProgress j
        let final = { j with Status = (if List.isEmpty rest then "done" else "cancelled") }
        onProgress final
        final
```

- [ ] **Step 4: Adapt the web `BulkJobs`/`BulkHandler` to the new record (transitional)**

In `src/Nameless.TaskList/ProcessMessage.fs`, replace the `BulkJobs` module (lines 34-64) body so it builds a `BulkJob` and calls the new `runSince` with `CancellationToken.None`:

```fsharp
module BulkJobs =
    open System.Collections.Concurrent
    open Nameless.TaskList.Core.Ports
    open Nameless.TaskList.Core.Pipeline
    open Nameless.TaskList.Core.BulkProcessor

    let private jobs = ConcurrentDictionary<string, BulkJob>()
    let private gate = obj ()

    /// Start a background bulk run, unless one is already running. Returns the new job id.
    let tryStart (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                 (since: System.DateTime) (chatJid: string option) : Result<string, string> =
        let started =
            lock gate (fun () ->
                if isRunning jobs.Values then None
                else
                    let jobId = System.Guid.NewGuid().ToString("N")
                    let job =
                        { JobId = jobId; Since = since; ChatJid = defaultArg chatJid ""
                          StartedAt = System.DateTime.UtcNow; Status = "running"
                          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
                    jobs.[jobId] <- job
                    Some job)
        match started with
        | None -> Error "a bulk job is already running"
        | Some job ->
            System.Threading.Tasks.Task.Run(fun () ->
                try runSince messages processOne job System.Threading.CancellationToken.None (fun p -> jobs.[job.JobId] <- p) |> ignore
                with ex -> jobs.[job.JobId] <- { jobs.[job.JobId] with Status = "error"; Error = ex.Message }) |> ignore
            Ok job.JobId

    let get (jobId: string) : BulkJob option =
        match jobs.TryGetValue jobId with
        | true, p -> Some p
        | _ -> None
```

Then update `BulkHandler.progressToHttp`'s type annotation (line 72) from `BulkProgress option` to `BulkJob option`:

```fsharp
    let progressToHttp (p: Nameless.TaskList.Core.BulkProcessor.BulkJob option) : IResult =
        match p with
        | Some prog -> Results.Ok(box prog)
        | None -> Results.NotFound()
```

- [ ] **Step 5: Update the endpoint tests for the new record**

In `tests/Nameless.TaskList.Tests/EndpointTests.fs`, replace the `bulk progress` test (lines 70-75):

```fsharp
[<Fact>]
let ``bulk progress Some maps to 200 and None to 404`` () =
    let p : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "j1"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1); Status = "done"
          Total = 3; Processed = 2; Noise = 0; Skipped = 1; Errors = 0; Error = "" }
    Assert.Equal(200, statusOfResult (BulkHandler.progressToHttp (Some p)))
    Assert.Equal(404, statusOfResult (BulkHandler.progressToHttp None))
```

And replace the background-failure test (lines 85-107) to assert on `Status` instead of `Done`:

```fsharp
[<Fact>]
let ``bulk job background failure is caught and stored`` () =
    let fake = FailingMessageSource() :> IMessageSource
    let result = BulkJobs.tryStart fake (fun _ _ -> Skipped) (System.DateTime(2026, 6, 1)) None
    let jobId =
        match result with
        | Ok id -> id
        | Error e -> failwith e

    let mutable progress = None
    for _ = 1 to 50 do
        System.Threading.Thread.Sleep(20)
        progress <- BulkJobs.get jobId

    let final = progress
    Assert.True(Option.isSome final, "Expected to find the job after polling")
    let p = Option.get final
    Assert.Equal("error", p.Status)
    Assert.True((p.Error.Length > 0 && p.Error.Contains("db down")), sprintf "Expected error containing 'db down', got: %s" p.Error)
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS (Core BulkProcessor tests + endpoint tests).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/BulkProcessor.fs src/Nameless.TaskList/ProcessMessage.fs tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: BulkJob record with status + prune + cancellable runSince

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 2: Core — `IJobStore` port + `FileSystemJobStore` adapter

Adds the persistence port (next to `BulkJob` for compile-order reasons) and a filesystem JSON adapter. Fully additive — nothing consumes it yet.

**Files:**
- Modify: `src/Nameless.TaskList.Core/BulkProcessor.fs` (append `IJobStore`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `FileSystemJobStore`)
- Test: `tests/Nameless.TaskList.Core.Tests/JobStoreTests.fs` (new)

**Interfaces:**
- Consumes: `BulkJob` (Task 1)
- Produces:
  - `type IJobStore` — `abstract member Save: BulkJob list -> unit`, `abstract member Load: unit -> BulkJob list`
  - `type FileSystemJobStore(path: string)` implementing `IJobStore`

- [ ] **Step 1: Write the failing roundtrip test**

Create `tests/Nameless.TaskList.Core.Tests/JobStoreTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.JobStoreTests

open System
open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.BulkProcessor
open Xunit

let private job (id: string) : BulkJob =
    { JobId = id; Since = DateTime(2026, 6, 1); ChatJid = "chat@x"; StartedAt = DateTime(2026, 6, 2)
      Status = "done"; Total = 5; Processed = 3; Noise = 1; Skipped = 1; Errors = 0; Error = "" }

[<Fact>]
let ``FileSystemJobStore round-trips jobs through a JSON file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "bulk-jobs.json")
    let store = FileSystemJobStore(path) :> IJobStore
    let jobs = [ job "a"; job "b" ]
    store.Save jobs
    let loaded = store.Load()
    Assert.Equal(2, List.length loaded)
    let a = loaded |> List.find (fun j -> j.JobId = "a")
    Assert.Equal("chat@x", a.ChatJid)
    Assert.Equal("done", a.Status)
    Assert.Equal(3, a.Processed)
    Assert.Equal(DateTime(2026, 6, 1), a.Since)

[<Fact>]
let ``FileSystemJobStore Load returns empty when the file is missing`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.json")
    let store = FileSystemJobStore(path) :> IJobStore
    Assert.Empty(store.Load())

[<Fact>]
let ``FileSystemJobStore Load returns empty on a corrupt file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, "{ not json")
    let store = FileSystemJobStore(path) :> IJobStore
    Assert.Empty(store.Load())
```

- [ ] **Step 2: Register the new test file in the project**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add a `<Compile Include="JobStoreTests.fs" />` line alongside the other test `Compile` entries (order relative to other test files does not matter, but it must appear before the test SDK targets).

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~JobStoreTests"`
Expected: build error — `IJobStore` / `FileSystemJobStore` don't exist.

- [ ] **Step 4: Add the `IJobStore` port**

Append to the `BulkProcessor` module in `src/Nameless.TaskList.Core/BulkProcessor.fs` (after `runSince`):

```fsharp
    /// Persists the set of bulk jobs. Save rewrites the whole set; Load is best-effort.
    type IJobStore =
        abstract member Save : jobs: BulkJob list -> unit
        abstract member Load : unit -> BulkJob list
```

- [ ] **Step 5: Add the `FileSystemJobStore` adapter**

In `src/Nameless.TaskList.Core/Adapters.fs`, add `open Nameless.TaskList.Core.BulkProcessor` to the existing `open` block at the top (after `open Nameless.TaskList.Core.Ports`), then add inside the `Adapters` module (e.g. after `FileSystemVault`):

```fsharp
    // ---- Bulk-job store over a single JSON file on the local filesystem ----
    type FileSystemJobStore(path: string) =
        interface IJobStore with
            member _.Save(jobs) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                // F# list isn't natively STJ-serializable: persist as an array.
                File.WriteAllText(path, JsonSerializer.Serialize(List.toArray jobs))
            member _.Load() =
                try
                    if File.Exists path then
                        match JsonSerializer.Deserialize<BulkJob[]>(File.ReadAllText path) with
                        | null -> []
                        | arr -> List.ofArray arr
                    else []
                with _ -> []
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~JobStoreTests"`
Expected: PASS (all three). If the round-trip fails because STJ cannot bind the F# record, that is the signal the `[<CLIMutable>]` from Task 1 is missing — confirm it is present on `BulkJob`.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/BulkProcessor.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests/JobStoreTests.fs tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: IJobStore port + FileSystemJobStore JSON adapter

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 3: Web — `BulkJobRegistry` + cancel handler (tested with a fake store)

Introduces the injectable registry (persistence + pruning + cancellation + resume) and the cancel-handler mapping, with unit tests against an in-memory `IJobStore`. The static `BulkJobs` module stays in place serving the endpoints until Task 4, so the build/endpoints keep working.

**Files:**
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (add `CancelOutcome`, `BulkJobRegistry`, `BulkHandler.cancelToHttp`)
- Test: `tests/Nameless.TaskList.Tests/EndpointTests.fs` (add registry + cancel tests, add `FakeJobStore`)

**Interfaces:**
- Consumes: `BulkJob`, `isRunning`, `prune`, `runSince`, `IJobStore` (Tasks 1-2); `IMessageSource`, `PipelineResult`
- Produces:
  - `type CancelOutcome = Cancelled | UnknownJob | AlreadyTerminal`
  - `type BulkJobRegistry(store: IJobStore, retain: int)` with members:
    - `TryStart : IMessageSource -> (string -> string -> PipelineResult) -> System.DateTime -> string option -> Result<string, string>`
    - `Get : string -> BulkJob option`
    - `Cancel : string -> CancelOutcome`
    - `Resume : IMessageSource -> (string -> string -> PipelineResult) -> unit`
  - `BulkHandler.cancelToHttp : CancelOutcome -> IResult`

- [ ] **Step 1: Write the failing registry + cancel-handler tests**

Append to `tests/Nameless.TaskList.Tests/EndpointTests.fs`:

```fsharp
open System.Threading

// In-memory IJobStore: Load returns the seed; Save records the latest snapshot.
type private FakeJobStore(seed: Nameless.TaskList.Core.BulkProcessor.BulkJob list) =
    let mutable saved = seed
    member _.Saved = saved
    interface Nameless.TaskList.Core.BulkProcessor.IJobStore with
        member _.Load() = seed
        member _.Save(js) = saved <- js

type private SinceSource(msgs: ChatMessage list) =
    interface IMessageSource with
        member _.GetMessage(_, _) = None
        member _.GetRecent(_, _, _) = []
        member _.GetMessagesSince(_, _) = msgs
        member _.GetMediaBytes(_, _) = None

let private testMsg (id: string) : ChatMessage =
    { Id = id; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = "s"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = "x"; MediaType = null
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = System.DateTime(2026, 6, 1) }

let private pollUntil (predicate: unit -> bool) : bool =
    let mutable ok = false
    let mutable i = 0
    while not ok && i < 100 do
        ok <- predicate ()
        if not ok then Thread.Sleep(20)
        i <- i + 1
    ok

[<Fact>]
let ``registry rejects a second concurrent job`` () =
    let started = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let src = SinceSource([ testMsg "a"; testMsg "b" ]) :> IMessageSource
    let processOne _ _ = started.Set(); release.Wait(); Skipped
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let first = reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None
    Assert.True(started.Wait(1000))
    let second = reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None
    match first with Ok _ -> () | Error e -> failwithf "expected Ok, got %s" e
    match second with Error _ -> () | Ok _ -> failwith "expected Error (already running)"
    release.Set()

[<Fact>]
let ``registry Cancel returns UnknownJob for an unknown id`` () =
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    Assert.Equal(UnknownJob, reg.Cancel "missing")

[<Fact>]
let ``registry Cancel stops a running job and reports cancelled`` () =
    let started = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let src = SinceSource([ testMsg "a"; testMsg "b"; testMsg "c" ]) :> IMessageSource
    let processOne _ _ = started.Set(); release.Wait(); Skipped
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let id = match reg.TryStart src processOne (System.DateTime(2026, 6, 1)) None with Ok i -> i | Error e -> failwith e
    Assert.True(started.Wait(1000))
    Assert.Equal(Cancelled, reg.Cancel id)
    release.Set()
    Assert.True(pollUntil (fun () -> match reg.Get id with Some j -> j.Status = "cancelled" | None -> false))

[<Fact>]
let ``registry resumes an interrupted job from the store`` () =
    let seed : Nameless.TaskList.Core.BulkProcessor.BulkJob =
        { JobId = "old"; Since = System.DateTime(2026, 6, 1); ChatJid = ""
          StartedAt = System.DateTime(2026, 6, 1); Status = "running"
          Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
    let src = SinceSource([ testMsg "a"; testMsg "b" ]) :> IMessageSource
    let reg = BulkJobRegistry(FakeJobStore([ seed ]), 20)
    reg.Resume src (fun _ _ -> Skipped)
    Assert.True(pollUntil (fun () -> match reg.Get "old" with Some j -> j.Status = "done" | None -> false))
    let j = (reg.Get "old").Value
    Assert.Equal(2, j.Skipped)

[<Fact>]
let ``bulk cancel outcomes map to 200/404/409`` () =
    Assert.Equal(200, statusOfResult (BulkHandler.cancelToHttp Cancelled))
    Assert.Equal(404, statusOfResult (BulkHandler.cancelToHttp UnknownJob))
    Assert.Equal(409, statusOfResult (BulkHandler.cancelToHttp AlreadyTerminal))
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Tests --filter "FullyQualifiedName~registry|FullyQualifiedName~cancel"`
Expected: build error — `BulkJobRegistry`, `CancelOutcome`, `BulkHandler.cancelToHttp` don't exist.

- [ ] **Step 3: Add `CancelOutcome` and `BulkJobRegistry`**

In `src/Nameless.TaskList/ProcessMessage.fs`, inside the `BulkJobs` module's `open` section add `open Nameless.TaskList.Core.Adapters` is **not** needed; the registry only needs Core types already opened. Add the following after the existing `get` function in the `BulkJobs` module (so `BulkJobRegistry` is `BulkJobs.BulkJobRegistry` and `CancelOutcome` is `BulkJobs.CancelOutcome`):

```fsharp
    type CancelOutcome =
        | Cancelled
        | UnknownJob
        | AlreadyTerminal

    /// Injectable singleton owning live job state, cancellation tokens, persistence,
    /// pruning, and startup resume. The in-memory dictionary updates on every progress;
    /// the store is written at start, on each terminal transition, and on resume.
    type BulkJobRegistry(store: IJobStore, retain: int) =
        let liveJobs = ConcurrentDictionary<string, BulkJob>()
        let tokens = ConcurrentDictionary<string, System.Threading.CancellationTokenSource>()
        let regGate = obj ()

        do for j in store.Load() do liveJobs.[j.JobId] <- j

        let persist () = store.Save(liveJobs.Values |> List.ofSeq)

        let pruneAndPersist () =
            lock regGate (fun () ->
                let kept = prune retain (liveJobs.Values |> List.ofSeq)
                let keptIds = kept |> List.map (fun j -> j.JobId) |> Set.ofList
                for id in (liveJobs.Keys |> List.ofSeq) do
                    if not (Set.contains id keptIds) then liveJobs.TryRemove id |> ignore
                persist ())

        let runJob (messages: IMessageSource) (processOne: string -> string -> PipelineResult) (job: BulkJob) =
            let cts = new System.Threading.CancellationTokenSource()
            tokens.[job.JobId] <- cts
            System.Threading.Tasks.Task.Run(fun () ->
                (try runSince messages processOne job cts.Token (fun p -> liveJobs.[job.JobId] <- p) |> ignore
                 with ex -> liveJobs.[job.JobId] <- { liveJobs.[job.JobId] with Status = "error"; Error = ex.Message })
                tokens.TryRemove job.JobId |> ignore
                pruneAndPersist ()) |> ignore

        member _.TryStart (messages: IMessageSource) (processOne: string -> string -> PipelineResult)
                          (since: System.DateTime) (chatJid: string option) : Result<string, string> =
            let started =
                lock regGate (fun () ->
                    if isRunning liveJobs.Values then None
                    else
                        let jobId = System.Guid.NewGuid().ToString("N")
                        let job =
                            { JobId = jobId; Since = since; ChatJid = defaultArg chatJid ""
                              StartedAt = System.DateTime.UtcNow; Status = "running"
                              Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }
                        liveJobs.[jobId] <- job
                        Some job)
            match started with
            | None -> Error "a bulk job is already running"
            | Some job ->
                pruneAndPersist ()
                runJob messages processOne job
                Ok job.JobId

        member _.Get (jobId: string) : BulkJob option =
            match liveJobs.TryGetValue jobId with
            | true, j -> Some j
            | _ -> None

        member _.Cancel (jobId: string) : CancelOutcome =
            match liveJobs.TryGetValue jobId with
            | false, _ -> UnknownJob
            | true, j when j.Status <> "running" -> AlreadyTerminal
            | true, _ ->
                match tokens.TryGetValue jobId with
                | true, cts -> cts.Cancel(); Cancelled
                | _ -> AlreadyTerminal

        member _.Resume (messages: IMessageSource) (processOne: string -> string -> PipelineResult) : unit =
            let interrupted =
                liveJobs.Values
                |> Seq.filter (fun j -> j.Status = "running")
                |> Seq.sortByDescending (fun j -> j.StartedAt)
                |> List.ofSeq
            match interrupted with
            | [] -> ()
            | latest :: rest ->
                for j in rest do liveJobs.[j.JobId] <- { j with Status = "interrupted" }
                persist ()
                runJob messages processOne latest
```

> Note: `BulkJob`, `isRunning`, `prune`, `runSince`, `IJobStore` are all in `Nameless.TaskList.Core.BulkProcessor`, already opened at the top of the `BulkJobs` module. `IMessageSource` is from `Nameless.TaskList.Core.Ports` (opened). `ConcurrentDictionary` is from `System.Collections.Concurrent` (opened).

- [ ] **Step 4: Add the cancel handler**

In `src/Nameless.TaskList/ProcessMessage.fs`, in the `BulkHandler` module (after `progressToHttp`):

```fsharp
    let cancelToHttp (o: BulkJobs.CancelOutcome) : IResult =
        match o with
        | BulkJobs.Cancelled -> Results.Ok(box {| cancelled = true |})
        | BulkJobs.UnknownJob -> Results.NotFound()
        | BulkJobs.AlreadyTerminal -> Results.Json({| error = "job is not running" |}, statusCode = 409)
```

- [ ] **Step 5: Make the new test names resolve**

The tests reference `BulkJobRegistry`, `CancelOutcome` cases, and `BulkHandler.cancelToHttp` unqualified/partially-qualified. Add `open Nameless.TaskList.BulkJobs` to the `open` block at the top of `tests/Nameless.TaskList.Tests/EndpointTests.fs` so `BulkJobRegistry`, `Cancelled`, `UnknownJob`, `AlreadyTerminal` resolve.

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build` then `dotnet test tests/Nameless.TaskList.Tests`
Expected: build succeeds; all endpoint tests PASS, including the four new registry tests and the cancel-mapping test.

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList/ProcessMessage.fs tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: BulkJobRegistry (persist + prune + cancel + resume) with cancel handler

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 4: Web — wire `Program.fs` (DI, endpoints, startup resume) and retire the static module

Registers the store + registry as singletons, routes the three endpoints through the registry (adding the cancel route), resumes interrupted jobs on startup, and removes the now-unused static `BulkJobs.tryStart`/`get`.

**Files:**
- Modify: `src/Nameless.TaskList/Program.fs` (DI, endpoints, resume)
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (remove static `tryStart`/`get`)
- Modify: `tests/Nameless.TaskList.Tests/EndpointTests.fs` (rewrite the background-failure test to use the registry)
- Modify: `src/Nameless.TaskList/appsettings.json` (document the new config keys)

**Interfaces:**
- Consumes: `BulkJobRegistry`, `FileSystemJobStore`, `PipelineDeps`, `processMessage` (earlier tasks); config keys `BulkJobs:StatePath`, `BulkJobs:Retain`.

- [ ] **Step 1: Rewrite the background-failure test to use the registry**

In `tests/Nameless.TaskList.Tests/EndpointTests.fs`, replace the `bulk job background failure is caught and stored` test (which currently calls `BulkJobs.tryStart`/`BulkJobs.get`) with a registry-based version:

```fsharp
[<Fact>]
let ``bulk job background failure is caught and stored`` () =
    let fake = FailingMessageSource() :> IMessageSource
    let reg = BulkJobRegistry(FakeJobStore([]), 20)
    let jobId = match reg.TryStart fake (fun _ _ -> Skipped) (System.DateTime(2026, 6, 1)) None with Ok i -> i | Error e -> failwith e
    Assert.True(pollUntil (fun () -> match reg.Get jobId with Some j -> j.Status = "error" | None -> false))
    let p = (reg.Get jobId).Value
    Assert.True(p.Error.Contains("db down"), sprintf "Expected error containing 'db down', got: %s" p.Error)
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Tests --filter "FullyQualifiedName~background failure"`
Expected: PASS already (the registry exists from Task 3). This step confirms the rewritten test is green before we remove the static module in Step 3. (If it references `pollUntil`/`FakeJobStore`/`BulkJobRegistry` and fails to compile, ensure Task 3's additions are present.)

- [ ] **Step 3: Remove the static `tryStart`/`get` from `BulkJobs`**

In `src/Nameless.TaskList/ProcessMessage.fs`, delete the module-level `jobs` dictionary, `gate`, `tryStart`, and `get` (the transitional code from Task 1, Step 4), keeping the module's `open` lines, `CancelOutcome`, and `BulkJobRegistry`. After this, no static job state remains; the registry is the only owner.

- [ ] **Step 4: Register the store and registry in DI**

In `src/Nameless.TaskList/Program.fs`, after the existing `AddSingleton<ITranscriber>` block (line 44), add:

```fsharp
        builder.Services.AddSingleton<IJobStore>(fun _ ->
            let configured = cfg.["BulkJobs:StatePath"]
            let path =
                if System.String.IsNullOrWhiteSpace configured
                then System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "bulk-jobs.json")
                else configured
            FileSystemJobStore(path) :> IJobStore) |> ignore
        builder.Services.AddSingleton<BulkJobs.BulkJobRegistry>(fun sp ->
            let store = sp.GetRequiredService<IJobStore>()
            let retain = match System.Int32.TryParse(cfg.["BulkJobs:Retain"]) with | true, v -> v | _ -> 20
            BulkJobs.BulkJobRegistry(store, retain)) |> ignore
```

`IJobStore` and `FileSystemJobStore` come from the already-opened `Nameless.TaskList.Core.BulkProcessor` / `Nameless.TaskList.Core.Adapters`.

- [ ] **Step 5: Add a shared `buildDeps` helper and reroute the endpoints**

In `src/Nameless.TaskList/Program.fs`, immediately after `let app = builder.Build()` (line 46), add a helper used by both the endpoint and startup resume:

```fsharp
        let buildDeps (messages: IMessageSource) (vault: IVault) (chat: IChatClient)
                      (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) : PipelineDeps =
            let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
            let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
            { Messages = messages; Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
              Embedder = embedder; TopK = topK; SimilarityFloor = floor; Vision = vision; Transcriber = transcriber }
```

Then replace the `/messages/process-since` POST handler (lines 80-93) and the `GET .../{jobId}` (lines 95-96) with registry-backed versions, and add the cancel route:

```fsharp
        app.MapPost("/messages/process-since", System.Func<ProcessSinceRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, ITranscriber, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessSinceRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) (registry: BulkJobs.BulkJobRegistry) ->
                match System.DateTime.TryParse(req.Since) with
                | false, _ -> Results.Json({| error = "invalid or missing 'since' date" |}, statusCode = 400)
                | true, since ->
                    let deps = buildDeps messages vault chat embedder vision transcriber
                    let processOne id jid = processMessage deps id jid
                    let chatJid = if System.String.IsNullOrWhiteSpace req.ChatJid then None else Some req.ChatJid
                    registry.TryStart messages processOne since chatJid |> BulkHandler.startToHttp)) |> ignore

        app.MapGet("/messages/process-since/{jobId}", System.Func<string, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (jobId: string) (registry: BulkJobs.BulkJobRegistry) -> registry.Get jobId |> BulkHandler.progressToHttp)) |> ignore

        app.MapPost("/messages/process-since/{jobId}/cancel", System.Func<string, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (jobId: string) (registry: BulkJobs.BulkJobRegistry) -> registry.Cancel jobId |> BulkHandler.cancelToHttp)) |> ignore
```

- [ ] **Step 6: Resume interrupted jobs on startup**

In `src/Nameless.TaskList/Program.fs`, immediately before `app.Run()` (line 98), add:

```fsharp
        // Resume any job left "running" by a previous host (interrupted by restart).
        let registry = app.Services.GetRequiredService<BulkJobs.BulkJobRegistry>()
        let resumeDeps =
            buildDeps
                (app.Services.GetRequiredService<IMessageSource>())
                (app.Services.GetRequiredService<IVault>())
                (app.Services.GetRequiredService<IChatClient>())
                (app.Services.GetRequiredService<IEmbedder>())
                (app.Services.GetRequiredService<IVision>())
                (app.Services.GetRequiredService<ITranscriber>())
        registry.Resume resumeDeps.Messages (fun id jid -> processMessage resumeDeps id jid)
```

`GetRequiredService` comes from the already-opened `Microsoft.Extensions.DependencyInjection`.

- [ ] **Step 7: Document the config keys**

In `src/Nameless.TaskList/appsettings.json`, add a `BulkJobs` section (next to the existing `Ollama`/`Vault` sections) so the keys are discoverable:

```json
  "BulkJobs": {
    "StatePath": "",
    "Retain": 20
  }
```

(An empty `StatePath` falls back to `<Vault:Root>/.taskmeister/bulk-jobs.json`.)

- [ ] **Step 8: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Nameless.TaskList/Program.fs src/Nameless.TaskList/ProcessMessage.fs src/Nameless.TaskList/appsettings.json tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: wire BulkJobRegistry into host with cancel route + startup resume

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

## Self-Review

**Spec coverage:**
- Prune completed jobs (keep latest N) → `prune` (Task 1) + `pruneAndPersist` in registry (Task 3); `BulkJobs:Retain` default 20 (Task 4). ✓
- Persistence to a single JSON file → `IJobStore` + `FileSystemJobStore` (Task 2), wired (Task 4); `BulkJobs:StatePath` default `<Vault:Root>/.taskmeister/bulk-jobs.json`. ✓
- Auto-resume interrupted job on restart → `BulkJobRegistry.Resume` (Task 3) + startup hook (Task 4). ✓
- Status string model → `BulkJob.Status` replaces `Done` (Task 1). ✓
- Cancellation: token in `runSince` (Task 1), registry `Cancel` + endpoint + 200/404/409 (Tasks 3-4). ✓
- "running" interpreted as interrupted on load; resume latest, mark others "interrupted" → `Resume` (Task 3). ✓
- Out-of-scope items (concurrent jobs, history endpoint, non-JSON backend) → not introduced. ✓

**Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows full code. ✓

**Type consistency:** `BulkJob` field set is identical across BulkProcessor, the store adapter, both test files, and the registry. `runSince` arity (`messages → processOne → job → token → onProgress`) matches every call site (Task 1 tests, registry `runJob`). `CancelOutcome` cases (`Cancelled`/`UnknownJob`/`AlreadyTerminal`) match between registry, handler, and tests. Status strings (`running`/`done`/`cancelled`/`interrupted`/`error`) used verbatim throughout. ✓

**Compile-order check:** `IJobStore` is added to `BulkProcessor.fs` (after `BulkJob`), not `Ports.fs`, satisfying the constraint that it compiles after the type it references; `FileSystemJobStore` is in `Adapters.fs` (compiles after `BulkProcessor.fs`). ✓
