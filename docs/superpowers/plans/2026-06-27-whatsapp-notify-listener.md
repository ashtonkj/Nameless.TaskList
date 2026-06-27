# WhatsApp LISTEN/NOTIFY Listener Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the WhatsApp channel self-triggering — the app `LISTEN`s for the bridge's `NOTIFY whatsapp_new_message` and runs `Pipeline.processMessage` automatically, with a startup/reconnect catch-up so downtime gaps aren't lost.

**Architecture:** Mirror the IMAP channel. Pure logic in Core behind ports (`parse`, `catchUp`, `runSession`), an Npgsql adapter for the live `LISTEN` connection, a `FileSystem` cursor store, and a gated `BackgroundService` in the host. The pipeline is unchanged; dedup is the existing message-file idempotency guard.

**Tech Stack:** F# / .NET 10, Npgsql 9.0.2 (LISTEN/NOTIFY), System.Text.Json, xUnit 2.9.2 + Xunit.SkippableFact, ASP.NET Core hosted service.

## Global Constraints

- Target framework `net10.0` (all projects).
- KB timestamps are SAST wall-clock, offset `+02:00` (DESIGN §4/§8). The cursor is a `DateTime` in that same SAST wall-clock so catch-up via `GetMessagesSince` matches the existing bulk `process-since` path. Convert a payload's `DateTimeOffset` with `.ToOffset(TimeSpan.FromHours 2.0).DateTime`.
- The bridge's NOTIFY channel is exactly `whatsapp_new_message`; payload is JSON with at least `id` and `chat_jid` (strings) and `timestamp` (ISO-8601 with offset).
- No secrets in source; reuse `ConnectionStrings:WhatsApp`. The listener is OFF unless `WhatsApp:Listen:Enabled = "true"` (off by default and in tests).
- Types serialized to JSON must be PUBLIC records, never `private` (a private record serializes to `{}` — `fsharp-private-record-serialization` lesson). `ListenCursor` is serialized.
- All parsing is `Result`/`option`-returning and never throws on bad input (`parse` returns `None`); failures are logged by the caller (failure-legibility).
- All vault/file writes via the existing adapters; pipeline never deletes.
- F# compile order is significant: new `Compile` entries go at the stated position; a type must be defined before use.
- Default `dotnet test` stays fully offline and starts no listener. Live DB coverage is opt-in in `Nameless.TaskList.IntegrationTests` (`-p:Integration=true`) and skips when the DB is absent.
- Build (`dotnet build`) and full suite (`dotnet test`) must pass at the end of every task.

---

## File Structure

- `src/Nameless.TaskList.Core/Library.fs` — add `NotifyPayload`, `ListenCursor`.
- `src/Nameless.TaskList.Core/Ports.fs` — add `INotificationListener`, `IListenCursorStore`.
- `src/Nameless.TaskList.Core/WhatsAppListener.fs` — **new** (after `Pipeline.fs`): `parse`, `catchUp`, `runSession`.
- `src/Nameless.TaskList.Core/Adapters.fs` — add `FileSystemListenCursorStore`, `NpgsqlNotificationListener`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — register `WhatsAppListener.fs` after `Pipeline.fs`.
- `src/Nameless.TaskList/WhatsAppListener.fs` — **new**: `WhatsAppListenerService : BackgroundService` (registered before `Program.fs`).
- `src/Nameless.TaskList/Program.fs` — register the service when `WhatsApp:Listen:Enabled = "true"`.
- `src/Nameless.TaskList/appsettings.json` — `WhatsApp:Listen` section.
- Tests — `WhatsAppListenerTests.fs` (Core.Tests); live test in IntegrationTests.

---

## Task 1: Payload types + pure `parse`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add records after `EmailCursor`)
- Create: `src/Nameless.TaskList.Core/WhatsAppListener.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`
- Test: `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs` (new; register in test fsproj)

