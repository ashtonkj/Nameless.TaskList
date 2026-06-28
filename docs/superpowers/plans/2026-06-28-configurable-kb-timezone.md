# Configurable KB Timezone + Topic Collision Guard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the SAST `+02:00` offset hardcoded across the codebase with one configurable fixed offset (default `+02:00`) used consistently by every KB timestamp read and write, and add a `freePath` collision guard to `createNewTopic`.

**Architecture:** A new pure, offset-parameterised `Time` module in `KnowledgeBase.fs`. The configured `TimeSpan` (from `Vault:UtcOffsetHours`, default `2.0`) is threaded explicitly into the components that produce/consume KB timestamps — `PipelineDeps`, `DigestDeps`, the `Indexer.regenerate` `now`, and the `PostgresMessageSource`/email read-shift. No global mutable state. The default reproduces the current SAST deployment.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2, System.DateTimeOffset.

## Global Constraints

- Target framework `net10.0`.
- The default offset `2.0` MUST reproduce current behaviour on a SAST host byte-for-byte (where `…zzz` already yields `+02:00`). The Indexer `last_updated` is a deliberate *correction* (UTC date → configured-offset date) — it may differ near midnight on a SAST host, which is the intended fix.
- The `Time` helper is **pure**: the offset is a parameter, never a module-level mutable.
- Each Steps/Pipeline change stays behaviour-preserving at the default offset; the full per-project test suite stays green.
- Make edits through the Verevoir MCP `edit_file`/`write_file` (built-in Edit fallback ONLY for a `.fsproj` if MCP rejects the XML escaping — note it). Run tests **per-project** with `-p:nodeReuse=false` (the host's solution-wide `dotnet test` CLR-crashes under MSBuild node-reuse): `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`.
- No secrets in source; no real PII.

---

## Task 1: The `Time` module

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add `module Time`)
- Create: `tests/Nameless.TaskList.Core.Tests/TimeTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (register `TimeTests.fs`)

**Interfaces:**
- Produces:
  - `Nameless.TaskList.Core.KnowledgeBase.Time.iso : System.TimeSpan -> System.DateTime -> string`
  - `Nameless.TaskList.Core.KnowledgeBase.Time.now : System.TimeSpan -> System.DateTimeOffset`

- [ ] **Step 1: Add the `Time` module**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, add a `module Time` (place it near the top of the file's modules, e.g. immediately after the `namespace`/opens and before the codec types, so it compiles before everything that uses it):

```fsharp
/// Fixed-offset KB timestamp formatting. The KB records wall-clock timestamps with an explicit
/// offset (DESIGN §4/§8); that offset is configurable (default +02:00 / SAST) and passed in here,
/// so output never depends on the server's timezone.
module Time =

    /// Format a wall-clock DateTime as ISO-8601 with the given fixed offset. SpecifyKind Unspecified
    /// so the DateTimeOffset ctor never throws on a Local/Utc-kind input.
    let iso (offset: System.TimeSpan) (ts: System.DateTime) : string =
        System.DateTimeOffset(System.DateTime.SpecifyKind(ts, System.DateTimeKind.Unspecified), offset)
            .ToString("yyyy-MM-ddTHH:mm:sszzz")

    /// The current instant expressed at the given fixed offset.
    let now (offset: System.TimeSpan) : System.DateTimeOffset =
        System.DateTimeOffset.UtcNow.ToOffset(offset)
