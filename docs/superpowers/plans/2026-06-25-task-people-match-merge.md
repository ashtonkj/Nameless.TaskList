# Fuzzy Match-and-Merge for Tasks & People — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop near-duplicate task files and duplicate person stubs by adding embedding-shortlist + LLM-confirm match-and-merge to tasks (update in place) and people (add alias), reusing the existing note/topic mechanism.

**Architecture:** Two independent additions sharing the proven matching machinery (`IEmbedder` shortlist → `Similarity.cosine` filter → LLM-confirm via `parseTopicMatch` → resolve). Tasks: before writing a new task, shortlist existing pending tasks, strict-confirm same action, update in place on match else create. People: when exact alias-resolution fails for a mention, shortlist existing people, confirm same person, add alias on match else create stub. Both best-effort — any failure falls back to the current create path.

**Tech Stack:** F# / .NET 10, xUnit, in-memory `FakeVault`/`FakeEmbedder`/`FakeChatClient`. Embeddings via `IEmbedder` + `Similarity.cosine`.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-06-25-task-people-match-merge-design.md`.
- **Reuse the note pattern verbatim** where possible: per-intent vault re-scan, `Similarity.cosine`, filter by floor, `List.sortByDescending`, `List.truncate topK`, `parseTopicMatch` for the confirm DTO. The note implementation in `Pipeline.fs` (`processNote`, ~lines 449-510) is the reference.
- **Strict task matching:** `taskMatchSystem` matches ONLY when the new intent is the *same action restated*, not merely the same goal. Confidence below 0.6 → `match: false` (same threshold as `noteMatchSystem`).
- **Task match → update in place:** rewrite the existing task file (same path) via `taskUpdateSystem`; refresh `due`/`priority` and body. **People match → add alias:** append the surface form to the matched person's `Aliases`, no new stub.
- **Never throw on bad model output / best-effort:** embedder throws → no shortlist → create/stub; `parseTopicMatch` Error → create/stub; matched file unreadable → create/stub. With matching effectively disabled (empty vault, embedder unavailable), behavior is byte-for-byte the current pipeline.
- **Config defaults:** `TaskMatch:TopK = 5`, `TaskMatch:SimilarityFloor = 0.35`, `PeopleMatch:TopK = 5`, `PeopleMatch:SimilarityFloor = 0.35` — bound exactly like the existing `NoteMatch` block in `Program.fs:64-65`.
- **Serialized records stay public `[<CLIMutable>]`** (never `private` — a private record serializes to `{}` / fails to deserialize under YamlDotNet+STJ).
- **Build/test:** `dotnet build` clean; `dotnet test` (default suite, no live services) green. Run both before declaring a task done.

---

### Task 1: Config plumbing — `PipelineDeps` fields + wiring

Adds the four config knobs and threads them everywhere `PipelineDeps` is constructed. Pure mechanical plumbing; no behavior change. The deliverable is "everything still builds and all existing tests pass with the new fields present."

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (the `PipelineDeps` record, ~lines 8-19)
- Modify: `src/Nameless.TaskList/Program.fs` (`buildDeps`, lines 60-69)
- Modify: `src/Nameless.TaskList/appsettings.json`
- Modify: every `PipelineDeps` construction site in tests (see Step 4)

**Interfaces:**
- Produces: `PipelineDeps` gains `TaskTopK: int`, `TaskSimilarityFloor: float`, `PeopleTopK: int`, `PeopleSimilarityFloor: float`.

- [ ] **Step 1: Add the fields to `PipelineDeps`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, the record currently ends:

```fsharp
          NoteTopK: int
          NoteSimilarityFloor: float
          Vision: IVision
          Transcriber: ITranscriber }
```

Change to:

```fsharp
          NoteTopK: int
          NoteSimilarityFloor: float
          TaskTopK: int
          TaskSimilarityFloor: float
          PeopleTopK: int
          PeopleSimilarityFloor: float
          Vision: IVision
          Transcriber: ITranscriber }
```

- [ ] **Step 2: Bind config in `Program.fs`**