**Interfaces:**
- Produces:
  - `NotifyPayload = { Id: string; ChatJid: string; Timestamp: System.DateTimeOffset }`
  - `ListenCursor = { Since: System.DateTime }`
  - `WhatsAppListener.parse : string -> NotifyPayload option`

- [ ] **Step 1: Add the records**

In `src/Nameless.TaskList.Core/Library.fs`, after the `EmailCursor` type add:

```fsharp
/// A decoded `whatsapp_new_message` NOTIFY payload. Only Id/ChatJid drive the pipeline;
/// Timestamp advances the catch-up cursor.
type NotifyPayload = { Id: string; ChatJid: string; Timestamp: System.DateTimeOffset }

/// Persisted catch-up cursor for the WhatsApp listener: the last-processed message time
/// (SAST wall-clock). Serialized to JSON — keep public.
type ListenCursor = { Since: System.DateTime }
```

- [ ] **Step 2: Register the new source + test files**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add immediately AFTER the `Pipeline.fs` line:

```xml
        <Compile Include="Pipeline.fs" />
        <Compile Include="WhatsAppListener.fs" />
```

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add `WhatsAppListenerTests.fs` to the `Compile` items (next to `EmailPollerTests.fs`).

- [ ] **Step 3: Write the failing parse tests**

Create `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.WhatsAppListenerTests

open System
open Nameless.TaskList.Core
open Xunit

let private sample =
    """{"id":"ACCC08","chat_jid":"123@newsletter","sender":"123","is_from_me":false,"media_type":"","album_id":null,"timestamp":"2026-06-18T13:08:11+02:00"}"""

[<Fact>]
let ``parse reads id, chat_jid and timestamp from a valid payload`` () =
    match WhatsAppListener.parse sample with
    | Some p ->
        Assert.Equal("ACCC08", p.Id)
        Assert.Equal("123@newsletter", p.ChatJid)
        Assert.Equal(DateTimeOffset(2026, 6, 18, 13, 8, 11, TimeSpan.FromHours 2.0), p.Timestamp)
    | None -> failwith "expected Some"

[<Fact>]
let ``parse returns None on malformed json`` () =
    Assert.Equal(None, WhatsAppListener.parse "{not json")

[<Fact>]
let ``parse returns None when id is missing`` () =
    Assert.Equal(None, WhatsAppListener.parse """{"chat_jid":"x@s"}""")

[<Fact>]
let ``parse returns None when chat_jid is blank`` () =
    Assert.Equal(None, WhatsAppListener.parse """{"id":"a","chat_jid":"  "}""")

[<Fact>]
let ``parse tolerates a missing timestamp`` () =
    match WhatsAppListener.parse """{"id":"a","chat_jid":"x@s"}""" with
    | Some p -> Assert.Equal(DateTimeOffset.MinValue, p.Timestamp)
    | None -> failwith "expected Some"
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: FAIL — `WhatsAppListener` module not defined.

- [ ] **Step 5: Implement `parse` in the new file**

Create `src/Nameless.TaskList.Core/WhatsAppListener.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System
open System.Text.Json
open Nameless.TaskList.Core.Ports

module WhatsAppListener =

    /// Decode a `whatsapp_new_message` NOTIFY payload. Tolerant: returns None on invalid JSON
    /// or when id/chat_jid are missing or blank. Never throws.
    let parse (json: string) : NotifyPayload option =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let str name =
                match root.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> null
            let id = str "id"
            let jid = str "chat_jid"
            if String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace jid then None
            else
                let ts =
                    match DateTimeOffset.TryParse(str "timestamp") with
                    | true, d -> d
                    | _ -> DateTimeOffset.MinValue
                Some { Id = id; ChatJid = jid; Timestamp = ts }
        with _ -> None
