# Vault Consistency (Increment B) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the vault queryable — update `Channel` files inline as messages are processed, and add an on-demand `POST /reindex` that rebuilds the seven `index.md` files from current vault state.

**Architecture:** Extend `IVault` with a recursive lister. Add an inline `updateChannel` step to `Pipeline.processMessage` (create-if-missing + activity update) in both the noise and signal branches. Add a standalone `Indexer` module (Core) that scans the vault and renders deterministic indexes, exposed via a new web-host endpoint.

**Tech Stack:** F# / .NET 10, Markdig + YamlDotNet, ASP.NET Core minimal API, xUnit.

## Global Constraints

- Target framework `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-22-vault-consistency-design.md`. KB conventions authoritative in `docs/DESIGN.md` (§4 schemas, §5 index examples, §8 naming).
- Writes are create / overwrite-only — **never delete** vault files. The channel update preserves the existing body; the indexer only overwrites `index.md` files.
- The channel update runs on every processed message (noise and signal); `message_count` counts unique processed messages (inherits the spine's message-existence idempotency guard).
- Index regeneration is a deterministic full rebuild; a malformed individual source file is skipped (counted), never fatal.
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope: digests/briefings, scheduling, DESIGN §9, live-service integration tests. `PipelineResult` is unchanged.

Existing record field sets (for reference):
- `Channel = { Type; Title; Platform; Context: string; People: string[]; SignalWeight; MessageCount: int; LastProcessed: System.DateTime; ActiveTopics: string[] }`
- `Task = { Type; Title; Status; Priority; Due; Context: string[]; People: string[]; Topic; SourceMessage }`
- `Topic = { Type; Title; Status; Context: string[]; Channel; People: string[]; FirstSeen; LastUpdated; SpawnedTasks: string[]; SpawnedEvents: string[]; MessageRefs: string[] }`
- `Event = { Type; Title; When; AllDay: bool; Context: string[]; Location; People: string[]; Topic; TasksLinked: string[]; ReminderDaysBefore: int }`
- `Commitment = { Type; Title; Status; Priority; Due; Context: string[]; Topic; TaskAssigned; EscalateAfterDays: int; SourceMessage }`
- `Note = { Type; Title; Context: string[]; PeopleLinked: string[]; Tags: string[]; Source; LastVerified }`
- `Person = { Type; Title; Role; Context: string[]; Channel; Phone; Email; Tags: string[] }`

---

### Task 1: `IVault.ListFilesRecursive`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs`
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (FileSystemVault)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (FakeVault)
- Modify: `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `IVault.ListFilesRecursive : relDir: string -> string list` (all files under the tree, vault-relative, `/`-separated; `[]` if the directory is absent). Implemented by `FileSystemVault` and `FakeVault`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/VaultTests.fs`:

```fsharp
[<Fact>]
let ``ListFilesRecursive returns files from nested directories`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("events/2026/06/a.md", "x")
        vault.Write("events/2026/07/b.md", "y")
        vault.Write("events/index.md", "i")
        let files = vault.ListFilesRecursive("events") |> List.sort
        Assert.Equal<string list>([ "events/2026/06/a.md"; "events/2026/07/b.md"; "events/index.md" ], files)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``ListFilesRecursive returns empty for a missing directory`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        Assert.Empty(vault.ListFilesRecursive("nope"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``FakeVault ListFilesRecursive returns nested keys under the prefix`` () =
    let vault = Nameless.TaskList.Core.Tests.Fakes.FakeVault()
    vault.Seed("people/medical/a.md", "x")
    vault.Seed("people/school/b.md", "y")
    vault.Seed("tasks/pending/c.md", "z")
    let files = (vault :> IVault).ListFilesRecursive("people") |> List.sort
    Assert.Equal<string list>([ "people/medical/a.md"; "people/school/b.md" ], files)
```

(`tempRoot` and `FileSystemVault` are already in scope in `VaultTests.fs`.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~VaultTests"`
Expected: FAIL — `ListFilesRecursive` is not a member of `IVault`.

- [ ] **Step 3: Add the member to IVault**

In `src/Nameless.TaskList.Core/Ports.fs`, inside the `IVault` type, after `ListFiles`:

```fsharp
    abstract member ListFilesRecursive : relDir: string -> string list
```

- [ ] **Step 4: Implement in FileSystemVault**

In `src/Nameless.TaskList.Core/Adapters.fs`, in the `FileSystemVault` `IVault` interface block, after the `ListFiles` member:

```fsharp
            member _.ListFilesRecursive(relDir) =
                let dir = full relDir
                if Directory.Exists(dir) then
                    Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    |> Array.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/'))
                    |> List.ofArray
                else []
```

- [ ] **Step 5: Implement in FakeVault**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs`, in the `FakeVault` `IVault` interface block, after the `ListFiles` member:

```fsharp
        member _.ListFilesRecursive(relDir) =
            let prefix = relDir.TrimEnd('/') + "/"
            files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~VaultTests"`
Expected: PASS, 3 new tests pass; existing vault tests still pass.

- [ ] **Step 7: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add IVault.ListFilesRecursive"
```

---

### Task 2: Channel update (inline, per message)

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add `Naming.channelPath`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (add `updateChannel`; call it in both branches)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `IVault`, `Channel` record, `Frontmatter`, `MarkdownFile`, `ChatMessage`.
- Produces: `Naming.channelPath : string -> string` → `channels/whatsapp/{slug}.md`; a module-level `private updateChannel : PipelineDeps -> ChatMessage -> string -> string option -> unit` in `Pipeline`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`. These use a signal classification with **empty** entity buckets and no people, so the only LLM calls are classify, topic-match, and topic-update (3 calls):

```fsharp
let private emptySignalClassify =
    Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"hi","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

let private newTopicMatch (title: string) =
    Responses.final (sprintf """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"%s"}""" title)

[<Fact>]
let ``signal message creates a channel with direct platform and records the topic`` () =
    let vault = FakeVault()
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ emptySignalClassify; newTopicMatch "Family chat"; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]   // sampleMessage NormalizedChatName = "Wife"
    Assert.Contains("platform: whatsapp-direct", ch)
    Assert.Contains("message_count: 1", ch)
    Assert.Contains("topics/active/family-chat.md", ch)   // active_topics holds the topic

[<Fact>]
let ``noise message creates a channel and counts it without a topic`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]
    Assert.Contains("message_count: 1", ch)
    Assert.DoesNotContain("topics/active", ch)

[<Fact>]
let ``existing channel increments count, dedupes topic, and preserves body`` () =
    let vault = FakeVault()
    // Pre-seed a channel that already counted 5 messages and already lists the topic.
    vault.Seed("channels/whatsapp/wife.md",
        "---\ntype: Channel\ntitle: Wife\nplatform: whatsapp-direct\ncontext: ''\npeople: []\nsignal_weight: high\nmessage_count: 5\nlast_processed: 2026-06-10T00:00:00\nactive_topics:\n  - topics/active/family-chat.md\n---\nExisting channel notes.")
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ emptySignalClassify; newTopicMatch "Family chat"; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let ch = vault.Files.["channels/whatsapp/wife.md"]
    Assert.Contains("message_count: 6", ch)
    Assert.Contains("Existing channel notes.", ch)
    // topic appears exactly once (deduped)
    let occurrences = (ch.Split("topics/active/family-chat.md").Length - 1)
    Assert.Equal(1, occurrences)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — no `channels/whatsapp/wife.md` is written.

- [ ] **Step 3: Add `Naming.channelPath`**

At the end of the `Naming` module in `KnowledgeBase.fs`:

```fsharp
    let channelPath (channelSlug: string) : string =
        sprintf "channels/whatsapp/%s.md" channelSlug
```

- [ ] **Step 4: Add the `updateChannel` helper**

In `src/Nameless.TaskList.Core/Pipeline.fs`, add a module-level `private` function after the existing `personExists` helper (or anywhere among the module-level helpers, before `processMessage`):

```fsharp
    /// Create-if-missing + activity update for the message's channel file.
    let private updateChannel (deps: PipelineDeps) (msg: ChatMessage) (channelSlug: string) (topic: string option) =
        let path = Naming.channelPath channelSlug
        let stub : Channel =
            { Type = "Channel"; Title = msg.NormalizedChatName
              Platform = (if msg.IsGroup then "whatsapp-group" else "whatsapp-direct")
              Context = ""; People = [||]; SignalWeight = "medium"
              MessageCount = 0; LastProcessed = System.DateTime.MinValue; ActiveTopics = [||] }
        let current, body =
            if deps.Vault.Exists path then
                let mf = MarkdownFile.FromString (deps.Vault.Read path)
                match mf.FrontMatter with
                | Some fm -> (Frontmatter.deserialize<Channel> fm), mf.Content
                | None -> stub, mf.Content
            else stub, ""
        let existingTopics = if isNull current.ActiveTopics then [||] else current.ActiveTopics
        let activeTopics =
            match topic with
            | Some t when not (Array.contains t existingTopics) -> Array.append existingTopics [| t |]
            | _ -> existingTopics
        let updated =
            { current with
                LastProcessed = msg.Timestamp
                MessageCount = current.MessageCount + 1
                ActiveTopics = activeTopics }
        deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize updated) body)
```

- [ ] **Step 5: Call it in the noise branch**

In `processMessage`, in the noise branch, just before `ProcessedNoise`:

```fsharp
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) "")
                updateChannel deps msg channelSlug None
                ProcessedNoise
```

- [ ] **Step 6: Call it in the signal branch**

In `processMessage`, at the very end of the signal path, just before `Processed(topicPath, taskPaths)`:

```fsharp
            updateChannel deps msg channelSlug (Some topicPath)
            Processed(topicPath, taskPaths)
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS — 3 new channel tests pass; existing pipeline tests still pass.

> If an existing pipeline test that processes a signal/noise message now also writes a channel file, that's expected and shouldn't break its assertions (they check specific keys/paths). If any test asserted the *total* number of vault files, update it to account for the new channel file.

- [ ] **Step 8: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: update Channel file (create-if-missing) per processed message"
```

---

### Task 3: Indexer scaffolding + tasks/topics/channels renderers

**Files:**
- Create: `src/Nameless.TaskList.Core/Indexer.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile after `Pipeline.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `IVault.ListFilesRecursive`, `Frontmatter`, `MarkdownFile`, the domain records.
- Produces (module `Nameless.TaskList.Core.Indexer`):
  - `IndexSummary = { Tasks:int; Topics:int; Events:int; Commitments:int; Notes:int; People:int; Channels:int; Skipped:int }`
  - `regenerate : IVault -> IndexSummary`
  - (private) `loadAll<'T>`, `writeIndex`, `wikilink`, and `renderTasks`/`renderTopics`/`renderChannels`.

- [ ] **Step 1: Register the test file**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add after the last existing `<Compile Include="...Tests.fs" />`:

```xml
        <Compile Include="IndexerTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.IndexerTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private seedCore () =
    let v = FakeVault()
    v.Seed("tasks/pending/high.md", "---\ntype: Task\ntitle: Urgent\nstatus: pending\npriority: high\ndue: 2026-07-01\ncontext:\n  - medical\n---\nbody")
    v.Seed("tasks/pending/low.md", "---\ntype: Task\ntitle: Whenever\nstatus: pending\npriority: low\ndue: ''\ncontext:\n  - family\n---\nbody")
    v.Seed("topics/active/t1.md", "---\ntype: Topic\ntitle: Topic One\nstatus: active\nlast_updated: 2026-06-15\n---\nbody")
    v.Seed("channels/whatsapp/wife.md", "---\ntype: Channel\ntitle: Wife\nplatform: whatsapp-direct\nsignal_weight: high\nmessage_count: 3\nlast_processed: 2026-06-15T00:00:00\nactive_topics:\n  - topics/active/t1.md\n---\nbody")
    v.Seed("tasks/pending/bad.md", "no frontmatter here")   // malformed → skipped
    v

[<Fact>]
let ``regenerate writes a tasks index ordering high priority before low`` () =
    let v = seedCore ()
    Indexer.regenerate (v :> IVault) |> ignore
    let idx = v.Files.["tasks/index.md"]
    Assert.Contains("[[tasks/pending/high]]", idx)
    Assert.Contains("[[tasks/pending/low]]", idx)
    Assert.True(idx.IndexOf("tasks/pending/high") < idx.IndexOf("tasks/pending/low"))

[<Fact>]
let ``regenerate writes topic and channel indexes`` () =
    let v = seedCore ()
    Indexer.regenerate (v :> IVault) |> ignore
    Assert.Contains("[[topics/active/t1]]", v.Files.["topics/index.md"])
    Assert.Contains("[[channels/whatsapp/wife]]", v.Files.["channels/index.md"])

[<Fact>]
let ``regenerate counts items and skips malformed files`` () =
    let v = seedCore ()
    let s = Indexer.regenerate (v :> IVault)
    Assert.Equal(2, s.Tasks)        // high + low; bad.md skipped
    Assert.Equal(1, s.Topics)
    Assert.Equal(1, s.Channels)
    Assert.Equal(1, s.Skipped)
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: FAIL — `Indexer` is not defined.

- [ ] **Step 4: Create Indexer.fs with scaffolding + three renderers**

Create `src/Nameless.TaskList.Core/Indexer.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System.Text
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

module Indexer =

    type IndexSummary =
        { Tasks: int; Topics: int; Events: int; Commitments: int
          Notes: int; People: int; Channels: int; Skipped: int }

    [<CLIMutable>]
    type private IndexMeta = { Type: string; Title: string; LastUpdated: string }

    let private nz (s: string) = if isNull s then "" else s
    let private joinArr (a: string array) = if isNull a then "" else System.String.Join(", ", a)

    let private wikilink (path: string) =
        let p = if path.EndsWith(".md") then path.[.. path.Length - 4] else path
        sprintf "[[%s]]" p

    /// Load all parseable records of type 'T under root (excluding any index.md); returns (path, record) list and a skip count.
    let private loadAll<'T> (vault: IVault) (root: string) : (string * 'T) list * int =
        let files =
            vault.ListFilesRecursive root
            |> List.filter (fun p -> not (p.EndsWith("index.md")))
        let mutable skipped = 0
        let parsed =
            files
            |> List.choose (fun p ->
                try
                    let mf = MarkdownFile.FromString (vault.Read p)
                    match mf.FrontMatter with
                    | Some fm -> Some(p, Frontmatter.deserialize<'T> fm)
                    | None -> skipped <- skipped + 1; None
                with _ -> skipped <- skipped + 1; None)
        parsed, skipped

    let private writeIndex (vault: IVault) (root: string) (title: string) (body: string) =
        let meta : IndexMeta =
            { Type = "Index"; Title = title; LastUpdated = System.DateTime.UtcNow.ToString("yyyy-MM-dd") }
        vault.Write(root.TrimEnd('/') + "/index.md", MarkdownFile.ToString (Frontmatter.serialize meta) body)

    let private priorityRank (p: string) =
        match (nz p).ToLowerInvariant() with
        | "critical" -> 0 | "high" -> 1 | "medium" -> 2 | "low" -> 3 | _ -> 4

    let private taskStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with
        | "pending" -> 0 | "in-progress" -> 1 | "blocked" -> 2 | "done" -> 3 | "cancelled" -> 4 | _ -> 5

    let private renderTasks (vault: IVault) : int * int =
        let items, skipped = loadAll<Task> vault "tasks"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, t) -> (taskStatusRank t.Status, priorityRank t.Priority, nz t.Due, path))
        |> List.groupBy (fun (_, t) -> (if nz t.Status = "" then "unknown" else t.Status))
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, t) in rows do
                sb.AppendLine(sprintf "- %s — due %s · %s" (wikilink path) (nz t.Due) (joinArr t.Context)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "tasks" "Task Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private topicStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with "active" -> 0 | "resolved" -> 1 | _ -> 2

    let private renderTopics (vault: IVault) : int * int =
        let items, skipped = loadAll<Topic> vault "topics"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (_, t) -> topicStatusRank t.Status)
        |> List.groupBy (fun (_, t) -> (if nz t.Status = "" then "unknown" else t.Status))
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, t) in rows |> List.sortByDescending (fun (_, t) -> nz t.LastUpdated) do
                sb.AppendLine(sprintf "- %s — last updated %s" (wikilink path) (nz t.LastUpdated)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "topics" "Topic Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private renderChannels (vault: IVault) : int * int =
        let items, skipped = loadAll<Channel> vault "channels"
        let sb = StringBuilder()
        for (path, c) in items |> List.sortByDescending (fun (_, c) -> c.LastProcessed) do
            let activeCount = if isNull c.ActiveTopics then 0 else c.ActiveTopics.Length
            sb.AppendLine(sprintf "- %s — %s · last processed %s · %d active"
                              (wikilink path) (nz c.SignalWeight) (c.LastProcessed.ToString("yyyy-MM-dd")) activeCount) |> ignore
        writeIndex vault "channels" "Channel Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let regenerate (vault: IVault) : IndexSummary =
        let tCount, tSkip = renderTasks vault
        let topCount, topSkip = renderTopics vault
        let chCount, chSkip = renderChannels vault
        // events/commitments/notes/people renderers are added in the next task.
        { Tasks = tCount; Topics = topCount; Events = 0; Commitments = 0
          Notes = 0; People = 0; Channels = chCount
          Skipped = tSkip + topSkip + chSkip }
```

- [ ] **Step 5: Register Indexer.fs in the Core project**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add `Indexer.fs` after `Pipeline.fs`:

```xml
        <Compile Include="Indexer.fs" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: PASS, 3 tests pass.

- [ ] **Step 7: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Indexer.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add Indexer with tasks/topics/channels renderers"
```

---

### Task 4: Indexer events/commitments/notes/people renderers

**Files:**
- Modify: `src/Nameless.TaskList.Core/Indexer.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`

**Interfaces:**
- Consumes: the scaffolding from Task 3.
- Produces: private `renderEvents`/`renderCommitments`/`renderNotes`/`renderPeople`; `regenerate` now populates all seven counts.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`:

```fsharp
let private seedRest () =
    let v = FakeVault()
    v.Seed("events/2026/06/early.md", "---\ntype: Event\ntitle: Early\nwhen: 2026-06-01T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("events/2026/07/late.md", "---\ntype: Event\ntitle: Late\nwhen: 2026-07-01T09:00:00+02:00\ncontext:\n  - family\n---\nb")
    v.Seed("commitments/fees.md", "---\ntype: Commitment\ntitle: Fees\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ntask_assigned: ''\n---\nb")
    v.Seed("notes/allergy.md", "---\ntype: Note\ntitle: Allergy\ncontext:\n  - medical\ntags:\n  - allergy\n---\nb")
    v.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\nrole: Paediatrician\ncontext:\n  - medical\n---\nb")
    v

[<Fact>]
let ``regenerate writes events index in chronological order`` () =
    let v = seedRest ()
    Indexer.regenerate (v :> IVault) |> ignore
    let idx = v.Files.["events/index.md"]
    Assert.True(idx.IndexOf("events/2026/06/early") < idx.IndexOf("events/2026/07/late"))

[<Fact>]
let ``regenerate flags commitments with no assigned task`` () =
    let v = seedRest ()
    Indexer.regenerate (v :> IVault) |> ignore
    Assert.Contains("[[commitments/fees]]", v.Files.["commitments/index.md"])
    Assert.Contains("⚑", v.Files.["commitments/index.md"])

[<Fact>]
let ``regenerate writes notes and people indexes and full counts`` () =
    let v = seedRest ()
    let s = Indexer.regenerate (v :> IVault)
    Assert.Contains("[[notes/allergy]]", v.Files.["notes/index.md"])
    Assert.Contains("[[people/medical/dr-naidoo]]", v.Files.["people/index.md"])
    Assert.Equal(2, s.Events)
    Assert.Equal(1, s.Commitments)
    Assert.Equal(1, s.Notes)
    Assert.Equal(1, s.People)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: FAIL — events/commitments/notes/people indexes not written; `s.Events` etc. are 0.

- [ ] **Step 3: Add the four renderers**

In `src/Nameless.TaskList.Core/Indexer.fs`, add these before `regenerate`:

```fsharp
    let private whenKey (s: string) =
        match System.DateTimeOffset.TryParse(s) with
        | true, dto -> dto.UtcDateTime
        | _ -> System.DateTime.MaxValue

    let private renderEvents (vault: IVault) : int * int =
        let items, skipped = loadAll<Event> vault "events"
        let sb = StringBuilder()
        for (path, e) in items |> List.sortBy (fun (path, e) -> (whenKey e.When, path)) do
            sb.AppendLine(sprintf "- %s — %s · %s" (wikilink path) (nz e.When) (joinArr e.Context)) |> ignore
        writeIndex vault "events" "Event Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private commitmentStatusRank (s: string) =
        match (nz s).ToLowerInvariant() with "unresolved" -> 0 | "resolved" -> 1 | _ -> 2

    let private renderCommitments (vault: IVault) : int * int =
        let items, skipped = loadAll<Commitment> vault "commitments"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, c) -> (commitmentStatusRank c.Status, nz c.Due, path))
        |> List.groupBy (fun (_, c) -> (if nz c.Status = "" then "unknown" else c.Status))
        |> List.iter (fun (status, rows) ->
            sb.AppendLine(sprintf "## %s" status) |> ignore
            for (path, c) in rows do
                let flag = if System.String.IsNullOrWhiteSpace c.TaskAssigned then " ⚑" else ""
                sb.AppendLine(sprintf "- %s — due %s · %s%s" (wikilink path) (nz c.Due) (nz c.Priority) flag) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "commitments" "Commitment Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    let private firstContext (c: string array) =
        if isNull c || c.Length = 0 || System.String.IsNullOrWhiteSpace c.[0] then "uncategorised" else c.[0]

    let private renderNotes (vault: IVault) : int * int =
        let items, skipped = loadAll<Note> vault "notes"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, n) -> (firstContext n.Context, path))
        |> List.groupBy (fun (_, n) -> firstContext n.Context)
        |> List.iter (fun (ctx, rows) ->
            sb.AppendLine(sprintf "## %s" ctx) |> ignore
            for (path, n) in rows do
                sb.AppendLine(sprintf "- %s — %s" (wikilink path) (joinArr n.Tags)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "notes" "Notes Index" (sb.ToString().TrimEnd())
        List.length items, skipped

    /// Context is the directory segment: people/{ctx}/{slug}.md
    let private peopleDirContext (path: string) =
        let parts = path.Split('/')
        if parts.Length >= 3 then parts.[1] else "uncategorised"

    let private renderPeople (vault: IVault) : int * int =
        let items, skipped = loadAll<Person> vault "people"
        let sb = StringBuilder()
        items
        |> List.sortBy (fun (path, _) -> (peopleDirContext path, path))
        |> List.groupBy (fun (path, _) -> peopleDirContext path)
        |> List.iter (fun (ctx, rows) ->
            sb.AppendLine(sprintf "## %s" ctx) |> ignore
            for (path, p) in rows do
                sb.AppendLine(sprintf "- %s — %s" (wikilink path) (nz p.Role)) |> ignore
            sb.AppendLine() |> ignore)
        writeIndex vault "people" "People Index" (sb.ToString().TrimEnd())
        List.length items, skipped
```

- [ ] **Step 4: Update `regenerate` to call all seven renderers**

Replace the `regenerate` function body with:

```fsharp
    let regenerate (vault: IVault) : IndexSummary =
        let tCount, tSkip = renderTasks vault
        let topCount, topSkip = renderTopics vault
        let evCount, evSkip = renderEvents vault
        let cmCount, cmSkip = renderCommitments vault
        let nCount, nSkip = renderNotes vault
        let pCount, pSkip = renderPeople vault
        let chCount, chSkip = renderChannels vault
        { Tasks = tCount; Topics = topCount; Events = evCount; Commitments = cmCount
          Notes = nCount; People = pCount; Channels = chCount
          Skipped = tSkip + topSkip + evSkip + cmSkip + nSkip + pSkip + chSkip }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~IndexerTests"`
Expected: PASS — all Indexer tests pass (Task 3 + Task 4).

- [ ] **Step 6: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Indexer.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add events/commitments/notes/people index renderers"
```

---

### Task 5: `POST /reindex` endpoint

**Files:**
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (add `ReindexHandler`)
- Modify: `src/Nameless.TaskList/Program.fs` (map the route)
- Modify: `tests/Nameless.TaskList.Tests/EndpointTests.fs`

**Interfaces:**
- Consumes: `IVault`, `Indexer.regenerate`, `Indexer.IndexSummary`.
- Produces: `POST /reindex` → `200` with the `IndexSummary`; `500` on unexpected error. `ReindexHandler.toHttp : Indexer.IndexSummary -> IResult`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Nameless.TaskList.Tests/EndpointTests.fs`:

```fsharp
let private statusOfResult (r: IResult) : int =
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddRouting() |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    (r.ExecuteAsync(ctx)).Wait()
    ctx.Response.StatusCode

[<Fact>]
let ``reindex summary maps to 200`` () =
    let summary : Nameless.TaskList.Core.Indexer.IndexSummary =
        { Tasks = 2; Topics = 1; Events = 0; Commitments = 0; Notes = 0; People = 0; Channels = 1; Skipped = 0 }
    Assert.Equal(200, statusOfResult (ReindexHandler.toHttp summary))
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: FAIL — `ReindexHandler` is not defined.

- [ ] **Step 3: Add `ReindexHandler`**

In `src/Nameless.TaskList/ProcessMessage.fs`, after the `ProcessMessageHandler` module, add:

```fsharp
module ReindexHandler =
    /// Maps an index summary to a 200 response.
    let toHttp (summary: Nameless.TaskList.Core.Indexer.IndexSummary) : IResult =
        Results.Ok(box summary)
```

- [ ] **Step 4: Map the route**

In `src/Nameless.TaskList/Program.fs`, add `open Nameless.TaskList.Core` near the other opens (so `Indexer` resolves). After the existing `app.MapPost("/messages/process", ...)` block, add:

```fsharp
        app.MapPost("/reindex", System.Func<IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) ->
                try Indexer.regenerate vault |> ReindexHandler.toHttp
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Tests`
Expected: PASS — `reindex summary maps to 200` plus the existing endpoint tests.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList/ProcessMessage.fs src/Nameless.TaskList/Program.fs tests/Nameless.TaskList.Tests
git commit -m "feat: add POST /reindex endpoint"
```

---

### Task 6: Whole-solution verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run all tests**

Run: `dotnet test`
Expected: all pass (Core tests grown by Tasks 1-4; endpoint tests by Task 5).

- [ ] **Step 3: Confirm scope**

Run: `git diff --stat main..HEAD`
Expected: changes only under `src/Nameless.TaskList.Core/`, `src/Nameless.TaskList/` (the new endpoint + handler), `tests/`, and `docs/`. `PipelineResult` and the `/messages/process` mapping are unchanged.

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Task 1 → §3 (port extension). Task 2 → §4 (channel update, both branches, create-if-missing, dedupe, body-preserve, idempotency-inherited). Tasks 3-4 → §5 (Indexer: recursion roots, per-type formats/sorting, skip-on-malformed, all seven indexes). Task 5 → §5.4 (endpoint). Task 6 → §2 scope guard. §6 (idempotency/deletes/errors) is satisfied by create/overwrite-only writes + the endpoint's try/catch.
- **Type consistency:** `IndexSummary` is defined once (Task 3) and reused by Task 4's `regenerate` and Task 5's handler/test. `Channel`/`Task`/`Topic`/etc. field names match the Global Constraints reference block. `updateChannel`'s signature matches both call sites.
- **Deferred (do NOT build here):** digests, scheduling, DESIGN §9, live integration tests, any change to `PipelineResult` or the `/messages/process` mapping.
- **Known nuance:** `FakeVault.ListFiles` and `FakeVault.ListFilesRecursive` share logic (the in-memory fake is inherently flat-keyed); the real `FileSystemVault` distinguishes them (single-dir vs `AllDirectories`). This is acceptable fake fidelity.
- **Index timestamp:** each `index.md`'s frontmatter `last_updated` uses `DateTime.UtcNow` (date only). Tests assert on body wikilinks/ordering, not the timestamp, so they stay deterministic.