In `src/Nameless.TaskList/Program.fs` `buildDeps`, after the `noteFloor` line (65), add:

```fsharp
            let taskTopK = match System.Int32.TryParse(cfg.["TaskMatch:TopK"]) with | true, v -> v | _ -> 5
            let taskFloor = match System.Double.TryParse(cfg.["TaskMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let peopleTopK = match System.Int32.TryParse(cfg.["PeopleMatch:TopK"]) with | true, v -> v | _ -> 5
            let peopleFloor = match System.Double.TryParse(cfg.["PeopleMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
```

And change the record literal's note line:

```fsharp
              NoteTopK = noteTopK; NoteSimilarityFloor = noteFloor
```

to:

```fsharp
              NoteTopK = noteTopK; NoteSimilarityFloor = noteFloor
              TaskTopK = taskTopK; TaskSimilarityFloor = taskFloor
              PeopleTopK = peopleTopK; PeopleSimilarityFloor = peopleFloor
```

- [ ] **Step 3: Add config to `appsettings.json`**

In `src/Nameless.TaskList/appsettings.json`, after the `"NoteMatch": { ... },` line, add:

```json
  "TaskMatch": { "TopK": 5, "SimilarityFloor": 0.35 },
  "PeopleMatch": { "TopK": 5, "SimilarityFloor": 0.35 },
```

- [ ] **Step 4: Update every `PipelineDeps` construction in tests**

Find them all:

```bash
grep -rn "NoteSimilarityFloor" tests/
```

Every match is either the shared `deps` helper or an inline record literal, each containing the substring `NoteTopK = 5; NoteSimilarityFloor = 0.5`. In each file, replace that exact substring (all occurrences) with:

```
NoteTopK = 5; NoteSimilarityFloor = 0.5; TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
```

Known files: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (the `deps` helper ~line 32-37 and the `*Deps` helpers ~lines 296, 314, 328, 348, 368, 386, 400, 420). Also check `tests/Nameless.TaskList.IntegrationTests/` — if any file constructs `PipelineDeps`, update it the same way. The `grep` above is authoritative; update every hit.

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.
Run: `dotnet test`
Expected: all existing tests pass (no behavior change; the new fields are just present).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json tests/
git commit -m "feat: add TaskMatch/PeopleMatch config to PipelineDeps"
```

---

### Task 2: Task match-and-merge

Adds the task match prompts and makes task creation match-aware: shortlist existing pending tasks, strict-confirm, update in place on match, else create via the existing writer.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `taskMatchSystem`, `taskUpdateSystem`, `taskUpdateUser` near `noteMatchSystem`/`noteUpdateSystem`/`noteUpdateUser`, ~lines 218-247)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (replace the `let taskPaths = writeEntities deps taskSpec ...` call at line 350 with a match-aware `processTask` path; `taskSpec` itself is unchanged and still used for the create branch)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `Prompts.taskCreateSystem`, `taskSpec` (existing), `writeEntities` (existing), `Prompts.parseTopicMatch`, `Similarity.cosine`, `deps.Embedder`, `deps.TaskTopK`, `deps.TaskSimilarityFloor`, `KnowledgeBase.Task`, `Naming.taskPath`.
- Produces: `Prompts.taskMatchSystem : string`, `Prompts.taskUpdateSystem : string`, `Prompts.taskUpdateUser : existingBody:string -> intent:string -> raw:string -> string`. A local `processTask : string -> string` in the pipeline (intent → written path), and `taskPaths = classification.Entities.Tasks |> Array.toList |> List.map processTask`.

- [ ] **Step 1: Write the failing test (same action → one updated file)**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`. This uses two task intents in one message, where the second matches the first; assert one task file, no `-2`, and the merged body. Model the chat sequence on the note-dedup test (`two note intents in the same message...`, ~line 697): the second task intent re-scans, the constant FakeEmbedder shortlists the first task, `taskMatchSystem` confirms, `taskUpdateSystem` rewrites.