```

(The `open Nameless.TaskList.Core.Ports` is unused in this task but is needed by Tasks 2–3 added to this same module; include it now so the file compiles unchanged later.)

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: PASS (5 tests).

- [ ] **Step 7: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: WhatsApp NOTIFY payload types + pure parse"
```

---

## Task 2: `catchUp` driver

**Files:**
- Modify: `src/Nameless.TaskList.Core/WhatsAppListener.fs` (add `catchUp`)
- Test: `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`

**Interfaces:**
- Consumes: `IMessageSource.GetMessagesSince : chatJid: string option * since: System.DateTime -> ChatMessage list`; `ListenCursor`.
- Produces: `WhatsAppListener.catchUp : IMessageSource -> (string -> string -> unit) -> ListenCursor -> ListenCursor`

- [ ] **Step 1: Write the failing catch-up tests**

Append to `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`:

```fsharp
open Nameless.TaskList.Core.Ports

// IMessageSource fake whose GetMessagesSince returns a fixed list and records the `since` asked for.
type FakeSince(sinceList: ChatMessage list) =
    member val AskedSince = DateTime.MinValue with get, set
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = None
        member _.GetRecent(_jid, _before, _ex) = []
        member this.GetMessagesSince(_chatJid, since) =
            this.AskedSince <- since
            sinceList
        member _.GetMediaBytes(_id, _jid) = None

let private msg id (ts: DateTime) : ChatMessage =
    { Id = id; ChatJid = "c@s"; ChatName = "C"; NormalizedChatName = "C"; IsGroup = false
      SenderId = "c"; SenderName = "C"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Platform = "whatsapp-direct"; IsBroadcast = false
      Content = "hi"; MediaType = null; FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = ts }

[<Fact>]
let ``catchUp processes since-cursor messages and advances to the latest timestamp`` () =
    let t1 = DateTime(2026, 6, 18, 10, 0, 0)
    let t2 = DateTime(2026, 6, 18, 11, 0, 0)
    let mb = FakeSince([ msg "<a>" t1; msg "<b>" t2 ])
    let seen = ResizeArray<string>()
    let next = WhatsAppListener.catchUp mb (fun id _ -> seen.Add id) { Since = t1 }
    Assert.Equal(t1, mb.AskedSince)                  // queried from the stored cursor
    Assert.Equal<string list>([ "<a>"; "<b>" ], List.ofSeq seen)
    Assert.Equal(t2, next.Since)                      // advanced to the latest processed

[<Fact>]
let ``catchUp on no new messages leaves the cursor unchanged`` () =
    let t = DateTime(2026, 6, 18, 10, 0, 0)
    let mb = FakeSince([])
    let seen = ResizeArray<string>()
    let next = WhatsAppListener.catchUp mb (fun id _ -> seen.Add id) { Since = t }
    Assert.Empty(seen)
    Assert.Equal(t, next.Since)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: FAIL — `catchUp` not defined.

- [ ] **Step 3: Implement `catchUp`**

In `src/Nameless.TaskList.Core/WhatsAppListener.fs`, inside `module WhatsAppListener`, after `parse` add:

```fsharp
    /// Drain every message since the cursor (all chats, ascending — GetMessagesSince already
    /// orders by timestamp ASC) through processOne, returning the cursor advanced to the latest
    /// processed message's timestamp. Re-processing is safe (pipeline idempotency).
    let catchUp (messages: IMessageSource) (processOne: string -> string -> unit) (cursor: ListenCursor) : ListenCursor =
        let msgs = messages.GetMessagesSince(None, cursor.Since)
        let mutable latest = cursor.Since
        for m in msgs do
            processOne m.Id m.ChatJid
            if m.Timestamp > latest then latest <- m.Timestamp
        { Since = latest }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: PASS.