```

(If `KnowledgeBase.fs` is a single `module KnowledgeBase = ...`, nest `Time` as a sub-module `module Time =` inside it, so the reference becomes `KnowledgeBase.Time.iso`. Match the file's existing module style; the consumers below use `Time.iso`/`Time.now` after `open Nameless.TaskList.Core.KnowledgeBase`.)

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/TimeTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.TimeTests

open System
open Nameless.TaskList.Core.KnowledgeBase
open Xunit

[<Fact>]
let ``iso emits the given offset regardless of DateTime.Kind`` () =
    let dt = DateTime(2026, 6, 24, 9, 30, 0)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) dt)
    Assert.Equal("2026-06-24T09:30:00+05:30", Time.iso (TimeSpan.FromHours 5.5) dt)
    // A Local-kind input must not throw and must still use the supplied offset.
    let local = DateTime.SpecifyKind(dt, DateTimeKind.Local)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) local)
    // A Utc-kind input likewise.
    let utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    Assert.Equal("2026-06-24T09:30:00+02:00", Time.iso (TimeSpan.FromHours 2.0) utc)

[<Fact>]
let ``now is at the requested offset`` () =
    Assert.Equal(TimeSpan.FromHours 2.0, (Time.now (TimeSpan.FromHours 2.0)).Offset)
    Assert.Equal(TimeSpan.FromHours -5.0, (Time.now (TimeSpan.FromHours -5.0)).Offset)
```

Register in `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (after the existing `CodecTests.fs`/`NamingTests.fs` entries):

```xml
        <Compile Include="TimeTests.fs" />
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~TimeTests" -p:nodeReuse=false`
Expected: PASS (2 tests).

- [ ] **Step 4: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs tests/Nameless.TaskList.Core.Tests/TimeTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: add pure offset-parameterised Time module"
```

---

## Task 2: `createNewTopic` collision guard

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`createNewTopic`, ~lines 309-319)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (add a collision test)

**Interfaces:**
- Consumes: the existing `freePath : IVault -> string -> string` (private, defined earlier in `Pipeline`).

- [ ] **Step 1: Route the topic write through `freePath`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, in the `createNewTopic` local function, change the write+return so the path goes through `freePath`. Replace:

```fsharp
                deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                slug, Naming.topicPath slug
```

with:

```fsharp
                let path = freePath deps.Vault (Naming.topicPath slug)
                deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                slug, path
```

- [ ] **Step 2: Write the failing collision test**

Drive `createNewTopic` through `processMessage`: a distinct topic already occupies the base slug, and the model declares a *new* topic whose title slugifies to the same slug. Without the guard the new topic overwrites the existing file; with it, the new one lands at `-2`. The test deps' embedder throws, so the topic-match step takes the tool-enabled fallback (one scripted reply). Append to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (uses the file's `deps`/`sampleMessage`/`FakeMessages` helpers):

```fsharp
[<Fact>]
let ``createNewTopic does not overwrite a slug-colliding existing topic`` () =
    let vault = FakeVault()
    // A distinct existing topic already occupies the base slug path.
    vault.Seed("topics/active/birthday-party.md",
               "---\ntype: Topic\ntitle: Birthday party\nstatus: active\n---\n## Current understanding\nExisting.\n")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"party planning","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    // Embedder throws in the test deps -> topic-match falls back to the tool-enabled path -> this reply.
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Birthday party"}"""
    let topicUpdate = Responses.final "STATUS: active\n## Current understanding\nNew party.\n"
    let chat = FakeChatClient([ classify; topicMatch; topicUpdate ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // existing file untouched; the colliding new topic lands at -2
    Assert.Contains("Existing.", vault.Files.["topics/active/birthday-party.md"])
    Assert.True(vault.Files.ContainsKey "topics/active/birthday-party-2.md")
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests" -p:nodeReuse=false`
Expected: PASS (existing PipelineTests + the new one).

- [ ] **Step 4: Run the full Core project**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS (behaviour-preserving except the new guard).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "fix: route createNewTopic write through freePath collision guard"
```

---

## Task 3: Thread the offset through the Pipeline

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`PipelineDeps` + `isoTimestamp`/`laterIso` → local closures)
- Modify: `src/Nameless.TaskList/Program.fs` (read `Vault:UtcOffsetHours`; pass `UtcOffset` in `buildDeps`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (add `UtcOffset` to every `PipelineDeps` literal)
- Modify: `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` (add `UtcOffset`)

**Interfaces:**
- Consumes: `KnowledgeBase.Time.iso` (Task 1).
- Produces: `PipelineDeps.UtcOffset : System.TimeSpan`.

- [ ] **Step 1: Add `UtcOffset` to `PipelineDeps` and make the timestamp helpers offset-aware**

In `src/Nameless.TaskList.Core/Pipeline.fs`, add a field to `PipelineDeps` (after `Transcriber`):

```fsharp
          Vision: IVision
          Transcriber: ITranscriber
          UtcOffset: System.TimeSpan }
```

Delete the module-level `let private isoTimestamp …` (line ~33) and `let private laterIso …` (lines ~48-52). Inside `processMessage`, at the very top of the `| Some msg ->` body (before `channelSlug`), add local closures bound to `deps.UtcOffset`:

```fsharp
            let isoTimestamp (ts: System.DateTime) = Time.iso deps.UtcOffset ts
            let laterIso (existing: string) (ts: System.DateTime) : string =
                let nu = isoTimestamp ts
                match System.DateTimeOffset.TryParse existing, System.DateTimeOffset.TryParse nu with
                | (true, prev), (true, cur) -> if prev > cur then existing else nu
                | _ -> nu
```

(Every existing `isoTimestamp ts` / `laterIso …` call site inside `processMessage` now binds to these closures — no other change.) Confirm with `grep -nE "isoTimestamp|laterIso" src/Nameless.TaskList.Core/Pipeline.fs` that the only definitions are the two closures and there are no remaining module-level references.

- [ ] **Step 2: Wire config + `buildDeps` in the host**

In `src/Nameless.TaskList/Program.fs`, add the offset binding where `cfg` is in scope and BEFORE `buildDeps` is defined (so `buildDeps` can capture it):

```fsharp
        let kbOffset =
            match System.Double.TryParse(cfg.["Vault:UtcOffsetHours"]) with
            | true, h -> System.TimeSpan.FromHours h
            | _ -> System.TimeSpan.FromHours 2.0
```

In the `PipelineDeps` record built in `buildDeps` (the `{ Messages = …; … Vision = vision; Transcriber = transcriber }` literal), add the field:

```fsharp
              Vision = vision; Transcriber = transcriber; UtcOffset = kbOffset }
