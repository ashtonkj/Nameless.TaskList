# In-App Scheduler Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the daily digest, weekly digest, and reindex on a fixed-time schedule from inside the app (with catch-up for missed runs), so the external n8n cron can be removed.

**Architecture:** Pure scheduling logic in Core (parse fixed-time config → decide which tasks are due via a per-task last-run state), a JSON state store, and a gated `BackgroundService` that runs due tasks by calling the existing `Digest.generate` / `Indexer.regenerate` through a small shared runner the HTTP endpoints also use. Mirrors the IMAP poller / WhatsApp listener pattern.

**Tech Stack:** F# / .NET 10, System.Text.Json, xUnit 2.9.2, ASP.NET Core hosted service.

## Global Constraints

- Target framework `net10.0` (all projects).
- Times are server-local via `System.DateTime.Now` (deployment is SAST +02:00), consistent with `Indexer`/`Digest` which already use `DateTime.Now`.
- No new dependencies (no cron library).
- Config strings are tolerant: blank / missing / unparseable → that task is disabled (logged once at startup); parsing never throws.
- The scheduler is OFF unless `Scheduler:Enabled = "true"` (off by default and in tests).
- `SchedulerState` is serialized to JSON → must be a PUBLIC record (a private record serializes to `{}` — `fsharp-private-record-serialization` lesson).
- Catch-up is safe because outputs are idempotent: a daily digest re-run overwrites `digests/YYYY-MM-DD-daily.md`; reindex regenerates index files.
- The shared-runner extraction must be behavior-preserving: the `/reindex`, `/digest/daily`, `/digest/weekly` endpoints keep producing the same HTTP results.
- F# compile order is significant: new `Compile` entries go at the stated position; a type must be defined before use.
- Default `dotnet test` stays offline and starts no scheduler. Build (`dotnet build`) and full suite (`dotnet test`) pass at the end of every task.

---

## File Structure

- `src/Nameless.TaskList.Core/Library.fs` — add `SchedulerState` (serialized; referenced by the port).
- `src/Nameless.TaskList.Core/Scheduler.fs` — **new** (after `Digest.fs`): `ScheduleSpec`, `ScheduledTask`, `parseSpec`, `mostRecentOccurrence`, `dueTasks`, `tick`.
- `src/Nameless.TaskList.Core/Ports.fs` — add `ISchedulerStateStore`.
- `src/Nameless.TaskList.Core/Adapters.fs` — add `FileSystemSchedulerStateStore`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — register `Scheduler.fs` after `Digest.fs`.
- `src/Nameless.TaskList/Scheduler.fs` — **new**: `MaintenanceTasks` (shared digest/reindex runner) + `SchedulerService : BackgroundService`.
- `src/Nameless.TaskList/Program.fs` — endpoints call `MaintenanceTasks`; build the task list from config; register `SchedulerService` when enabled.
- `src/Nameless.TaskList/Nameless.TaskList.fsproj` — register the host `Scheduler.fs` before `Program.fs`.
- `src/Nameless.TaskList/appsettings.json` — `Scheduler` section.
- Tests — `SchedulerTests.fs` (Core.Tests).

---

## Task 1: ScheduleSpec + parse + most-recent-occurrence (pure)

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add `SchedulerState` after `ListenCursor`)
- Create: `src/Nameless.TaskList.Core/Scheduler.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`
- Test: `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs` (new; register in test fsproj)

**Interfaces:**
- Produces:
  - `SchedulerState = { LastRuns: System.Collections.Generic.Dictionary<string, System.DateTime> }`
  - `ScheduleSpec = Daily of hour:int * minute:int | Weekly of day:System.DayOfWeek * hour:int * minute:int`
  - `Scheduler.parseSpec : string -> ScheduleSpec option`
  - `Scheduler.mostRecentOccurrence : now:System.DateTime -> ScheduleSpec -> System.DateTime`

- [ ] **Step 1: Add `SchedulerState` to Library.fs**

In `src/Nameless.TaskList.Core/Library.fs`, after the `ListenCursor` type add:

```fsharp
/// Per-task last-run timestamps for the in-app scheduler. Serialized to JSON — keep public.
type SchedulerState = { LastRuns: System.Collections.Generic.Dictionary<string, System.DateTime> }
```

- [ ] **Step 2: Register the new source + test files**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add immediately AFTER the `Digest.fs` line:

```xml
        <Compile Include="Digest.fs" />
        <Compile Include="Scheduler.fs" />
```

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add `SchedulerTests.fs` to the `Compile` items (next to the other `*Tests.fs`).

- [ ] **Step 3: Write the failing parse + occurrence tests**

Create `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.SchedulerTests

open System
open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``parseSpec reads a daily HH:mm`` () =
    Assert.Equal(Some(Scheduler.Daily(7, 0)), Scheduler.parseSpec "07:00")

[<Fact>]
let ``parseSpec reads a weekly Ddd HH:mm case-insensitively`` () =
    Assert.Equal(Some(Scheduler.Weekly(DayOfWeek.Monday, 7, 0)), Scheduler.parseSpec "mon 07:00")

[<Fact>]
let ``parseSpec rejects blank, garbage and out-of-range`` () =
    Assert.Equal(None, Scheduler.parseSpec "")
    Assert.Equal(None, Scheduler.parseSpec "   ")
    Assert.Equal(None, Scheduler.parseSpec null)
    Assert.Equal(None, Scheduler.parseSpec "garbage")
    Assert.Equal(None, Scheduler.parseSpec "25:00")
    Assert.Equal(None, Scheduler.parseSpec "Xyz 07:00")

[<Fact>]
let ``mostRecentOccurrence daily returns today when the time has passed`` () =
    let now = DateTime(2026, 6, 28, 9, 0, 0)
    Assert.Equal(DateTime(2026, 6, 28, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Daily(7, 0)))

[<Fact>]
let ``mostRecentOccurrence daily returns yesterday when the time has not passed`` () =
    let now = DateTime(2026, 6, 28, 6, 0, 0)
    Assert.Equal(DateTime(2026, 6, 27, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Daily(7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on the day after the time returns today`` () =
    // 2026-06-29 is a Monday
    let now = DateTime(2026, 6, 29, 9, 0, 0)
    Assert.Equal(DateTime(2026, 6, 29, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on the day before the time returns last week`` () =
    let now = DateTime(2026, 6, 29, 6, 0, 0)   // Monday, before 07:00
    Assert.Equal(DateTime(2026, 6, 22, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))

[<Fact>]
let ``mostRecentOccurrence weekly on a different weekday returns the most recent matching day`` () =
    let now = DateTime(2026, 7, 1, 12, 0, 0)   // Wednesday
    Assert.Equal(DateTime(2026, 6, 29, 7, 0, 0), Scheduler.mostRecentOccurrence now (Scheduler.Weekly(DayOfWeek.Monday, 7, 0)))
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: FAIL — `Scheduler` module not defined.

- [ ] **Step 5: Implement `Scheduler.fs` (types + parse + occurrence)**

Create `src/Nameless.TaskList.Core/Scheduler.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System