- [ ] **Step 5: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: WhatsApp listener catch-up driver"
```

---

## Task 3: Ports + `runSession` orchestration

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add two interfaces)
- Modify: `src/Nameless.TaskList.Core/WhatsAppListener.fs` (add `runSession`)
- Test: `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`

**Interfaces:**
- Consumes: `parse`, `catchUp`, `IMessageSource`.
- Produces:
  - `INotificationListener.Subscribe : channel: string -> unit`
  - `INotificationListener.WaitNext : token: System.Threading.CancellationToken -> string list`
  - `IListenCursorStore.Load : unit -> ListenCursor` / `Save : ListenCursor -> unit`
  - `WhatsAppListener.runSession : INotificationListener -> IListenCursorStore -> IMessageSource -> (string -> string -> unit) -> string -> (string -> unit) -> System.Threading.CancellationToken -> unit`

- [ ] **Step 1: Add the ports**

In `src/Nameless.TaskList.Core/Ports.fs`, after `IEmailCursorStore` add:

```fsharp
/// A persistent Postgres LISTEN connection, behind a port so the session loop is testable.
type INotificationListener =
    /// Issue LISTEN on the channel. After this, the server buffers notifications for delivery.
    abstract member Subscribe : channel: string -> unit
    /// Block until at least one notification is available, returning all currently-available
    /// payloads. Throws OperationCanceledException when the token is cancelled.
    abstract member WaitNext : token: System.Threading.CancellationToken -> string list

/// Persists the WhatsApp listener catch-up cursor.
type IListenCursorStore =
    abstract member Load : unit -> ListenCursor
    abstract member Save : cursor: ListenCursor -> unit
```

- [ ] **Step 2: Write the failing runSession tests**

Append to `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`:

```fsharp
open System.Threading

// Fake listener: records Subscribe, then WaitNext returns scripted batches in order; when
// exhausted it throws OperationCanceledException to end the session cleanly.
type FakeListener(batches: string list list, events: ResizeArray<string>) =
    let queue = System.Collections.Generic.Queue<string list>(batches)
    interface INotificationListener with
        member _.Subscribe(channel) = events.Add(sprintf "subscribe:%s" channel)
        member _.WaitNext(_token) =
            if queue.Count = 0 then raise (OperationCanceledException())
            else queue.Dequeue()

type FakeCursorStore() =
    member val Saved = ResizeArray<ListenCursor>() with get
    member val Current = { Since = DateTime.MinValue } with get, set
    interface IListenCursorStore with
        member this.Load() = this.Current
        member this.Save(c) = this.Current <- c; this.Saved.Add c

let private payload id (iso: string) =
    sprintf """{"id":"%s","chat_jid":"c@s","timestamp":"%s"}""" id iso

[<Fact>]
let ``runSession subscribes before catch-up, then dispatches live payloads`` () =
    let events = ResizeArray<string>()
    // Catch-up source records "catchup" so we can assert ordering relative to subscribe.
    let mb =
        { new IMessageSource with
            member _.GetMessage(_, _) = None
            member _.GetRecent(_, _, _) = []
            member _.GetMessagesSince(_, _) = events.Add "catchup"; []
            member _.GetMediaBytes(_, _) = None }
    let listener = FakeListener([ [ payload "<a>" "2026-06-18T10:00:00+02:00" ] ], events)
    let store = FakeCursorStore()
    WhatsAppListener.runSession listener store mb
        (fun id _ -> events.Add(sprintf "process:%s" id))
        "whatsapp_new_message" (fun _ -> ()) CancellationToken.None
    Assert.Equal<string list>(
        [ "subscribe:whatsapp_new_message"; "catchup"; "process:<a>" ], List.ofSeq events)
    // cursor advanced to the live payload's SAST timestamp
    Assert.Equal(DateTime(2026, 6, 18, 10, 0, 0), store.Current.Since)