```

Add the config default to `src/Nameless.TaskList/appsettings.json` under `Vault`:

```json
  "Vault": { "Root": "/data/@documents/Synced-Vault/Knowledge-Base", "UtcOffsetHours": 2.0 },
```

- [ ] **Step 3: Update test `PipelineDeps` literals**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, add `UtcOffset = System.TimeSpan.FromHours 2.0` to **every** `PipelineDeps` record literal (the `{ Messages = …; … Transcriber = … }` blocks — there are several; add the field to each, after `Transcriber`). In `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs`, add the same field to its `deps` record.

- [ ] **Step 4: Write the failing offset test**

A noise message writes a Message record with `Timestamp = isoTimestamp msg.Timestamp`. `sampleMessage ()` has `Timestamp = DateTime(2026, 6, 15, 14, 17, 45, Utc)`; with `UtcOffset = 5.5` the written timestamp must read `+05:30`. Use the file's `deps` builder with a record-update override. Append to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``message timestamp uses the configured UtcOffset`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"ack","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let d = { deps (FakeMessages(Some(sampleMessage ()))) vault chat with UtcOffset = System.TimeSpan.FromHours 5.5 }
    Pipeline.processMessage d "M1" "jid" |> ignore
    let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
    Assert.Contains("+05:30", vault.Files.[msgKey])
```

- [ ] **Step 5: Run the tests + build**

Run: `dotnet build -p:nodeReuse=false 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings (host + tests compile with the new field).

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS — full Core project green; the new offset test passes; all existing tests still pass (default `+02:00` unchanged on this host).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json \
        tests/Nameless.TaskList.Core.Tests/PipelineTests.fs tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs
git commit -m "feat: thread configurable UtcOffset through the pipeline timestamps"
```

---

## Task 4: Thread the offset through the Digest

**Files:**
- Modify: `src/Nameless.TaskList.Core/Digest.fs` (`DigestDeps` + `Generated`)
- Modify: `src/Nameless.TaskList/Scheduler.fs` (the host `MaintenanceTasks.digest`)
- Modify: `tests/Nameless.TaskList.Core.Tests/DigestTests.fs` (add `UtcOffset` to `DigestDeps` literals)

**Interfaces:**
- Consumes: `KnowledgeBase.Time.iso`, `KnowledgeBase.Time.now` (Task 1).
- Produces: `DigestDeps.UtcOffset : System.TimeSpan`.

- [ ] **Step 1: Add `UtcOffset` to `DigestDeps`; format `Generated` with it**

In `src/Nameless.TaskList.Core/Digest.fs`, extend `DigestDeps` (line ~16):

```fsharp
    type DigestDeps =
        { Vault: IVault; Chat: IChatClient; Model: string; Today: System.DateTime; UtcOffset: System.TimeSpan }
```

Change `Generated` (line ~137) from `deps.Today.ToString("yyyy-MM-ddTHH:mm:sszzz")` to:

```fsharp
              Kind = kind; Generated = Time.iso deps.UtcOffset deps.Today }
```

(Ensure `open Nameless.TaskList.Core.KnowledgeBase` is present in `Digest.fs` so `Time` resolves; it almost certainly is, since `Digest` uses `MarkdownFile`/`Frontmatter`.)

- [ ] **Step 2: Wire the host digest task**

In `src/Nameless.TaskList/Scheduler.fs`, add a shared offset helper to `MaintenanceTasks` (used by both digest and reindex) and use the configured-offset "now" for `Today`:

```fsharp
    let kbOffset (cfg: IConfiguration) : System.TimeSpan =
        match System.Double.TryParse(cfg.["Vault:UtcOffsetHours"]) with
        | true, h -> System.TimeSpan.FromHours h
        | _ -> System.TimeSpan.FromHours 2.0
```

Change the `digest` invocation (line ~24) so `Today` is the configured-offset now and `UtcOffset` is supplied:

```fsharp
        let off = kbOffset cfg
        Digest.generate { Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
                          Today = (KnowledgeBase.Time.now off).DateTime; UtcOffset = off } p
```

(`open Nameless.TaskList.Core` / `KnowledgeBase` as the file already does; match its existing opens.)

- [ ] **Step 3: Update `DigestTests` literals**

In `tests/Nameless.TaskList.Core.Tests/DigestTests.fs`, add `UtcOffset = System.TimeSpan.FromHours 2.0` to every `DigestDeps` record literal (`{ Vault = …; Chat = …; Model = …; Today = … }` → add the field). No assertion changes needed — the tests do not assert the `Generated` value.

- [ ] **Step 4: Write the failing test**

`Digest.generate` writes the digest file (with the `generated:` frontmatter) to the vault at `DigestResult.Path`. With `Today = DateTime(2026,6,23)` and `UtcOffset = 5.5`, `Generated = "2026-06-23T00:00:00+05:30"`. Append to `tests/Nameless.TaskList.Core.Tests/DigestTests.fs` (uses the file's `seed`/`proseChat`/`today`):

```fsharp
[<Fact>]
let ``digest generated timestamp carries the configured offset`` () =
    let v = seed ()
    let d = { Vault = v :> IVault; Chat = proseChat "BODY"; Model = "m"; Today = today; UtcOffset = System.TimeSpan.FromHours 5.5 }
    let r = Digest.generate d DigestParams.daily
    Assert.Contains("+05:30", v.Files.[r.Path])