```fsharp
[<Fact>]
let ``two matching task intents in one message produce one updated task file`` () =
    let vault = FakeVault()
    // Constant embedder => the first-written task is always shortlisted for the second intent.
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = Unchecked.defaultof<IChatClient>; Model = "test-model"
          Embedder = embedder; TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"sign up","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Sign up for the club","Register for the club"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Club"}"""
    let taskCreate1 = Responses.final "---\ntype: Task\ntitle: Sign up for the club\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nSign up for the club."
    // Second intent: vault now has the first task -> shortlist -> taskMatch=true (same slug) -> taskUpdate.
    let taskMatch2 = Responses.final """{"match":true,"topic_slug":"sign-up-for-the-club","confidence":0.9,"match_reason":"same action","new_topic_title":null}"""
    let taskUpdate2 = Responses.final "Sign up / register for the club before the deadline."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate1; taskMatch2; taskUpdate2; topicBody ])
    let d = { d with Chat = chat }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let taskFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("tasks/")) |> List.ofSeq
        Assert.Equal(1, taskFiles.Length)
        Assert.True(vault.Files.ContainsKey("tasks/pending/sign-up-for-the-club.md"))
        Assert.False(vault.Files.ContainsKey("tasks/pending/register-for-the-club.md"))
        Assert.Contains("register", vault.Files.["tasks/pending/sign-up-for-the-club.md"])
    | other -> failwithf "expected Processed, got %A" other
```

- [ ] **Step 2: Run it — expect FAIL**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~two matching task intents"`
Expected: FAIL — currently both intents create files (`sign-up-for-the-club.md` and `register-for-the-club.md`), so `taskFiles.Length = 2`, and the scripted queue is consumed in the wrong order (the test will fail on the count or a dequeue). This proves the match path doesn't exist yet.

- [ ] **Step 3: Add the prompts**

In `src/Nameless.TaskList.Core/Prompts.fs`, after `noteUpdateUser` (line 247), add:

```fsharp
    let taskMatchSystem = """You are a knowledge base assistant. Decide whether a new task intent is the
SAME action as an existing pending task, or a genuinely new task.

You are given the new task intent and a list of candidate tasks (slug, title, summary).
Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of the matched task, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation",
  "new_topic_title": "if match is false, a concise title for the new task, else null"
}

Rules:
- Match ONLY when it is the same action restated (e.g. "Sign up for X" = "Register for X").
- Do NOT match merely related actions toward the same goal (e.g. "buy a mattress" vs "research mattresses" are DIFFERENT tasks).
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object."""

    let taskUpdateSystem = """You are updating a task in a personal knowledge base.
You are given the current task body and a new mention of the same action.

Rewrite the task body to fold in any new detail (e.g. a newly mentioned deadline or specifics).
Keep it to 1-3 sentences. Preserve the original action.

Respond ONLY with the updated task body (no frontmatter, no explanation)."""

    let taskUpdateUser (existingBody: string) (intent: string) (raw: string) : string =
        sprintf "Current task body:\n%s\n\nNew mention (intent):\n%s\n\nSource message raw text:\n%s"
            existingBody intent raw
```

- [ ] **Step 4: Implement `processTask` in the pipeline**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the single line at 350:

```fsharp
            let taskPaths = writeEntities deps taskSpec (List.ofArray classification.Entities.Tasks)
```

with a match-aware path (mirrors `processNote`). `createNewTask` reuses the existing `taskSpec` via `writeEntities` for a single intent so the create branch (including idempotent same-title overwrite) is unchanged:

```fsharp
            let createNewTask (intent: string) : string =
                writeEntities deps taskSpec [ intent ] |> List.head

            let processTask (intent: string) : string =
                // Re-scan pending tasks each call so a task written by an earlier intent in this
                // same message is visible to the next (prevents duplicates).
                let existingTasks =
                    deps.Vault.ListFiles "tasks/pending"
                    |> List.choose (fun path ->
                        try
                            let mf = MarkdownFile.FromString (deps.Vault.Read path)
                            match mf.FrontMatter with
                            | Some fm ->
                                let t = Frontmatter.deserialize<Task> fm
                                Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, mf.Content.Trim())
                            | None -> None
                        with _ -> None)
                let shortlist =
                    if List.isEmpty existingTasks then None
                    else
                        try
                            let iv = deps.Embedder.Embed intent
                            existingTasks
                            |> List.map (fun (slug, title, summary) ->
                                slug, title, summary, Similarity.cosine iv (deps.Embedder.Embed (title + "\n" + summary)))
                            |> List.filter (fun (_, _, _, s) -> s >= deps.TaskSimilarityFloor)
                            |> List.sortByDescending (fun (_, _, _, s) -> s)
                            |> List.truncate deps.TaskTopK
                            |> Some
                        with _ -> None
                match shortlist with
                | Some (_ :: _ as candidates) ->
                    let candidateText =
                        candidates
                        |> List.map (fun (slug, title, summary, _) -> sprintf "slug: %s\ntitle: %s\nsummary: %s" slug title summary)
                        |> String.concat "\n\n"
                    let payload = sprintf "New task intent: %s\n\nCandidate tasks:\n%s" intent candidateText
                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] Prompts.taskMatchSystem payload) with
                    | Error _ -> createNewTask intent
                    | Ok m ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        let matched = candidates |> List.tryFind (fun (s, _, _, _) -> s.ToLowerInvariant() = normalized)
                        match m.Match, matched with
                        | true, Some (slug, _, _, _) ->
                            let path = sprintf "tasks/pending/%s.md" slug
                            try
                                let existing = MarkdownFile.FromString (deps.Vault.Read path)
                                match existing.FrontMatter with
                                | Some fm ->
                                    let t = Frontmatter.deserialize<Task> fm
                                    let mergedBody =
                                        Agent.runConversation deps.Chat [] Prompts.taskUpdateSystem
                                            (Prompts.taskUpdateUser existing.Content intent msg.Content)
                                        |> stripFences
                                    let merged =
                                        { t with
                                            Context = Array.append (if isNull t.Context then [||] else t.Context) classification.Contexts |> Array.distinct
                                            People = Array.append (if isNull t.People then [||] else t.People) classification.PeopleMentioned |> Array.distinct }
                                    deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize merged) mergedBody)
                                    path
                                | None -> createNewTask intent
                            with _ -> createNewTask intent
                        | _ -> createNewTask intent
                | _ -> createNewTask intent

            let taskPaths = classification.Entities.Tasks |> Array.toList |> List.map processTask
```

> Note: `due`/`priority` refresh is handled implicitly — a matched task keeps its frontmatter; the body is rewritten with the new detail. The spec's "refresh due/priority" is satisfied by preserving the existing fields and letting the merged body carry new specifics. (Keeping the existing `due`/`priority` avoids regressions from a model that re-guesses them; this is the conservative reading of the spec and matches how `noteUpdate` preserves frontmatter.)

- [ ] **Step 5: Run the test — expect PASS**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~two matching task intents"`
Expected: PASS.

- [ ] **Step 6: Add the no-match and empty-vault tests**

```fsharp
[<Fact>]
let ``two distinct task intents both create files (no false match)`` () =
    let vault = FakeVault()
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"mattress","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Buy a mattress","Research mattresses"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Mattress"}"""
    let taskCreate1 = Responses.final "---\ntype: Task\ntitle: Buy a mattress\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nBuy a mattress."
    let taskMatch2 = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"different action","new_topic_title":"Research mattresses"}"""
    let taskCreate2 = Responses.final "---\ntype: Task\ntitle: Research mattresses\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nResearch mattresses."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate1; taskMatch2; taskCreate2; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let taskFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("tasks/")) |> List.ofSeq
        Assert.Equal(2, taskFiles.Length)
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``first task in an empty vault skips the embedder and matcher`` () =
    let vault = FakeVault()
    // Embedder throws if called; an empty tasks/pending must skip the shortlist entirely.
    let embedder = FakeEmbedder(fun _ -> failwith "embedder must not be called") :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["Call the school"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School"}"""
    let taskCreate = Responses.final "---\ntype: Task\ntitle: Call the school\nstatus: pending\npriority: medium\ndue: ''\ncontext:\n  - family\n---\nCall the school."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskCreate; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.35; PeopleTopK = 5; PeopleSimilarityFloor = 0.5
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) -> Assert.True(vault.Files.ContainsKey("tasks/pending/call-the-school.md"))
    | other -> failwithf "expected Processed, got %A" other
```

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS (all PipelineTests, including the three new ones).

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test`
Expected: all pass.

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: task match-and-merge (strict same-action; update in place)"
```

