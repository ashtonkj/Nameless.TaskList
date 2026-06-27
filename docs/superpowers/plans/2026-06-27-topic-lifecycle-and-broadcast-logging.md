# Topic Lifecycle & Broadcast Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give topics a lifecycle (active → resolved → archived) via a status frontmatter flag, and turn broadcast feeds into message-only logs — without violating the vault's never-delete invariant.

**Architecture:** Status is a frontmatter flag; files never move, so no backlinks break. Resolution is judged inline (folded into the existing topic-update LLM call, conservative default-active). Archival is a deterministic, LLM-free sweep inside `/reindex`. Broadcast non-noise messages short-circuit to a new `Logged` result before any topic/entity work.

**Tech Stack:** F# / .NET 10, xUnit, in-memory `FakeVault`/`FakeChatClient`/`FakeMessages`.

## Global Constraints

- The vault never deletes or moves files; all topics stay at `topics/active/{slug}.md`. Lifecycle is expressed only through the `status` frontmatter field (`active` / `resolved` / `archived`).
- Resolution defaults to `active` on any parse doubt (granite is unreliable; a false "still active" only delays archival).
- Only matched (existing) topics may become `resolved`; a newly created topic is always `active`.
- Archive thresholds are config with defaults: `Topics:ResolvedArchiveAfterDays` = 14, `Topics:DormantArchiveAfterDays` = 90.
- Run `dotnet test` (Core + endpoint) after each task; all must pass.

---

### Task 1: `parseTopicUpdate` + resolution marker in the topic-update prompt

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (`topicUpdateSystem` string; add `parseTopicUpdate`)
- Test: `tests/Nameless.TaskList.Core.Tests/ParsingTests.fs`

**Interfaces:**
- Produces: `Prompts.parseTopicUpdate : string -> bool * string` — returns `(resolved, body)`. Reads the first non-blank line; if it matches `^\s*STATUS:\s*(resolved|active)\b` (case-insensitive) it sets `resolved` and strips that line from the body; otherwise `resolved = false` and the whole input is the body.

- [ ] **Step 1: Write the failing tests**

In `ParsingTests.fs`, after the existing classification tests, add:

```fsharp
[<Fact>]
let ``parseTopicUpdate reads a resolved marker and strips it from the body`` () =
    let raw = "STATUS: resolved\n## Current understanding\nDone.\n"
    let resolved, body = Prompts.parseTopicUpdate raw
    Assert.True(resolved)
    Assert.Equal("## Current understanding\nDone.", body.Trim())

[<Fact>]
let ``parseTopicUpdate reads an active marker (case-insensitive) and strips it`` () =
    let resolved, body = Prompts.parseTopicUpdate "  status:  Active \n## Current understanding\nx"
    Assert.False(resolved)
    Assert.DoesNotContain("status", body.ToLowerInvariant())

[<Fact>]
let ``parseTopicUpdate defaults to active and keeps the whole body when no marker`` () =
    let raw = "## Current understanding\nNo marker here.\n"
    let resolved, body = Prompts.parseTopicUpdate raw
    Assert.False(resolved)
    Assert.Equal(raw, body)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~parseTopicUpdate"`
Expected: FAIL — `parseTopicUpdate` is not defined.

- [ ] **Step 3: Implement `parseTopicUpdate`**

In `Prompts.fs`, add near the other parsers (after `parseRelationships`):

```fsharp
    /// Split a topic-update reply into (resolved, body). The model is asked to begin with a
    /// single line "STATUS: active|resolved"; we read the first non-blank line, and on a match
    /// strip it and set the flag. Anything else defaults to active with the whole reply as body
    /// — conservative, since a wrongly-"resolved" topic would hide live work.
    let parseTopicUpdate (raw: string) : bool * string =
        let text = if isNull raw then "" else raw
        let m = System.Text.RegularExpressions.Regex.Match(text, @"^\s*STATUS:\s*(resolved|active)\b\s*",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        if m.Success && m.Index = 0 then
            let resolved = m.Groups.[1].Value.ToLowerInvariant() = "resolved"
            (resolved, text.Substring(m.Length))
        else
            (false, text)
```