```

- [ ] **Step 5: Run the tests + build**

Run: `dotnet build -p:nodeReuse=false 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Digest.fs src/Nameless.TaskList/Scheduler.fs tests/Nameless.TaskList.Core.Tests/DigestTests.fs
git commit -m "feat: thread configurable UtcOffset through the digest Generated timestamp"
```

---

## Task 5: Correct the Indexer `last_updated`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Indexer.fs` (`writeIndex` + each `render*` + `regenerate`)
- Modify: `src/Nameless.TaskList/Scheduler.fs` (`MaintenanceTasks.reindex` passes a configured-offset `now`)
- Modify: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs` (add a `last_updated` assertion)

**Interfaces:**
- Consumes: `KnowledgeBase.Time.now`, `MaintenanceTasks.kbOffset` (Task 4).
- Produces: `Indexer.regenerate` now derives `last_updated` from its `now` argument (no new public signature; the `now : DateTime` argument is reused).

- [ ] **Step 1: Thread a `lastUpdated` string from `regenerate` to `writeIndex`**

The Indexer currently hardcodes `DateTime.UtcNow` in `writeIndex`. Make `writeIndex` take a pre-computed `lastUpdated` string and have `regenerate` derive it from the `now` it is already given (so the offset enters only via the host's `now`).

In `src/Nameless.TaskList.Core/Indexer.fs`:

- Change `writeIndex` (line ~60):

```fsharp
    let private writeIndex (vault: IVault) (root: string) (title: string) (body: string) (lastUpdated: string) =
        let meta : IndexMeta = { Type = "Index"; Title = title; LastUpdated = lastUpdated }
        vault.Write(root.TrimEnd('/') + "/index.md", MarkdownFile.ToString (Frontmatter.serialize meta) body)
```

- Add a `(lastUpdated: string)` parameter to every `render*` function that calls `writeIndex` (`renderTasks`, `renderTopics`, `renderChannels`, `renderEvents`, `renderCommitments`, `renderNotes`, `renderPeople`, `renderRelationships`) and forward it to their `writeIndex …` call. For example `renderTasks`:

```fsharp
    let private renderTasks (vault: IVault) (lastUpdated: string) : int * int =
        // … unchanged body …
        writeIndex vault "tasks" "Task Index" (sb.ToString().TrimEnd()) lastUpdated
```

(Repeat the `(lastUpdated: string)` parameter + the trailing `lastUpdated` argument on the `writeIndex` call for each of the eight render functions — the bodies are otherwise unchanged.)

- In `regenerate` (line ~229), compute `lastUpdated` once from the `now` argument and pass it to each `render*` call:

```fsharp
    let regenerate (vault: IVault) (cfg: TopicSweepConfig) (now: System.DateTime) : IndexSummary =
        let lastUpdated = now.ToString("yyyy-MM-dd")
        // … each `renderX vault` call becomes `renderX vault lastUpdated` …
```

(Update every `render*` invocation inside `regenerate` to pass `lastUpdated`.)

- [ ] **Step 2: Host passes a configured-offset `now` to `reindex`**

In `src/Nameless.TaskList/Scheduler.fs`, change `MaintenanceTasks.reindex` (line ~20-21) so `now` is the configured-offset now:

```fsharp
    let reindex (cfg: IConfiguration) (vault: IVault) : Indexer.IndexSummary =
        Indexer.regenerate vault (topicCfg cfg) (KnowledgeBase.Time.now (kbOffset cfg)).DateTime
```

- [ ] **Step 3: Write the failing test**

Append to `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs` (it already seeds topic files and calls `Indexer.regenerate`):

The file already defines `let private cfg : Indexer.TopicSweepConfig = { ResolvedArchiveAfterDays = 14; DormantArchiveAfterDays = 90 }`. Append to `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`:

```fsharp
[<Fact>]
let ``index last_updated uses the regenerate now date`` () =
    let v = FakeVault()
    v.Seed("topics/active/t1.md", "---\ntype: Topic\ntitle: T1\nstatus: active\nlast_updated: 2026-06-15\n---\nb")
    Indexer.regenerate (v :> IVault) cfg (System.DateTime(2026, 7, 9, 1, 0, 0)) |> ignore
    Assert.Contains("last_updated: 2026-07-09", v.Files.["topics/index.md"])