module Scheduler =

    /// A parsed schedule for one task.
    type ScheduleSpec =
        | Daily of hour:int * minute:int
        | Weekly of day:DayOfWeek * hour:int * minute:int

    /// One schedulable operation. Name is the state key + log label.
    type ScheduledTask = { Name: string; Spec: ScheduleSpec }

    let private parseHm (hm: string) : (int * int) option =
        match hm.Split(':') with
        | [| h; m |] ->
            match Int32.TryParse h, Int32.TryParse m with
            | (true, hh), (true, mm) when hh >= 0 && hh <= 23 && mm >= 0 && mm <= 59 -> Some(hh, mm)
            | _ -> None
        | _ -> None

    let private parseDay (d: string) : DayOfWeek option =
        match d.Trim().ToLowerInvariant() with
        | "mon" -> Some DayOfWeek.Monday
        | "tue" -> Some DayOfWeek.Tuesday
        | "wed" -> Some DayOfWeek.Wednesday
        | "thu" -> Some DayOfWeek.Thursday
        | "fri" -> Some DayOfWeek.Friday
        | "sat" -> Some DayOfWeek.Saturday
        | "sun" -> Some DayOfWeek.Sunday
        | _ -> None

    /// Parse a config time string. "07:00" -> Daily; "Mon 07:00" -> Weekly; anything else -> None.
    let parseSpec (s: string) : ScheduleSpec option =
        if String.IsNullOrWhiteSpace s then None
        else
            match s.Trim().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
            | [| hm |] -> parseHm hm |> Option.map (fun (h, m) -> Daily(h, m))
            | [| d; hm |] ->
                match parseDay d, parseHm hm with
                | Some dw, Some(h, m) -> Some(Weekly(dw, h, m))
                | _ -> None
            | _ -> None

    /// The latest scheduled datetime <= now.
    let mostRecentOccurrence (now: DateTime) (spec: ScheduleSpec) : DateTime =
        match spec with
        | Daily(h, m) ->
            let today = DateTime(now.Year, now.Month, now.Day, h, m, 0)
            if today <= now then today else today.AddDays(-1.0)
        | Weekly(d, h, m) ->
            // Walk back day by day from today at h:m to the most recent matching weekday <= now.
            let rec back (candidate: DateTime) =
                if candidate.DayOfWeek = d && candidate <= now then candidate
                else back (candidate.AddDays(-1.0))
            back (DateTime(now.Year, now.Month, now.Day, h, m, 0))
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS.

- [ ] **Step 7: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: scheduler ScheduleSpec + parse + most-recent-occurrence"
```

---

## Task 2: dueTasks + tick (pure)

**Files:**
- Modify: `src/Nameless.TaskList.Core/Scheduler.fs` (add `dueTasks`, `tick`)
- Test: `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs`

**Interfaces:**
- Consumes: `ScheduledTask`, `SchedulerState`, `mostRecentOccurrence`.
- Produces:
  - `Scheduler.dueTasks : now:System.DateTime -> ScheduledTask list -> SchedulerState -> ScheduledTask list`
  - `Scheduler.tick : now:System.DateTime -> ScheduledTask list -> SchedulerState -> run:(ScheduledTask -> unit) -> SchedulerState`

- [ ] **Step 1: Write the failing dueTasks/tick tests**

Append to `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs`:

```fsharp
let private stateOf (pairs: (string * DateTime) list) : SchedulerState =
    { LastRuns = System.Collections.Generic.Dictionary(dict pairs) }

let private daily name h m : Scheduler.ScheduledTask = { Name = name; Spec = Scheduler.Daily(h, m) }

[<Fact>]
let ``dueTasks includes a task never run whose slot has passed`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let due = Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] (stateOf [])
    Assert.Equal<string list>([ "daily-digest" ], due |> List.map (fun t -> t.Name))

[<Fact>]
let ``dueTasks excludes a task already run this slot`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let state = stateOf [ "daily-digest", DateTime(2026, 6, 28, 7, 30, 0) ]   // ran after 07:00 today
    Assert.Empty(Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] state)

[<Fact>]
let ``dueTasks fires once after multi-day downtime (catch-up, not repeated)`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let state = stateOf [ "daily-digest", DateTime(2026, 6, 25, 7, 0, 0) ]    // last ran 3 days ago
    let due = Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] state
    Assert.Equal(1, List.length due)                                          // due once
    // after running, the same now is no longer due
    let after = Scheduler.tick now [ daily "daily-digest" 7 0 ] state (fun _ -> ())
    Assert.Empty(Scheduler.dueTasks now [ daily "daily-digest" 7 0 ] after)

[<Fact>]
let ``tick runs due tasks and advances only their last-run`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let tasks = [ daily "daily-digest" 7 0; daily "reindex" 9 0 ]   // reindex 09:00 not yet due at 08:00
    let ran = ResizeArray<string>()
    let after = Scheduler.tick now tasks (stateOf []) (fun t -> ran.Add t.Name)
    Assert.Equal<string list>([ "daily-digest" ], List.ofSeq ran)
    Assert.True(after.LastRuns.ContainsKey "daily-digest")
    Assert.False(after.LastRuns.ContainsKey "reindex")
    Assert.Equal(now, after.LastRuns.["daily-digest"])