- [ ] **Step 4: Update `topicUpdateSystem` to emit the marker**

In `Prompts.fs`, find the `topicUpdateSystem` string. Its body currently ends with the "CONVERSATION HISTORY — STRICT SCOPE" block and `Respond ONLY with the updated markdown body...`. Replace that final `Respond ONLY...` line with:

```
Begin your reply with a single line that is exactly "STATUS: active" or "STATUS: resolved" —
"resolved" only when this thread's matter is now concluded (its open questions answered, nothing
left to do or track); otherwise "active". Then, on the following lines, respond with ONLY the
updated markdown body (no frontmatter, no other explanation).
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~parseTopicUpdate"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs tests/Nameless.TaskList.Core.Tests/ParsingTests.fs
git commit -m "feat: parse a STATUS resolution marker from topic-update replies"
```

---

### Task 2: Apply resolution + re-activation in the topic-update step

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (topic-match `topicOutcome` to carry `isNew`; topic-update step)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `Prompts.parseTopicUpdate`.
- Produces: after processing, a matched topic whose update reply begins `STATUS: resolved` has `status: resolved`; a matched topic otherwise has `status: active` (re-activating a previously-resolved one); a newly created topic stays `status: active` regardless of the marker.

- [ ] **Step 1: Write the failing tests**

In `PipelineTests.fs`, near the other topic tests, add:

```fsharp
[<Fact>]
let ``a matched topic whose update says resolved becomes status resolved`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") || t.Contains("party") || t.Contains("Birthday") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    let confirm = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let topicBody = Responses.final "STATUS: resolved\n## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ signalClassify; confirm; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Contains("status: resolved", vault.Files.[topic])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a new topic is never marked resolved even if the reply says so`` () =
    let vault = FakeVault()
    let embedder = FakeEmbedder(fun _ -> [| 0.0; 1.0 |]) :> IEmbedder   // nothing similar -> new topic, no match LLM call
    let topicBody = Responses.final "STATUS: resolved\n## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ signalClassify; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Contains("status: active", vault.Files.[topic])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a new message matching a resolved topic re-activates it`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/birthday-party.md",
               "---\ntype: Topic\ntitle: Birthday party\ndescription: d\nstatus: resolved\ncontext:\n  - family\n" +
               "first_seen: 2026-06-10T09:00:00+02:00\nlast_updated: 2026-06-11T09:00:00+02:00\n---\n## Current understanding\nplanning the party\n\n## Open questions\n\n## Resolved\n")
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") || t.Contains("party") || t.Contains("Birthday") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    let confirm = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let topicBody = Responses.final "STATUS: active\n## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ signalClassify; confirm; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Contains("status: active", vault.Files.[topic])
    | other -> failwithf "expected Processed, got %A" other
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~resolved topic|never marked resolved|re-activates"`
Expected: FAIL — topics are written `status: active` (resolution not applied) / re-activation absent.

- [ ] **Step 3: Make `topicOutcome` carry an `isNew` flag**

In `Pipeline.fs`, the topic-match builds `let topicOutcome : Result<string * string, string> =`. Change it to `Result<string * string * bool, string>` where the bool is `isNew` (true for created topics). Concretely:
- `createNewTopic` arms: wrap as `let (s, p) = createNewTopic (...) in Ok (s, p, true)`.
- The matched arm `| true, Some (slug, _, _, _) -> Ok (slug, Naming.topicPath slug)` becomes `Ok (slug, Naming.topicPath slug, false)`.
- Every other arm that returns a created topic uses `, true)`; the tool-fallback matched arm uses `, false)`.

Then the consumer `| Ok (_, topicPath) ->` becomes `| Ok (_, topicPath, isNewTopic) ->`.