[<Fact>]
let ``runSession skips an unparseable payload without dispatching or dying`` () =
    let events = ResizeArray<string>()
    let mb =
        { new IMessageSource with
            member _.GetMessage(_, _) = None
            member _.GetRecent(_, _, _) = []
            member _.GetMessagesSince(_, _) = []
            member _.GetMediaBytes(_, _) = None }
    let listener = FakeListener([ [ "{garbage"; payload "<b>" "2026-06-18T11:00:00+02:00" ] ], events)
    let store = FakeCursorStore()
    let skipped = ResizeArray<string>()
    WhatsAppListener.runSession listener store mb
        (fun id _ -> events.Add(sprintf "process:%s" id))
        "whatsapp_new_message" (fun s -> skipped.Add s) CancellationToken.None
    Assert.Equal<string list>([ "process:<b>" ], List.ofSeq events)   // good one still processed
    Assert.Equal(1, skipped.Count)                                    // bad one logged
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: FAIL — `runSession` / `INotificationListener` not defined.

- [ ] **Step 4: Implement `runSession`**

In `src/Nameless.TaskList.Core/WhatsAppListener.fs`, inside `module WhatsAppListener`, after `catchUp` add:

```fsharp
    // The KB cursor is SAST wall-clock; a payload carries an offset timestamp.
    let private toSast (ts: System.DateTimeOffset) = ts.ToOffset(System.TimeSpan.FromHours 2.0).DateTime

    /// One connection session: LISTEN first (so live notifications buffer), then catch up from the
    /// stored cursor, then process live payloads — advancing + persisting the cursor per message.
    /// A bad payload is reported via `log` and skipped. Returns on cancellation; lets a connection
    /// failure propagate so the host can reconnect.
    let runSession
        (listener: INotificationListener) (cursorStore: IListenCursorStore)
        (messages: IMessageSource) (processOne: string -> string -> unit)
        (channel: string) (log: string -> unit) (token: System.Threading.CancellationToken) : unit =
        listener.Subscribe channel
        cursorStore.Save(catchUp messages processOne (cursorStore.Load()))
        try
            while not token.IsCancellationRequested do
                for payload in listener.WaitNext token do
                    match parse payload with
                    | Some p ->
                        processOne p.Id p.ChatJid
                        cursorStore.Save { Since = toSast p.Timestamp }
                    | None -> log (sprintf "skipped unparseable NOTIFY payload: %s" payload)
        with :? System.OperationCanceledException -> ()
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: PASS.

- [ ] **Step 6: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: WhatsApp listener ports + runSession orchestration"
```

---

## Task 4: Adapters — cursor store + Npgsql listener

**Files:**
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add two adapters)
- Test: `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs` (cursor store round-trip)

**Interfaces:**
- Consumes: `IListenCursorStore`, `INotificationListener`, `ListenCursor`.
- Produces:
  - `Adapters.FileSystemListenCursorStore(path: string) : IListenCursorStore`
  - `Adapters.NpgsqlNotificationListener(connectionString: string) : INotificationListener`

- [ ] **Step 1: Write the failing cursor-store test**

Append to `tests/Nameless.TaskList.Core.Tests/WhatsAppListenerTests.fs`:

```fsharp
open System.IO
open Nameless.TaskList.Core.Adapters

[<Fact>]
let ``FileSystemListenCursorStore round-trips, defaulting to MinValue when missing`` () =
    let path = Path.Combine(Path.GetTempPath(), "wa-cursor-" + Guid.NewGuid().ToString("N") + ".json")
    try
        let store = FileSystemListenCursorStore(path) :> IListenCursorStore
        Assert.Equal({ Since = DateTime.MinValue }, store.Load())
        let c = { Since = DateTime(2026, 6, 18, 12, 0, 0) }
        store.Save c
        Assert.Equal(c, (FileSystemListenCursorStore(path) :> IListenCursorStore).Load())
    finally
        (try File.Delete path with _ -> ())
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: FAIL — `FileSystemListenCursorStore` not defined.

- [ ] **Step 3: Implement the cursor store**

In `src/Nameless.TaskList.Core/Adapters.fs`, inside `module Adapters` (near `FileSystemEmailCursorStore`), add:

```fsharp
    // ---- WhatsApp listener catch-up cursor over a single JSON file. ----
    type FileSystemListenCursorStore(path: string) =
        interface IListenCursorStore with
            member _.Save(cursor) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                File.WriteAllText(path, JsonSerializer.Serialize(cursor))
            member _.Load() =
                try
                    if File.Exists path then JsonSerializer.Deserialize<ListenCursor>(File.ReadAllText path)
                    else { Since = DateTime.MinValue }
                with _ -> { Since = DateTime.MinValue }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: PASS.