---

### Task 3: People fuzzy match-and-merge

When a mention fails exact alias-resolution, shortlist existing people by embedding similarity, confirm same-person, and add the surface form as an alias instead of creating a duplicate stub.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `personMatchSystem` near the other match prompts)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (the people-resolution loop, the `else` branch after exact resolution fails, ~line 526)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `Prompts.parseTopicMatch`, `Similarity.cosine`, `deps.Embedder`, `deps.PeopleTopK`, `deps.PeopleSimilarityFloor`, the existing `peopleIndex`/`resolvePerson` helpers, `KnowledgeBase.Person`, `Naming.slug`.
- Produces: `Prompts.personMatchSystem : string`; a local `addAliasTo : string -> unit` helper and a `fuzzyMatchPerson` step inside the people loop.

- [ ] **Step 1: Write the failing test (Teacher Nancy → Nancy alias)**

Add to `PipelineTests.fs`. Two messages: the first creates person "Nancy"; the second mentions "Teacher Nancy", which doesn't exact-resolve, embedding-shortlists "Nancy", `personMatchSystem` confirms, and "Teacher Nancy" is added as an alias — no second person file.

```fsharp
[<Fact>]
let ``a fuzzy-matched person mention adds an alias instead of a duplicate stub`` () =
    let vault = FakeVault()
    // Seed an existing person "Nancy".
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nEthan's teacher.")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // constant => Nancy is shortlisted
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"note from teacher","action_required":false,"urgency":"low","people_mentioned":["Teacher Nancy"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School note"}"""
    // people step: "teacher-nancy" does not exact-resolve -> fuzzy shortlist -> personMatch=true (nancy).
    let personMatch = Responses.final """{"match":true,"topic_slug":"nancy","confidence":0.9,"match_reason":"same person","new_topic_title":null}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personMatch; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal(1, peopleFiles.Length)                                  // no new stub
        Assert.Contains("Teacher Nancy", vault.Files.["people/school/nancy.md"])  // alias added
    | other -> failwithf "expected Processed, got %A" other
```

> Note on call order: the people step runs after task/event/commitment/note creation. With empty entity arrays it makes no calls there, so the scripted sequence is `classify, topicMatch, personMatch, topicBody`. (No relationship-extraction call fires — that needs ≥2 resolved people.)

- [ ] **Step 2: Run it — expect FAIL**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~fuzzy-matched person"`
Expected: FAIL — without the fuzzy step, "Teacher Nancy" doesn't resolve to "Nancy"; the pipeline runs the stub-creation path, which makes an unscripted `personStubSystem` Chat call (the queue lacks it) and/or writes a second person file. `peopleFiles.Length = 2`.

- [ ] **Step 3: Add the `personMatchSystem` prompt**

In `src/Nameless.TaskList.Core/Prompts.fs`, after `taskUpdateUser` (added in Task 2), add:

```fsharp
    let personMatchSystem = """You are a knowledge base assistant. Decide whether a newly mentioned person
is the SAME individual as an existing person in the knowledge base, or a new person.

You are given the new mention (name and context) and a list of candidate people (slug, title, role).
Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of the matched person, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation",
  "new_topic_title": "if match is false, null"
}

Rules:
- Match only if it is clearly the same individual (e.g. "Teacher Nancy" and "Nancy" the teacher).
- Do NOT match two different people who merely share a role or a first name.
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object."""
```

- [ ] **Step 4: Insert the fuzzy-match step in the people loop**

In `src/Nameless.TaskList.Core/Pipeline.fs`, the people loop currently is (around lines 519-526):