[<Fact>]
let ``tick does not mutate the input state`` () =
    let now = DateTime(2026, 6, 28, 8, 0, 0)
    let input = stateOf []
    Scheduler.tick now [ daily "daily-digest" 7 0 ] input (fun _ -> ()) |> ignore
    Assert.Empty(input.LastRuns)   // original untouched
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: FAIL — `dueTasks` / `tick` not defined.

- [ ] **Step 3: Implement `dueTasks` and `tick`**

In `src/Nameless.TaskList.Core/Scheduler.fs`, inside `module Scheduler`, after `mostRecentOccurrence` add:

```fsharp
    let private lastRunOf (state: SchedulerState) (name: string) : DateTime =
        match state.LastRuns.TryGetValue name with
        | true, v -> v
        | _ -> DateTime.MinValue

    /// Tasks whose most-recent scheduled slot is newer than their recorded last run.
    let dueTasks (now: DateTime) (tasks: ScheduledTask list) (state: SchedulerState) : ScheduledTask list =
        tasks |> List.filter (fun t -> lastRunOf state t.Name < mostRecentOccurrence now t.Spec)

    /// Run each due task via `run`, returning a NEW state with those tasks' last-run set to `now`.
    /// Does not mutate the input. `run` is expected to swallow its own failures (the host service
    /// wraps each task) so one failure never aborts the others; last-run advances regardless, so a
    /// failed run simply waits for its next slot rather than retrying every tick.
    let tick (now: DateTime) (tasks: ScheduledTask list) (state: SchedulerState) (run: ScheduledTask -> unit) : SchedulerState =
        let due = dueTasks now tasks state
        for t in due do run t
        let updated = System.Collections.Generic.Dictionary(state.LastRuns)
        for t in due do updated.[t.Name] <- now
        { LastRuns = updated }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS.

- [ ] **Step 5: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: scheduler dueTasks + tick"
```

---

## Task 3: State store port + adapter

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `ISchedulerStateStore`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `FileSystemSchedulerStateStore`)
- Test: `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs`

**Interfaces:**
- Consumes: `SchedulerState`.
- Produces:
  - `ISchedulerStateStore.Load : unit -> SchedulerState` / `Save : SchedulerState -> unit`
  - `Adapters.FileSystemSchedulerStateStore(path: string) : ISchedulerStateStore`

- [ ] **Step 1: Add the port**

In `src/Nameless.TaskList.Core/Ports.fs`, after `IListenCursorStore` add:

```fsharp
/// Persists the in-app scheduler's per-task last-run state.
type ISchedulerStateStore =
    abstract member Load : unit -> SchedulerState
    abstract member Save : state: SchedulerState -> unit
```

- [ ] **Step 2: Write the failing store test**

Append to `tests/Nameless.TaskList.Core.Tests/SchedulerTests.fs`:

```fsharp
open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters

[<Fact>]
let ``FileSystemSchedulerStateStore round-trips, empty when missing`` () =
    let path = Path.Combine(Path.GetTempPath(), "sched-" + Guid.NewGuid().ToString("N") + ".json")
    try
        let store = FileSystemSchedulerStateStore(path) :> ISchedulerStateStore
        Assert.Empty((store.Load()).LastRuns)
        let s = { LastRuns = System.Collections.Generic.Dictionary(dict [ "daily-digest", DateTime(2026, 6, 28, 7, 0, 0) ]) }
        store.Save s
        let reloaded = (FileSystemSchedulerStateStore(path) :> ISchedulerStateStore).Load()
        Assert.Equal(DateTime(2026, 6, 28, 7, 0, 0), reloaded.LastRuns.["daily-digest"])
    finally
        (try File.Delete path with _ -> ())
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: FAIL — `FileSystemSchedulerStateStore` not defined.

- [ ] **Step 4: Implement the adapter**

In `src/Nameless.TaskList.Core/Adapters.fs`, inside `module Adapters` (near `FileSystemListenCursorStore`), add:

```fsharp
    // ---- Scheduler last-run state over a single JSON file. ----
    type FileSystemSchedulerStateStore(path: string) =
        interface ISchedulerStateStore with
            member _.Save(state) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                File.WriteAllText(path, JsonSerializer.Serialize(state))
            member _.Load() =
                try
                    if File.Exists path then
                        match JsonSerializer.Deserialize<SchedulerState>(File.ReadAllText path) with
                        | s when not (obj.ReferenceEquals(s.LastRuns, null)) -> s
                        | _ -> { LastRuns = System.Collections.Generic.Dictionary() }
                    else { LastRuns = System.Collections.Generic.Dictionary() }
                with _ -> { LastRuns = System.Collections.Generic.Dictionary() }
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS.