- [ ] **Step 4: Apply resolution in the topic-update step**

In `Pipeline.fs`, in the `--- Step: update the topic body ---` block, the call is currently:

```fsharp
                let newBody = Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user
                match existing.FrontMatter with
                | Some fm ->
                    let t = Frontmatter.deserialize<Topic> fm
                    let merged =
                        { t with
                            LastUpdated = laterIso t.LastUpdated msg.Timestamp
```

Replace the `let newBody = ...` line and add status handling, so it reads:

```fsharp
                let resolved, newBody = Prompts.parseTopicUpdate (Agent.runConversation deps.Chat [] Prompts.topicUpdateSystem user)
                match existing.FrontMatter with
                | Some fm ->
                    let t = Frontmatter.deserialize<Topic> fm
                    // New topics are always active; a matched topic is resolved only when the
                    // model said so this turn, otherwise active (re-activating a resolved one).
                    let newStatus = if isNewTopic then "active" elif resolved then "resolved" else "active"
                    let merged =
                        { t with
                            Status = newStatus
                            LastUpdated = laterIso t.LastUpdated msg.Timestamp
```

(Leave the rest of the `merged` record and the body write unchanged — `newBody` is still used downstream by `linkifyPeople`/`withLinkedPeople`.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~resolved topic|never marked resolved|re-activates"`
Expected: PASS (3 tests). Then run the whole Core suite to catch fallout from the tuple change: `dotnet test tests/Nameless.TaskList.Core.Tests` → all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: set topic status from inline resolution; re-activate matched resolved topics"
```

---

### Task 3: `nextTopicStatus` pure function + sweep config type

**Files:**
- Modify: `src/Nameless.TaskList.Core/Indexer.fs` (add `TopicSweepConfig`, `nextTopicStatus`)
- Test: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`

**Interfaces:**
- Produces:
  - `Indexer.TopicSweepConfig = { ResolvedArchiveAfterDays: int; DormantArchiveAfterDays: int }`
  - `Indexer.nextTopicStatus : TopicSweepConfig -> System.DateTime -> string -> string -> string option` — args `(config) (now) (status) (lastUpdated)`; returns `Some newStatus` when it changed, `None` when unchanged or undecidable.

- [ ] **Step 1: Write the failing tests**

In `IndexerTests.fs`, add:

```fsharp
open System

let private cfg : Indexer.TopicSweepConfig = { ResolvedArchiveAfterDays = 14; DormantArchiveAfterDays = 90 }
let private now = DateTime(2026, 6, 27, 12, 0, 0)
let private daysAgo (d: int) = now.AddDays(float -d).ToString("yyyy-MM-ddTHH:mm:sszzz")

[<Fact>]
let ``active topic idle past the dormant threshold is archived`` () =
    Assert.Equal(Some "archived", Indexer.nextTopicStatus cfg now "active" (daysAgo 100))

[<Fact>]
let ``active topic within the dormant threshold is unchanged`` () =
    Assert.Equal(None, Indexer.nextTopicStatus cfg now "active" (daysAgo 30))

[<Fact>]
let ``resolved topic idle past the resolved threshold is archived`` () =
    Assert.Equal(Some "archived", Indexer.nextTopicStatus cfg now "resolved" (daysAgo 20))

[<Fact>]
let ``resolved topic within the resolved threshold is unchanged`` () =
    Assert.Equal(None, Indexer.nextTopicStatus cfg now "resolved" (daysAgo 5))

[<Fact>]
let ``already-archived and unparseable dates are left unchanged`` () =
    Assert.Equal(None, Indexer.nextTopicStatus cfg now "archived" (daysAgo 999))
    Assert.Equal(None, Indexer.nextTopicStatus cfg now "active" "not-a-date")
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~topic idle|threshold is unchanged|unparseable dates"`
Expected: FAIL — `nextTopicStatus` / `TopicSweepConfig` not defined.

- [ ] **Step 3: Implement the type and function**

In `Indexer.fs`, inside `module Indexer`, near the top (after the `IndexSummary` type), add:

```fsharp
    type TopicSweepConfig = { ResolvedArchiveAfterDays: int; DormantArchiveAfterDays: int }

    /// Decide a topic's next status from its current status, last-updated time, and now.
    /// Pure. `Some s` when it should change; `None` when unchanged or the date can't be read.
    let nextTopicStatus (cfg: TopicSweepConfig) (now: System.DateTime) (status: string) (lastUpdated: string) : string option =
        let s = (if isNull status then "active" else status).Trim().ToLowerInvariant()
        match System.DateTimeOffset.TryParse lastUpdated with
        | true, ts ->
            let idleDays = (now - ts.LocalDateTime).TotalDays
            match s with
            | "active"   when idleDays >= float cfg.DormantArchiveAfterDays  -> Some "archived"
            | "resolved" when idleDays >= float cfg.ResolvedArchiveAfterDays -> Some "archived"
            | _ -> None
        | _ -> None
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~topic idle|threshold is unchanged|unparseable dates"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Indexer.fs tests/Nameless.TaskList.Core.Tests/IndexerTests.fs
git commit -m "feat: nextTopicStatus pure dormancy rule + sweep config"
```

---

### Task 4: `sweepTopics`, wire into `/reindex`, config

**Files:**
- Modify: `src/Nameless.TaskList.Core/Indexer.fs` (`sweepTopics`; `regenerate` signature)
- Modify: `src/Nameless.TaskList/Program.fs` (read `Topics:` config, pass to `regenerate`)
- Modify: `src/Nameless.TaskList/appsettings.json` (add `Topics`)
- Modify: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs` (update existing `regenerate` callers)
- Test: `tests/Nameless.TaskList.Core.Tests/IndexerTests.fs`

**Interfaces:**
- Consumes: `Indexer.nextTopicStatus`, `Indexer.TopicSweepConfig`.
- Produces:
  - `Indexer.sweepTopics : IVault -> TopicSweepConfig -> System.DateTime -> int` — applies `nextTopicStatus` to every topic, writes back changed ones (same path, body preserved), prunes newly-`archived` slugs from every channel's `active_topics`; returns the number of topics changed.
  - `Indexer.regenerate : IVault -> TopicSweepConfig -> System.DateTime -> IndexSummary` — now runs `sweepTopics` first, then the existing index rendering.

- [ ] **Step 1: Write the failing test**

In `IndexerTests.fs`, add (reuse `cfg`/`now`/`daysAgo` from Task 3):

```fsharp
[<Fact>]
let ``sweepTopics archives a stale active topic and prunes it from its channel`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/old-thread.md",
               sprintf "---\ntype: Topic\ntitle: Old thread\ndescription: d\nstatus: active\ncontext:\n  - family\nchannel: wife\nfirst_seen: %s\nlast_updated: %s\n---\nbody\n" (daysAgo 200) (daysAgo 200))
    vault.Seed("topics/active/fresh.md",
               sprintf "---\ntype: Topic\ntitle: Fresh\ndescription: d\nstatus: active\ncontext:\n  - family\nchannel: wife\nfirst_seen: %s\nlast_updated: %s\n---\nbody\n" (daysAgo 1) (daysAgo 1))
    vault.Seed("channels/whatsapp/wife.md",
               "---\ntype: Channel\ntitle: Wife\nplatform: whatsapp-direct\ncontext: ''\npeople: []\nsignal_weight: medium\nmessage_count: 2\nlast_processed: 2026-06-20T00:00:00\nactive_topics:\n  - topics/active/old-thread.md\n  - topics/active/fresh.md\n---\n")
    let changed = Indexer.sweepTopics vault cfg now
    Assert.Equal(1, changed)
    Assert.Contains("status: archived", vault.Files.["topics/active/old-thread.md"])
    Assert.Contains("status: active", vault.Files.["topics/active/fresh.md"])
    let channel = vault.Files.["channels/whatsapp/wife.md"]
    Assert.DoesNotContain("topics/active/old-thread.md", channel)
    Assert.Contains("topics/active/fresh.md", channel)
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~sweepTopics archives"`
Expected: FAIL — `sweepTopics` not defined.

- [ ] **Step 3: Implement `sweepTopics` and update `regenerate`**

In `Indexer.fs`, add `sweepTopics` (above `regenerate`). It reuses the module's existing `loadAll`/helpers pattern; here is a self-contained implementation:

```fsharp
    /// Apply the dormancy rule to every topic, persist status changes (same path, body kept),
    /// and prune any newly-archived topic from every channel's active_topics. Returns #changed.
    let sweepTopics (vault: IVault) (cfg: TopicSweepConfig) (now: System.DateTime) : int =
        let newlyArchived = System.Collections.Generic.HashSet<string>()
        let mutable changed = 0
        for path in vault.ListFilesRecursive "topics" do
            if not (path.EndsWith "index.md") then
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Topic> fm
                        match nextTopicStatus cfg now t.Status t.LastUpdated with
                        | Some s ->
                            if s = "archived" then newlyArchived.Add path |> ignore
                            vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize { t with Status = s }) mf.Content)
                            changed <- changed + 1
                        | None -> ()
                    | None -> ()
                with _ -> ()
        if newlyArchived.Count > 0 then
            for path in vault.ListFilesRecursive "channels" do
                if not (path.EndsWith "index.md") then
                    try
                        let mf = MarkdownFile.FromString (vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let c = Frontmatter.deserialize<Channel> fm
                            let kept = (if isNull c.ActiveTopics then [||] else c.ActiveTopics) |> Array.filter (fun tp -> not (newlyArchived.Contains tp))
                            if kept.Length <> (if isNull c.ActiveTopics then 0 else c.ActiveTopics.Length) then
                                vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize { c with ActiveTopics = kept }) mf.Content)
                        | None -> ()
                    with _ -> ()
        changed
```

Then change `regenerate` to run the sweep first and accept config + now:

```fsharp
    let regenerate (vault: IVault) (cfg: TopicSweepConfig) (now: System.DateTime) : IndexSummary =
        sweepTopics vault cfg now |> ignore
        let tCount, tSkip = renderTasks vault
        // ... (rest unchanged) ...
```

- [ ] **Step 4: Update the existing `regenerate` callers**

In `IndexerTests.fs`, every existing `Indexer.regenerate vault` call becomes `Indexer.regenerate vault cfg now`.

In `src/Nameless.TaskList/Program.fs`, the `/reindex` handler line `try Indexer.regenerate vault |> ReindexHandler.toHttp` becomes:

```fsharp
                let topicCfg : Indexer.TopicSweepConfig =
                    { ResolvedArchiveAfterDays = (match System.Int32.TryParse(cfg.["Topics:ResolvedArchiveAfterDays"]) with | true, n -> n | _ -> 14)
                      DormantArchiveAfterDays = (match System.Int32.TryParse(cfg.["Topics:DormantArchiveAfterDays"]) with | true, n -> n | _ -> 90) }
                try Indexer.regenerate vault topicCfg System.DateTime.Now |> ReindexHandler.toHttp
```

(`cfg` is the `IConfiguration` already in scope in `Program.fs`; confirm the local name — it is read the same way as `cfg.["Ollama:Model"]` elsewhere in the file.)

In `src/Nameless.TaskList/appsettings.json`, add a sibling of the `Ollama` object:

```json
  "Topics": { "ResolvedArchiveAfterDays": 14, "DormantArchiveAfterDays": 90 },
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~sweepTopics archives"` → PASS.
Then `dotnet test` (full) → all pass (confirms the `regenerate` signature change is consistently updated and the web project builds).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Indexer.fs src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json tests/Nameless.TaskList.Core.Tests/IndexerTests.fs
git commit -m "feat: dormancy archival sweep folded into /reindex with config thresholds"
```

---

### Task 5: Exclude archived topics from matching

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (embedding candidate load)
- Modify: `src/Nameless.TaskList.Core/Tools.fs` (`get_topics` excludes archived)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Produces: the topic-match candidate set (embedding shortlist and the `get_topics` tool) excludes `status: archived` topics; `active` and `resolved` remain candidates.

- [ ] **Step 1: Write the failing test**

In `PipelineTests.fs`, add:

```fsharp
[<Fact>]
let ``an archived topic is not a match candidate so a new topic is created`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/birthday-party.md",
               "---\ntype: Topic\ntitle: Birthday party\ndescription: d\nstatus: archived\ncontext:\n  - family\n" +
               "first_seen: 2026-01-01T09:00:00+02:00\nlast_updated: 2026-01-01T09:00:00+02:00\n---\n## Current understanding\nplanning the party\n")
    // Everything is "similar" by embedding; only the archived status should keep it out.
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let topicBody = Responses.final "STATUS: active\n## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"
    // No topic-match LLM response scripted: an empty candidate set must take the fast new-topic path.
    let chat = FakeChatClient([ signalClassify; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) ->
        Assert.StartsWith("topics/active/", topic)
        Assert.NotEqual<string>("topics/active/birthday-party.md", topic)
    | other -> failwithf "expected Processed new topic, got %A" other
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~archived topic is not a match"`
Expected: FAIL — the archived topic is loaded as a candidate, the scripted queue underflows (a topic-match call is attempted), so it throws / matches the wrong topic.

- [ ] **Step 3: Filter archived from the embedding candidate load**

In `Pipeline.fs`, the `let activeTopics =` block maps `deps.Vault.ListFiles "topics/active"` into `(slug, title, understanding)`. Inside its `Some fm ->` arm, guard on status:

```fsharp
                        | Some fm ->
                            let t = Frontmatter.deserialize<Topic> fm
                            if (if isNull t.Status then "active" else t.Status).Trim().ToLowerInvariant() = "archived" then None
                            else Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, understandingOf mf.Content)
```

- [ ] **Step 4: Filter archived from the `get_topics` tool**

In `Tools.fs`, replace the `getTopics` handler `(fun _ -> dumpDir vault "topics/active")` with a status-filtered listing:

```fsharp
            (fun _ ->
                vault.ListFiles "topics/active"
                |> List.choose (fun path ->
                    try
                        let mf = MarkdownFile.FromString (vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let t = Frontmatter.deserialize<Topic> fm
                            if (if isNull t.Status then "active" else t.Status).Trim().ToLowerInvariant() = "archived" then None
                            else Some (sprintf "slug: %s\ntitle: %s" (System.IO.Path.GetFileNameWithoutExtension path) t.Title)
                        | None -> None
                    with _ -> None)
                |> String.concat "\n\n")
```

Ensure `Tools.fs` opens `Nameless.TaskList.Core.KnowledgeBase` (for `MarkdownFile`/`Frontmatter`/`Topic`); add the `open` if missing.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~archived topic is not a match"` → PASS.
Then `dotnet test tests/Nameless.TaskList.Core.Tests` → all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList.Core/Tools.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: exclude archived topics from topic matching"
```

---

### Task 6: Broadcast `Logged` result (log-only feeds)

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`PipelineResult.Logged`; replace broadcast entity-clearing with a `Logged` short-circuit)
- Modify: `src/Nameless.TaskList/ProcessMessage.fs` (`toHttp` for `Logged`)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (update the existing broadcast test), `tests/Nameless.TaskList.Tests/EndpointTests.fs`

**Interfaces:**
- Produces: `PipelineResult.Logged`; a broadcast non-noise message writes a message record (`noise: false`, `topic: ""`, raw content preserved) and creates no topic/task/event/commitment/note; HTTP `200 {logged:true}`.

- [ ] **Step 1: Update the existing broadcast test and add the no-topic assertion**

In `PipelineTests.fs`, find `broadcast channel suppresses task event and commitment extraction`. Replace its match block so it expects `Logged` and asserts no topic file and a content-bearing message record:

```fsharp
    match Pipeline.processMessage d "M1" "120363241214508891@newsletter" with
    | Logged ->
        let has prefix = vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith(prefix: string))
        Assert.False(has "tasks/")
        Assert.False(has "events/")
        Assert.False(has "commitments/")
        Assert.False(has "topics/")
        let msg = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith "messages/")
        Assert.Contains("noise: false", vault.Files.[msg])
    | other -> failwithf "expected Logged, got %A" other