- [ ] **Step 5: Implement the Npgsql listener (live-only; build is the check)**

In `src/Nameless.TaskList.Core/Adapters.fs`, inside `module Adapters`, add the adapter below. It uses Npgsql's notification model: `LISTEN` via a command, then `Wait` surfaces queued notifications through the `Notification` event. **Verify the exact API against Npgsql 9.0.2 and adapt if a member differs** (e.g. `Wait`/`WaitAsync` overloads) — `dotnet build` is the ground truth:

```fsharp
    // ---- Live Postgres LISTEN connection. Not unit-tested (opt-in integration test covers it). ----
    type NpgsqlNotificationListener(connectionString: string) =
        let mutable conn : NpgsqlConnection = null
        let received = System.Collections.Generic.Queue<string>()
        interface INotificationListener with
            member _.Subscribe(channel) =
                conn <- new NpgsqlConnection(connectionString)
                conn.Open()
                conn.Notification.Add(fun e -> received.Enqueue e.Payload)
                use cmd = new NpgsqlCommand(sprintf "LISTEN %s" channel, conn)
                cmd.ExecuteNonQuery() |> ignore
            member _.WaitNext(token) =
                // Block until the server pushes at least one notification, then drain the queue.
                if received.Count = 0 then
                    conn.WaitAsync(token).AsTask().GetAwaiter().GetResult() |> ignore
                [ while received.Count > 0 do received.Dequeue() ]
```

(If `WaitAsync` returns a plain `Task`/`ValueTask<bool>` in 9.0.2, adjust the `.AsTask()`/`GetResult` chain so it compiles and respects `token`. The channel name passed to `Subscribe` is a trusted constant from config, not user input.)

- [ ] **Step 6: Build + full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds (Npgsql API resolves); all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: WhatsApp listener adapters — cursor store + Npgsql LISTEN"
```

---

## Task 5: Host service + wiring

**Files:**
- Create: `src/Nameless.TaskList/WhatsAppListener.fs`
- Modify: `src/Nameless.TaskList/Nameless.TaskList.fsproj` (register before `Program.fs`)
- Modify: `src/Nameless.TaskList/Program.fs` (gated registration)
- Modify: `src/Nameless.TaskList/appsettings.json` (`WhatsApp:Listen` section)

**Interfaces:**
- Consumes: `WhatsAppListener.runSession`, `INotificationListener`, `IListenCursorStore`, `IMessageSource`, `Pipeline.processMessage`, `PipelineDeps`, `Adapters.NpgsqlNotificationListener`, `Adapters.FileSystemListenCursorStore`.
- Produces: `WhatsAppListenerService : BackgroundService`.

- [ ] **Step 1: Add the config section**

In `src/Nameless.TaskList/appsettings.json`, add a top-level `"WhatsApp"` object:

```json
  "WhatsApp": {
    "Listen": { "Enabled": false, "ReconnectSeconds": 10 }
  }
```

- [ ] **Step 2: Create the host service**

Create `src/Nameless.TaskList/WhatsAppListener.fs`:

```fsharp
namespace Nameless.TaskList

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline

/// Drives the WhatsApp pipeline from Postgres LISTEN/NOTIFY: a connection session (LISTEN →
/// catch-up → live) wrapped in a reconnect loop. Off unless WhatsApp:Listen:Enabled = "true".
type WhatsAppListenerService
    (listener: INotificationListener, cursorStore: IListenCursorStore, messages: IMessageSource,
     buildDeps: unit -> PipelineDeps, channel: string, reconnectSeconds: int,
     logger: ILogger<WhatsAppListenerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let deps = buildDeps ()
            let processOne (id: string) (jid: string) = Pipeline.processMessage deps id jid |> ignore
            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Run((fun () ->
                            WhatsAppListener.runSession listener cursorStore messages processOne
                                channel (fun m -> logger.LogWarning("{Msg}", m)) stoppingToken),
                            stoppingToken)
                with ex ->
                    logger.LogWarning(ex, "WhatsApp listener session ended; reconnecting")
                if not stoppingToken.IsCancellationRequested then
                    try do! Task.Delay(TimeSpan.FromSeconds(float reconnectSeconds), stoppingToken)
                    with :? OperationCanceledException -> ()
        } :> Task
```

- [ ] **Step 3: Register the file before `Program.fs`**

In `src/Nameless.TaskList/Nameless.TaskList.fsproj`, add immediately BEFORE the `Program.fs` line:

```xml
        <Compile Include="WhatsAppListener.fs" />
        <Compile Include="Program.fs"/>