- [ ] **Step 6: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: scheduler state store port + filesystem adapter"
```

---

## Task 4: Shared maintenance runner (behavior-preserving endpoint refactor)

Extract the digest/reindex invocation the endpoints do into a reusable `MaintenanceTasks` module the scheduler will also call, and rewire the existing endpoints to use it. No behavior change to the endpoints.

**Files:**
- Create: `src/Nameless.TaskList/Scheduler.fs` (the `MaintenanceTasks` module; the `SchedulerService` is added in Task 5)
- Modify: `src/Nameless.TaskList/Nameless.TaskList.fsproj` (register before `Program.fs`)
- Modify: `src/Nameless.TaskList/Program.fs:137-166` (endpoints call `MaintenanceTasks`)

**Interfaces:**
- Consumes: `Digest.generate`, `Digest.DigestParams`, `Digest.DigestResult`, `Indexer.regenerate`, `Indexer.TopicSweepConfig`, `Indexer.IndexSummary`, `IVault`, `IChatClient`, `IConfiguration`.
- Produces:
  - `MaintenanceTasks.reindex : IConfiguration -> IVault -> Indexer.IndexSummary`
  - `MaintenanceTasks.digest : IConfiguration -> IVault -> IChatClient -> Digest.DigestParams -> Digest.DigestResult`

- [ ] **Step 1: Create the runner module**

Create `src/Nameless.TaskList/Scheduler.fs`:

```fsharp
namespace Nameless.TaskList

open System
open Microsoft.Extensions.Configuration
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports

/// The digest/reindex invocations shared by the HTTP endpoints and the in-app scheduler,
/// so a manual run and a scheduled run execute identical code reading the same config.
module MaintenanceTasks =

    let private topicCfg (cfg: IConfiguration) : Indexer.TopicSweepConfig =
        { ResolvedArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:ResolvedArchiveAfterDays"]) with | true, n -> n | _ -> 14)
          DormantArchiveAfterDays = (match Int32.TryParse(cfg.["Topics:DormantArchiveAfterDays"]) with | true, n -> n | _ -> 90) }

    let reindex (cfg: IConfiguration) (vault: IVault) : Indexer.IndexSummary =
        Indexer.regenerate vault (topicCfg cfg) DateTime.Now

    let digest (cfg: IConfiguration) (vault: IVault) (chat: IChatClient) (p: Digest.DigestParams) : Digest.DigestResult =
        Digest.generate { Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]; Today = DateTime.Now } p
```

- [ ] **Step 2: Register the file before `Program.fs`**

In `src/Nameless.TaskList/Nameless.TaskList.fsproj`, add immediately BEFORE the `Program.fs` line:

```xml
        <Compile Include="Scheduler.fs" />
        <Compile Include="Program.fs"/>
```

- [ ] **Step 3: Rewire the `/reindex` endpoint**

In `src/Nameless.TaskList/Program.fs`, replace the `/reindex` handler body (currently lines ~137-143) so it uses the runner:

```fsharp
        app.MapPost("/reindex", System.Func<IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) ->
                try MaintenanceTasks.reindex cfg vault |> ReindexHandler.toHttp
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

- [ ] **Step 4: Rewire the digest endpoints**

In `src/Nameless.TaskList/Program.fs`, replace the local `runDigest` helper (currently lines ~155-160) with one that calls the runner:

```fsharp
        let runDigest (vault: IVault) (chat: IChatClient) (p: Digest.DigestParams) : Microsoft.AspNetCore.Http.IResult =
            try MaintenanceTasks.digest cfg vault chat p |> DigestHandler.toHttp
            with ex -> Results.Json({| error = ex.Message |}, statusCode = 500)
```