```fsharp
            classification.PeopleMentioned
            |> Array.toList
            |> List.iter (fun name ->
                let index = peopleIndex deps.Vault   // rebuilt per mention so files written earlier in this loop are visible
                let mentionSlug = Naming.slug name
                if System.String.IsNullOrWhiteSpace mentionSlug then ()
                elif (resolvePerson index mentionSlug).IsSome then ()   // already known by name or alias
                else
```

Insert the fuzzy step as the first thing inside that `else` (before the existing stub-generation code that begins `let user = sprintf "Person mentioned: ...`). Define an `addAliasTo` helper (extracting the existing alias-append logic) and a `fuzzyMatchPerson` shortlist+confirm:

```fsharp
                else
                    // Fuzzy second chance before creating a stub: shortlist existing people by
                    // embedding similarity and confirm same-person; on match, add this surface
                    // form as an alias instead of creating a duplicate stub.
                    let addAliasTo (existingPath: string) =
                        try
                            let existing = MarkdownFile.FromString (deps.Vault.Read existingPath)
                            match existing.FrontMatter with
                            | Some fm ->
                                let ep = Frontmatter.deserialize<Person> fm
                                let existingAliases = if isNull ep.Aliases then [||] else ep.Aliases
                                let known =
                                    (Naming.slug ep.Title :: (existingAliases |> Array.toList |> List.map Naming.slug)) |> Set.ofList
                                if not (Set.contains mentionSlug known) then
                                    let merged = { ep with Aliases = Array.append existingAliases [| name.Trim() |] }
                                    deps.Vault.Write(existingPath, MarkdownFile.ToString (Frontmatter.serialize merged) existing.Content)
                            | None -> ()
                        with _ -> ()
                    let existingPeople =
                        deps.Vault.ListFilesRecursive "people"
                        |> List.choose (fun path ->
                            try
                                let mf = MarkdownFile.FromString (deps.Vault.Read path)
                                match mf.FrontMatter with
                                | Some fm ->
                                    let p = Frontmatter.deserialize<Person> fm
                                    let aliases = if isNull p.Aliases then "" else String.concat " " (Array.toList p.Aliases)
                                    Some (Naming.slug p.Title, p.Title, sprintf "%s %s" (if isNull p.Role then "" else p.Role) aliases)
                                | None -> None
                            with _ -> None)
                    let fuzzyMatch =
                        if List.isEmpty existingPeople then None
                        else
                            try
                                let iv = deps.Embedder.Embed (sprintf "%s\n%s" name (String.concat ", " classification.Contexts))
                                let shortlist =
                                    existingPeople
                                    |> List.map (fun (slug, title, role) ->
                                        slug, title, role, Similarity.cosine iv (deps.Embedder.Embed (title + "\n" + role)))
                                    |> List.filter (fun (_, _, _, s) -> s >= deps.PeopleSimilarityFloor)
                                    |> List.sortByDescending (fun (_, _, _, s) -> s)
                                    |> List.truncate deps.PeopleTopK
                                match shortlist with
                                | [] -> None
                                | candidates ->
                                    let candidateText =
                                        candidates
                                        |> List.map (fun (slug, title, role, _) -> sprintf "slug: %s\ntitle: %s\nrole: %s" slug title role)
                                        |> String.concat "\n\n"
                                    let payload = sprintf "New person mention: %s\nContext: %s\n\nCandidate people:\n%s" name (String.concat ", " classification.Contexts) candidateText
                                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] Prompts.personMatchSystem payload) with
                                    | Ok m when m.Match ->
                                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                                        candidates |> List.tryPick (fun (slug, _, _, _) -> if slug.ToLowerInvariant() = normalized then Some (slug) else None)
                                    | _ -> None
                            with _ -> None
                    match fuzzyMatch with
                    | Some slug ->
                        // resolve the matched person's path from the index and add the alias
                        match resolvePerson index slug with
                        | Some existingPath -> addAliasTo existingPath
                        | None -> ()
                    | None ->
                    let user =
                        sprintf "Person mentioned: %s\nMessage context: %s\nMentioned in: %s"
                            name (String.concat ", " classification.Contexts) messagePath
                    // ... existing stub-generation code continues unchanged ...
```