```

- [ ] **Step 4: Wire it in `Program.fs`**

In `src/Nameless.TaskList/Program.fs`, after the IMAP registration block and before `let app = builder.Build()`, add (READ the file first to confirm `buildDeps` is in scope here — it was hoisted above `builder.Build()` for the IMAP poller; reuse it):

```fsharp
        // WhatsApp channel: register the LISTEN/NOTIFY listener only when enabled.
        if cfg.["WhatsApp:Listen:Enabled"] = "true" then
            builder.Services.AddHostedService<WhatsAppListenerService>(fun sp ->
                let connStr = cfg.GetConnectionString("WhatsApp")
                let listener = NpgsqlNotificationListener(connStr) :> INotificationListener
                let cursorPath = System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "whatsapp-listen-cursor.json")
                let cursorStore = FileSystemListenCursorStore(cursorPath) :> IListenCursorStore
                let messages = sp.GetRequiredService<IMessageSource>()
                let buildListenerDeps () =
                    buildDeps
                        messages
                        (sp.GetRequiredService<IVault>())
                        (sp.GetRequiredService<IChatClient>())
                        (sp.GetRequiredService<IEmbedder>())
                        (sp.GetRequiredService<IVision>())
                        (sp.GetRequiredService<ITranscriber>())
                let reconnectSeconds = match System.Int32.TryParse(cfg.["WhatsApp:Listen:ReconnectSeconds"]) with | true, n -> n | _ -> 10
                let logger = sp.GetRequiredService<ILogger<WhatsAppListenerService>>()
                new WhatsAppListenerService(listener, cursorStore, messages, buildListenerDeps, "whatsapp_new_message", reconnectSeconds, logger)) |> ignore
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: build succeeds. (No new unit test — this is composition; the build verifies DI/types, and Task 3's `runSession` tests cover the loop behavior.)

- [ ] **Step 6: Full suite (confirm listener stays off in tests)**

Run: `dotnet test`
Expected: all tests pass; no listener starts (`WhatsApp:Listen:Enabled` defaults false).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: WhatsAppListenerService background listener + wiring"
```

---

## Task 6: Opt-in live integration test

**Files:**
- Create: `tests/Nameless.TaskList.IntegrationTests/WhatsAppListenerTests.fs` (register in that fsproj)

**Interfaces:**
- Consumes: `Adapters.NpgsqlNotificationListener`, `Support.Config.connectionString`, `Support.ServiceProbes.postgres`.

- [ ] **Step 1: Write the live test**

Create `tests/Nameless.TaskList.IntegrationTests/WhatsAppListenerTests.fs`. It subscribes on a real connection, issues a `NOTIFY` from a second connection, and asserts `WaitNext` surfaces the payload. Mirror the skip idiom used by the sibling tests (`Skip.IfNot ServiceProbes.postgres.Value`):

```fsharp
module Nameless.TaskList.IntegrationTests.WhatsAppListenerTests

open System.Threading
open Npgsql
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.IntegrationTests.Support
open Xunit

[<SkippableFact>]
let ``NpgsqlNotificationListener receives a payload published on its channel`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not available")
    let listener = NpgsqlNotificationListener(Config.connectionString) :> INotificationListener
    listener.Subscribe "whatsapp_new_message"
    // Publish from a second connection.
    use pub = new NpgsqlConnection(Config.connectionString)
    pub.Open()
    use cmd = new NpgsqlCommand("""NOTIFY whatsapp_new_message, '{"id":"IT1","chat_jid":"c@s","timestamp":"2026-06-18T10:00:00+02:00"}'""", pub)
    cmd.ExecuteNonQuery() |> ignore
    // WaitNext should surface it (use a timeout token so a failure doesn't hang the suite).
    use cts = new CancellationTokenSource(System.TimeSpan.FromSeconds 10.0)
    let payloads = listener.WaitNext cts.Token
    Assert.Contains(payloads, fun (p: string) -> p.Contains "IT1")
```

- [ ] **Step 2: Register the test file**

In `tests/Nameless.TaskList.IntegrationTests/*.fsproj`, add `WhatsAppListenerTests.fs` to the `Compile` items (match existing ordering).

- [ ] **Step 3: Confirm it builds and skips by default**

Run: `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true --filter "FullyQualifiedName~WhatsAppListenerTests"`
Expected: SKIPPED when no Postgres is configured/reachable; PASS against a real DB.

- [ ] **Step 4: Confirm the default suite is unaffected**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; default tests pass; integration suite excluded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test: opt-in live integration test for NpgsqlNotificationListener"
```

---

## Self-Review

**Spec coverage:**
- Config & gating (`WhatsApp:Listen:Enabled`, reuse conn string) → Task 5. ✓
- Pure `parse` → Task 1. ✓
- `INotificationListener` port + Npgsql adapter → Tasks 3, 4. ✓
- Cursor + `catchUp` → Tasks 2, 4. ✓
- Service loop ordering (LISTEN → catch-up → live; reconnect) → Task 3 (`runSession`) + Task 5 (reconnect wrapper). ✓
- Sequential processing + idempotency dedup → inherent (no new dedup); `runSession` is sequential. ✓
- Tests: parse, catchUp, service-logic ordering/skip, cursor store, opt-in live → Tasks 1–4, 6. ✓
- Out of scope (DB trigger, parallel processing, multi-channel, delivery guarantees) → not built. ✓
- Risk: cursor timezone consistency → handled by `toSast` (live) + `catchUp` advancing on `ChatMessage.Timestamp` (already SAST), both feeding `GetMessagesSince` as the bulk path does; `catchUp` test asserts the advanced cursor value. ✓

**Placeholder scan:** No TBD/TODO; every code step has complete code. The Npgsql `WaitAsync` shape (Task 4) and the integration skip idiom (Task 6) carry explicit "verify against the installed version / sibling tests" notes — the code given is correct for Npgsql 9.0.2's documented API and the repo's `Support`/`SkippableFact` setup, adapt if local differs.

**Type consistency:** `NotifyPayload`/`ListenCursor` (Task 1) used unchanged in Tasks 2–5. `catchUp`/`runSession` signatures (Tasks 2–3) match their call sites in Task 5. `INotificationListener`/`IListenCursorStore` (Task 3) match the adapters (Task 4) and the service (Task 5). The host `buildDeps` reuse (Task 5) matches the existing hoisted helper from the IMAP work.