Leave the two `app.MapPost("/digest/daily", ...)` / `("/digest/weekly", ...)` registrations unchanged — they already call `runDigest vault chat Digest.DigestParams.daily/weekly`.

- [ ] **Step 5: Build + full suite (behavior-preserving check)**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass (the endpoint→HTTP mapping tests in `tests/Nameless.TaskList.Tests` still pass — the endpoints produce the same results).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: shared MaintenanceTasks runner for digest/reindex endpoints"
```

---

## Task 5: SchedulerService + wiring

**Files:**
- Modify: `src/Nameless.TaskList/Scheduler.fs` (add `SchedulerService`)
- Modify: `src/Nameless.TaskList/Program.fs` (build task list, register service when enabled)
- Modify: `src/Nameless.TaskList/appsettings.json` (`Scheduler` section)

**Interfaces:**
- Consumes: `Scheduler.tick`, `Scheduler.parseSpec`, `Scheduler.ScheduledTask`, `ISchedulerStateStore`, `MaintenanceTasks`, `Adapters.FileSystemSchedulerStateStore`.
- Produces: `SchedulerService : BackgroundService`.

- [ ] **Step 1: Add the `Scheduler` config section**

In `src/Nameless.TaskList/appsettings.json`, add a top-level `"Scheduler"` object:

```json
  "Scheduler": {
    "Enabled": false,
    "DailyDigest": "07:00",
    "WeeklyDigest": "Mon 07:00",
    "Reindex": "03:00",
    "CheckIntervalSeconds": 60
  }
```

- [ ] **Step 2: Add the `SchedulerService` to the host Scheduler.fs**

In `src/Nameless.TaskList/Scheduler.fs`, append (after the `MaintenanceTasks` module; add the needed opens at the top of the file: `open System.Threading`, `open System.Threading.Tasks`, `open Microsoft.Extensions.Hosting`, `open Microsoft.Extensions.Logging`):

```fsharp
/// Runs due scheduled tasks on a timer. Registered only when Scheduler:Enabled = "true".
/// `runTask` (built in Program.fs) dispatches by task name and swallows its own failures so one
/// failing task never aborts the tick or the others.
type SchedulerService
    (tasks: Scheduler.ScheduledTask list, stateStore: ISchedulerStateStore,
     runTask: Scheduler.ScheduledTask -> unit, checkSeconds: int,
     logger: ILogger<SchedulerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try
                    let state = stateStore.Load()
                    let next = Scheduler.tick DateTime.Now tasks state runTask
                    stateStore.Save next
                with ex ->
                    logger.LogWarning(ex, "scheduler tick failed")
                try do! Task.Delay(TimeSpan.FromSeconds(float checkSeconds), stoppingToken)
                with :? OperationCanceledException -> ()
        } :> Task