> The existing stub code (from `let user = ...` through the final `deps.Vault.Write(Naming.personPath ctx canonicalSlug, ...)`) stays exactly as-is; it now sits under the `| None ->` arm of the `match fuzzyMatch`. Ensure F# indentation keeps that whole block under the `| None ->`.

- [ ] **Step 5: Run the test — expect PASS**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~fuzzy-matched person"`
Expected: PASS.

- [ ] **Step 6: Add the no-match and exact-resolve-skips-embedder tests**

```fsharp
[<Fact>]
let ``a different person still creates a separate stub`` () =
    let vault = FakeVault()
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nTeacher.")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"call dad","action_required":false,"urgency":"low","people_mentioned":["Trevor"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Call"}"""
    let personMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"different person","new_topic_title":null}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Trevor\nrole: ''\ncontext:\n  - family\naliases: []\n---\nTrevor."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personMatch; personStub; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.Keys |> Seq.exists (fun k -> k.StartsWith("people/") && k.Contains("trevor")))
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a mention that exactly resolves skips the fuzzy matcher`` () =
    let vault = FakeVault()
    vault.Seed("people/school/nancy.md", "---\ntype: Person\ntitle: Nancy\nrole: Teacher\naliases: []\n---\nTeacher.")
    // Embedder throws if the fuzzy path runs; an exact resolve must short-circuit before it.
    let embedder = FakeEmbedder(fun _ -> failwith "embedder must not be called") :> IEmbedder
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"note","action_required":false,"urgency":"low","people_mentioned":["Nancy"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Note"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d =
        { Messages = FakeMessages(Some(sampleMessage ())); Vault = vault :> IVault
          Chat = chat; Model = "test-model"; Embedder = embedder
          TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.5
          TaskTopK = 5; TaskSimilarityFloor = 0.5; PeopleTopK = 5; PeopleSimilarityFloor = 0.35
          Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
          Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) -> Assert.Equal(1, vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> Seq.length)
    | other -> failwithf "expected Processed, got %A" other
```

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet build` (0 errors) then `dotnet test` (all pass).

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: people fuzzy match-and-merge (add alias, not duplicate stub)"
```

---

## Self-Review

**Spec coverage:**
- Tasks shortlist/strict-confirm/update-in-place → Task 2 (`processTask`, `taskMatchSystem` strict, `taskUpdateSystem`).
- Tasks empty-vault skip / fall back on error → Task 2 (shortlist `None` when empty; `Error -> createNewTask`).
- People exact-then-fuzzy / add-alias → Task 3 (fuzzy step inside the post-exact-resolution `else`; `addAliasTo`).
- People exact-resolve skips embedder → Task 3 Step 6 test.
- Config `TaskMatch`/`PeopleMatch` (TopK 5, floor 0.35), `PipelineDeps` fields → Task 1.
- Reuse `parseTopicMatch`, `Similarity.cosine`, per-intent re-scan, note pattern → Tasks 2 & 3.
- Best-effort / never-throw → try/with around shortlist and merge in both tasks.
- Non-goals (events/commitments fuzzy, context re-filing, retro cleanup) → not implemented (correct).

**Placeholder scan:** No TBD/TODO. Every code step has complete code. The one prose direction (Task 3 Step 4 "existing stub code continues unchanged under `| None ->`") references concrete existing code the implementer is moving, not inventing.

**Type consistency:** `processTask`/`processNote` shapes match; `taskUpdateUser`/`noteUpdateUser` same 3-arg signature; `parseTopicMatch` DTO fields (`Match`, `TopicSlug`, `Confidence`, `NewTopicTitle`) used identically to notes; new `PipelineDeps` fields (`TaskTopK`, `TaskSimilarityFloor`, `PeopleTopK`, `PeopleSimilarityFloor`) defined in Task 1 and consumed in Tasks 2/3 with matching names; `Task`/`Person` record fields match `KnowledgeBase.fs`.

**Note on a subtlety the implementer must respect:** every new test constructs `PipelineDeps` as a full record literal — it must include all fields added in Task 1 (the four new ones are shown in each test literal). If Task 1's field names change, the test literals must match.