```

Also: this test currently scripts `FakeChatClient([ classify; topicMatch; topicBody ])`. With the short-circuit only `classify` is consumed; change it to `FakeChatClient([ classify ])`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~broadcast channel suppresses"`
Expected: FAIL — `Logged` is not a defined case.

- [ ] **Step 3: Add the `Logged` result case**

In `Pipeline.fs`, extend the DU:

```fsharp
    type PipelineResult =
        | NotFound
        | Skipped
        | ProcessedNoise
        | Logged
        | Processed of topic: string * tasks: string list
        | LlmError of string
```

- [ ] **Step 4: Replace the broadcast entity-clearing with a `Logged` short-circuit**

In `Pipeline.fs`, the classification rebinding currently does broadcast entity-clearing AND `stripEndearments`:

```fsharp
            let classification =
                let c =
                    if isBroadcastChannel chatJid then
                        { classification with
                            Entities = { classification.Entities with Tasks = [||]; Events = [||]; Commitments = [||] } }
                    else classification
                { c with PeopleMentioned = stripEndearments c.PeopleMentioned }
```

Remove the broadcast branch (keep only `stripEndearments`):

```fsharp
            let classification = { classification with PeopleMentioned = stripEndearments classification.PeopleMentioned }
```

Then, immediately after the existing `if classification.Noise then ... ProcessedNoise else` block (i.e. once we know the message is not noise), add the broadcast short-circuit before the people/topic steps:

```fsharp
            if isBroadcastChannel chatJid then
                // Broadcast feeds are one-to-many: log the message, but never thread it into a
                // topic or extract entities from it.
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = false; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
                deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize record) (mediaHeader + msg.Content))
                updateChannel deps msg channelSlug None
                Logged
            else
```

(Place this right after the noise `else`, so it sits at the same indentation as the `slugifyPeople`/topic-match code that follows.)

- [ ] **Step 5: Map `Logged` to HTTP**

In `src/Nameless.TaskList/ProcessMessage.fs`, in `ProcessMessageHandler.toHttp`, add a case alongside `Skipped`:

```fsharp
        | Logged -> Results.Ok(box {| logged = true |})
```

- [ ] **Step 6: Add the endpoint test**

In `tests/Nameless.TaskList.Tests/EndpointTests.fs`, mirror the existing `Skipped maps to 200` test, which uses the file's `statusOf : PipelineResult -> int` helper:

```fsharp
[<Fact>]
let ``Logged maps to 200`` () =
    Assert.Equal(200, statusOf Logged)
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test` (full Core + endpoint).
Expected: PASS — including the updated broadcast test and the new endpoint test.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/ProcessMessage.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs tests/Nameless.TaskList.Tests/EndpointTests.fs
git commit -m "feat: broadcast feeds become message-only logs (Logged result)"
```

---

## Final verification

- [ ] Run the whole suite: `dotnet test` → all green (Core + endpoint).
- [ ] Manual smoke (host on granite): process a `@newsletter` message → `200 {logged:true}`, no topic file; process a normal message twice into one topic, scripting/observing a `STATUS: resolved` → topic frontmatter shows `status: resolved`; `POST /reindex` after back-dating a topic's `last_updated` → that topic flips to `status: archived` and drops from its channel's `active_topics`.