```

- [ ] **Step 3: Wire it in `Program.fs`**

In `src/Nameless.TaskList/Program.fs`, after the WhatsApp listener registration block and before `let app = builder.Build()`, add (READ the file first to confirm `buildDeps` is not needed here — the scheduler uses `MaintenanceTasks` directly, not `PipelineDeps`; it only needs `IVault`/`IChatClient` and `cfg`):

```fsharp
        // Scheduled maintenance: register the in-app scheduler only when enabled.
        if cfg.["Scheduler:Enabled"] = "true" then
            builder.Services.AddHostedService<SchedulerService>(fun sp ->
                let tasks =
                    [ "daily-digest",  cfg.["Scheduler:DailyDigest"]
                      "weekly-digest", cfg.["Scheduler:WeeklyDigest"]
                      "reindex",       cfg.["Scheduler:Reindex"] ]
                    |> List.choose (fun (name, s) ->
                        Scheduler.parseSpec s |> Option.map (fun spec -> ({ Name = name; Spec = spec } : Scheduler.ScheduledTask)))
                let checkSeconds = match System.Int32.TryParse(cfg.["Scheduler:CheckIntervalSeconds"]) with | true, n -> n | _ -> 60
                let statePath = System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "scheduler-state.json")
                let stateStore = FileSystemSchedulerStateStore(statePath) :> ISchedulerStateStore
                let vault = sp.GetRequiredService<IVault>()
                let chat = sp.GetRequiredService<IChatClient>()
                let logger = sp.GetRequiredService<ILogger<SchedulerService>>()
                let enabledNames = tasks |> List.map (fun t -> t.Name) |> String.concat ", "
                logger.LogInformation("Scheduler enabled; tasks: {Tasks}", (if enabledNames = "" then "(none)" else enabledNames))
                let runTask (t: Scheduler.ScheduledTask) =
                    try
                        match t.Name with
                        | "daily-digest"  -> MaintenanceTasks.digest cfg vault chat Digest.DigestParams.daily |> ignore
                        | "weekly-digest" -> MaintenanceTasks.digest cfg vault chat Digest.DigestParams.weekly |> ignore
                        | "reindex"       -> MaintenanceTasks.reindex cfg vault |> ignore
                        | other           -> logger.LogWarning("unknown scheduled task {Name}", other)
                        logger.LogInformation("ran scheduled task {Name}", t.Name)
                    with ex ->
                        logger.LogWarning(ex, "scheduled task {Name} failed", t.Name)
                new SchedulerService(tasks, stateStore, runTask, checkSeconds, logger)) |> ignore
```

(Confirm `open Microsoft.Extensions.Logging` is present in `Program.fs` — it was added for the IMAP poller. `Digest` is in `Nameless.TaskList.Core`, already opened.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: build succeeds, 0 warnings. (No new unit test — the tick/parse logic is covered by Tasks 1-2; this is composition. `new SchedulerService(...)` uses `new` because BackgroundService is IDisposable, matching the other services.)

- [ ] **Step 5: Full suite (confirm scheduler stays off in tests)**

Run: `dotnet test`
Expected: all tests pass; no scheduler starts (`Scheduler:Enabled` defaults false).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: SchedulerService background scheduler + config wiring"
```

---

## Self-Review

**Spec coverage:**
- Fixed-time config (`HH:mm` daily/reindex, `Ddd HH:mm` weekly; blank = disabled) → Task 1 (`parseSpec`), Task 5 (config + task list). ✓
- Catch-up via per-task last-run → Task 1 (`mostRecentOccurrence`), Task 2 (`dueTasks`/`tick`), Task 3 (store). ✓
- `Scheduler:Enabled` gate, off by default → Task 5. ✓
- Pure logic in Core; `SchedulerState` in Library.fs (public, serialized) → Tasks 1-2. ✓
- State store port + filesystem adapter → Task 3. ✓
- Gated BackgroundService, per-task try/catch isolation, clean cancellation, CheckIntervalSeconds → Task 5. ✓
- Shared runner so endpoints + scheduler share code, behavior-preserving → Task 4. ✓
- Idempotency makes catch-up safe (digests date-stamped, reindex idempotent) → relied on, not code. ✓
- Tests: parseSpec, mostRecentOccurrence (daily/weekly/off-day), dueTasks/tick (due, already-run, catch-up-once, no-mutation), state-store round-trip → Tasks 1-3. ✓
- Out of scope (cron, sub-minute, distributed lock, retry/backoff, new task types) → not built. ✓

**Placeholder scan:** No TBD/TODO; every code step has complete code. Program.fs line numbers are approximate ("~137-166") but the exact surrounding code is quoted for unambiguous replacement.

**Type consistency:** `SchedulerState.LastRuns : Dictionary<string,DateTime>` (Task 1) used in Tasks 2-3, 5. `ScheduleSpec`/`ScheduledTask` (Task 1) used in Tasks 2, 5. `dueTasks`/`tick` signatures (Task 2) match the `tick` call in `SchedulerService` (Task 5). `ISchedulerStateStore` (Task 3) matches the adapter (Task 3) and the service (Task 5). `MaintenanceTasks.reindex`/`digest` (Task 4) match the endpoint rewrites (Task 4) and `runTask` (Task 5). Task names `"daily-digest"`/`"weekly-digest"`/`"reindex"` are identical in Task 5's task-list construction and `runTask` dispatch.