```

(The topics index frontmatter's `last_updated` is now the `now` date passed to `regenerate`, not `DateTime.UtcNow`.)

- [ ] **Step 4: Run the tests + build**

Run: `dotnet build -p:nodeReuse=false 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS — the new test passes; existing IndexerTests still pass (they do not assert the index meta `last_updated`).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Indexer.fs src/Nameless.TaskList/Scheduler.fs tests/Nameless.TaskList.Core.Tests/IndexerTests.fs
git commit -m "fix: derive Indexer last_updated from the configured-offset now (was UtcNow)"
```

---

## Task 6: Make the Adapters/Email read-shift offset configurable

**Files:**
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (`PostgresMessageSource` ctor + `readKbTimestamp`/`toInstantParam`/`mapChat`)
- Modify: `src/Nameless.TaskList.Core/Email.fs` (`toChatMessage`)
- Modify: `src/Nameless.TaskList/Program.fs` + `src/Nameless.TaskList/ImapPoller.fs` callers (pass the configured offset)
- Modify: `tests/Nameless.TaskList.Core.Tests/EmailTests.fs` (offset assertion); check `AdapterWireTests.fs` compiles

**Interfaces:**
- Consumes: the host `kbOffset` (Task 3 in Program; `MaintenanceTasks.kbOffset` in Scheduler).
- Produces:
  - `PostgresMessageSource(connectionString: string, utcOffset: System.TimeSpan)` (+ a back-compat `new(connectionString)` defaulting to `+02:00`)
  - `Email.toChatMessage` gains a leading `(utcOffset: System.TimeSpan)` parameter.

- [ ] **Step 1: Make the Postgres read-shift offset an instance value**

In `src/Nameless.TaskList.Core/Adapters.fs`:

- Delete `let internal sastOffset = System.TimeSpan.FromHours 2.0` (line ~221).
- Give the three module-level helpers an explicit offset parameter:

```fsharp
    let private readKbTimestamp (offset: System.TimeSpan) (reader: NpgsqlDataReader) (col: string) : System.DateTime =
        (reader.GetFieldValue<System.DateTimeOffset>(reader.GetOrdinal col)).ToOffset(offset).DateTime

    let private toInstantParam (offset: System.TimeSpan) (ts: System.DateTime) : System.DateTime =
        System.DateTimeOffset(System.DateTime.SpecifyKind(ts, System.DateTimeKind.Unspecified), offset).UtcDateTime
```

- `mapChat` calls `readKbTimestamp`; give it the offset too:

```fsharp
    let private mapChat (offset: System.TimeSpan) (reader: NpgsqlDataReader) : ChatMessage =
        { Id = reader.GetString(reader.GetOrdinal("id"))
          // … unchanged …
          Timestamp = readKbTimestamp offset reader "timestamp" }
```

- `PostgresMessageSource` gains the offset (with a back-compat 1-arg ctor) and passes it to `mapChat`/`toInstantParam` at every call site inside the class:

```fsharp
    type PostgresMessageSource(connectionString: string, utcOffset: System.TimeSpan) =
        new(connectionString: string) = PostgresMessageSource(connectionString, System.TimeSpan.FromHours 2.0)
        // … in each method, `mapChat reader` becomes `mapChat utcOffset reader`,
        //    and any `toInstantParam ts` becomes `toInstantParam utcOffset ts` …
```

Use `grep -nE "mapChat|toInstantParam" src/Nameless.TaskList.Core/Adapters.fs` to find and update every call site to pass `utcOffset`.

- [ ] **Step 2: Make the Email read-shift offset a parameter**

In `src/Nameless.TaskList.Core/Email.fs`:

- Delete `let private sastOffset = TimeSpan.FromHours 2.0` (line ~9).
- Add a leading `(utcOffset: System.TimeSpan)` parameter to `toChatMessage` and use it (line ~82 `email.Date.ToOffset(sastOffset)` → `email.Date.ToOffset(utcOffset)`):

```fsharp
    let toChatMessage (utcOffset: System.TimeSpan) (email: RawEmail) : ChatMessage =
        // … unchanged …
          Timestamp = email.Date.ToOffset(utcOffset).DateTime }
```

Update `toChatMessage`'s caller(s) — `grep -rnE "toChatMessage" src/Nameless.TaskList.Core/*.fs src/Nameless.TaskList/*.fs` — to pass the offset (the `EmailPoller`/`ImapMessageSource` path; thread an offset from the poller's construction).

- [ ] **Step 3: Wire the host callers**

In `src/Nameless.TaskList/Program.fs`, the Postgres source construction (line ~28) passes `kbOffset`:

```fsharp
            PostgresMessageSource(cfg.GetConnectionString("WhatsApp"), kbOffset) :> IMessageSource) |> ignore
```

For the email path, thread `kbOffset` from `Program.fs` into the `ImapPoller`/`Email.toChatMessage` call chain (`src/Nameless.TaskList/ImapPoller.fs` and the `buildEmailDeps`/poller construction in `Program.fs`) so emails are read with the configured offset. (Match the existing poller wiring; add an `utcOffset: System.TimeSpan` parameter where the poller calls `Email.toChatMessage`.)

The integration test `tests/Nameless.TaskList.IntegrationTests/Support.fs` constructs `PostgresMessageSource(Config.connectionString)` (1-arg) — the back-compat ctor keeps it compiling unchanged.

- [ ] **Step 4: Update existing calls + write the failing email-offset test**

First, update **every existing** `Email.toChatMessage (raw ())` / `Email.toChatMessage { raw () with … }` call in `tests/Nameless.TaskList.Core.Tests/EmailTests.fs` (there are ~4: the identity/SAST-timestamp, subject-prepend, blank-subject, and bulk-broadcast tests) to pass the new leading offset arg `(System.TimeSpan.FromHours 2.0)`, e.g. `Email.toChatMessage (System.TimeSpan.FromHours 2.0) (raw ())`. The existing "maps … SAST timestamp" assertion (12:17 UTC → 14:17 SAST) still holds at offset 2.0.

Then append a test proving a non-default offset is applied. The file's `raw ()` has `Date = DateTimeOffset(2026, 6, 15, 12, 17, 45, TimeSpan.Zero)` (12:17:45 UTC); `.ToOffset(+05:30)` → 17:47:45 wall-clock:

```fsharp
[<Fact>]
let ``toChatMessage applies the supplied offset to the timestamp`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 5.5) (raw ())
    Assert.Equal(System.DateTime(2026, 6, 15, 17, 47, 45), m.Timestamp)
```

- [ ] **Step 5: Run the tests + build**

Run: `dotnet build -p:nodeReuse=false 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings (host + Core + tests + integration project all compile with the new ctor/param; the back-compat ctor covers the 1-arg integration call).

Run: `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1`
Expected: PASS — the new email-offset test passes; existing EmailTests/AdapterWireTests pass with the updated calls.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Adapters.fs src/Nameless.TaskList.Core/Email.fs \
        src/Nameless.TaskList/Program.fs src/Nameless.TaskList/ImapPoller.fs \
        tests/Nameless.TaskList.Core.Tests/EmailTests.fs
git commit -m "feat: make the Postgres/email read-shift offset configurable (default +02:00)"
```

---

## Final verification

- [ ] Run `dotnet build -p:nodeReuse=false` — all projects compile, 0 warnings.
- [ ] Run `dotnet test tests/Nameless.TaskList.Core.Tests -p:nodeReuse=false -maxcpucount:1` and `dotnet test tests/Nameless.TaskList.Tests -p:nodeReuse=false -maxcpucount:1` — both green.
- [ ] `grep -rnE "FromHours 2.0|sastOffset" src/Nameless.TaskList.Core` — the only remaining `FromHours 2.0` are the documented **defaults** (the back-compat `PostgresMessageSource` ctor and the host `kbOffset` fallbacks); no live read/write path hardcodes the offset.
- [ ] Confirm `git status` clean and the branch holds the six task commits.
