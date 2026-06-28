# Prompt Evaluation System — Phase 3 Implementation Plan (match / merge / person-stub / relationships)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the eval's coverage of every prompted step — the task/note/person match decisions, the task/note merge-generation, person-stub generation, and relationship extraction — by extracting their LLM-call cores into shared `Steps.*` functions and adding deterministic scorers, runner wiring, and gold dataset.

**Architecture:** Same faithfulness pattern as Phases 1–2. Extract only the LLM-call cores from `Pipeline.processMessage` into `Steps.fs` (the shared `shortlistAndConfirm`, per-type match wrappers, the update/merge generators, the person-stub creator, and the relationship extractor); ALL pipeline plumbing (vault writes, safe-merge, alias-add, resolve, `buildEdge`/`reconcile`, context-filing) stays in Pipeline. The eval calls the same `Steps.*` and scores only the model's decision/output.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2, System.Text.Json, the existing `Nameless.TaskList.Eval` console project and the `Steps`/`Scoring`/`Runner` modules.

## Global Constraints

- Target framework `net10.0` (every project).
- Each Steps extraction MUST be **behaviour-preserving** for `Pipeline.processMessage` — full `dotnet test` stays green, `PipelineTests` unchanged (new StepsTests/eval tests are additive).
- Deterministic scoring only — no LLM-as-judge.
- `Steps.*` return the model's decision / output; the eval scores ONLY model-produced values — never pipeline-forced linkage, safe-merge results, alias-adds, or `buildEdge`/`reconcile` output.
- The eval (`eval/Nameless.TaskList.Eval`) stays a console project, NOT a default `dotnet test` project; `dotnet build` still compiles it.
- Make every edit to a covered file through the Verevoir MCP `write_file`/`edit_file` (built-in Edit fallback ONLY for a `.fsproj` if MCP rejects the XML escaping — note it). Run builds/tests via Bash.
- No real PII in any committed dataset/world file (anonymised stand-ins only).
- Person-merge is a deterministic alias-add (no LLM) — there is no `person-update` step.

---

## Task 1: Extract the shared match core + per-type match decisions

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (move `shortlistAndConfirm`; add `matchTask`/`matchNote`/`matchPerson`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (delete private `shortlistAndConfirm`; rewire the three decision sites)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add match tests)

**Interfaces:**
- Consumes: `IChatClient`, `IEmbedder`, `IVault`; `Similarity.cosine`; `Prompts.parseTopicMatch`/`taskMatchSystem`/`noteMatchSystem`/`personMatchSystem`.
- Produces (in `module Steps`):
  - `Steps.shortlistAndConfirm : IChatClient -> IEmbedder -> (queryText:string) -> (candidates:(string*string*string) list) -> (floor:float) -> (topK:int) -> (systemPrompt:string) -> (buildPayload:string list -> string) -> string option`
  - `Steps.matchTask : IChatClient -> IEmbedder -> IVault -> (intent:string) -> (floor:float) -> (topK:int) -> string option`
  - `Steps.matchNote : IChatClient -> IEmbedder -> IVault -> (intent:string) -> (floor:float) -> (topK:int) -> string option`
  - `Steps.matchPerson : IChatClient -> IEmbedder -> IVault -> (name:string) -> (contexts:string array) -> (floor:float) -> (topK:int) -> string option`

- [ ] **Step 1: Add the shared core + three match wrappers to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs` (inside `module Steps`). The bodies are copied from the current Pipeline code with `deps.Chat`/`deps.Embedder` replaced by `chat`/`embedder` and the vault candidate-building lifted from `processTask`/`processNote`/the person block:

```fsharp
    /// Shared shortlist-and-confirm core for the match sites (tasks, notes, people).
    /// Embeds queryText, scores each candidate's embedText by cosine, keeps those >= floor,
    /// takes the top topK, then asks the model (systemPrompt over buildPayload displayLines) to
    /// confirm a match and returns the matched slug. Best-effort: empty candidates / embedder
    /// failure / parse error / no confirmed match all yield None.
    let shortlistAndConfirm
        (chat: IChatClient) (embedder: IEmbedder) (queryText: string)
        (candidates: (string * string * string) list)
        (floor: float) (topK: int) (systemPrompt: string)
        (buildPayload: string list -> string) : string option =
        if List.isEmpty candidates then None
        else
            try
                let q = embedder.Embed queryText
                let shortlisted =
                    candidates
                    |> List.map (fun (slug, embedText, line) -> slug, line, Similarity.cosine q (embedder.Embed embedText))
                    |> List.filter (fun (_, _, s) -> s >= floor)
                    |> List.sortByDescending (fun (_, _, s) -> s)
                    |> List.truncate topK
                    |> List.map (fun (slug, line, _) -> slug, line)
                match shortlisted with
                | [] -> None
                | sl ->
                    let payload = buildPayload (sl |> List.map snd)
                    match Prompts.parseTopicMatch (Agent.runConversation chat [] systemPrompt payload) with
                    | Ok m when m.Match ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        sl |> List.tryPick (fun (slug, _) -> if slug.ToLowerInvariant() = normalized then Some slug else None)
                    | _ -> None
            with _ -> None

    /// Decide whether `intent` matches an existing pending task (None = no confirmed match).
    let matchTask (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (intent: string) (floor: float) (topK: int) : string option =
        let existingTasks =
            vault.ListFiles "tasks/pending"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Task> fm
                        let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                        let summary = mf.Content.Trim()
                        Some (slug, t.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug t.Title summary)
                    | None -> None
                with _ -> None)
        let buildPayload lines = sprintf "New task intent: %s\n\nCandidate tasks:\n%s" intent (String.concat "\n\n" lines)
        shortlistAndConfirm chat embedder intent existingTasks floor topK Prompts.taskMatchSystem buildPayload

    /// Decide whether `intent` matches an existing note (None = no confirmed match).
    let matchNote (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (intent: string) (floor: float) (topK: int) : string option =
        let existingNotes =
            vault.ListFiles "notes"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let n = Frontmatter.deserialize<Note> fm
                        let slug = System.IO.Path.GetFileNameWithoutExtension(path)
                        let summary = mf.Content.Trim()
                        Some (slug, n.Title + "\n" + summary, sprintf "slug: %s\ntitle: %s\nsummary: %s" slug n.Title summary)
                    | None -> None
                with _ -> None)
        let buildPayload lines = sprintf "New note intent: %s\n\nCandidate notes:\n%s" intent (String.concat "\n\n" lines)
        shortlistAndConfirm chat embedder intent existingNotes floor topK Prompts.noteMatchSystem buildPayload

    /// Fuzzy person match (the second chance after exact alias-resolution fails in the pipeline).
    /// Returns the title-slug of the matched person, or None. Candidate slug is Naming.slug of the
    /// person's Title (matching the pipeline's resolvePerson key), NOT the filename.
    let matchPerson (chat: IChatClient) (embedder: IEmbedder) (vault: IVault) (name: string) (contexts: string array) (floor: float) (topK: int) : string option =
        let existingPeople =
            vault.ListFilesRecursive "people"
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let p = Frontmatter.deserialize<Person> fm
                        let aliases = if isNull p.Aliases then "" else String.concat " " (Array.toList p.Aliases)
                        let role = sprintf "%s %s" (if isNull p.Role then "" else p.Role) aliases
                        let slug = Naming.slug p.Title
                        Some (slug, p.Title + "\n" + role, sprintf "slug: %s\ntitle: %s\nrole: %s" slug p.Title role)
                    | None -> None
                with _ -> None)
        let buildPayload lines =
            sprintf "New person mention: %s\nContext: %s\n\nCandidate people:\n%s"
                name (String.concat ", " contexts) (String.concat "\n\n" lines)
        let queryText = sprintf "%s\n%s" name (String.concat ", " contexts)
        shortlistAndConfirm chat embedder queryText existingPeople floor topK Prompts.personMatchSystem buildPayload
```

- [ ] **Step 2: Delete Pipeline's private `shortlistAndConfirm` and rewire `processTask`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, delete the private `shortlistAndConfirm` function (currently ~lines 202-233).

In `processTask` (currently ~lines 383-400), delete the inline `existingTasks` and `buildPayload` bindings and change the match to call `Steps.matchTask`:

```fsharp
            let processTask (intent: string) : string =
                match Steps.matchTask deps.Chat deps.Embedder deps.Vault intent deps.TaskSimilarityFloor deps.TaskTopK with
                | Some slug ->
```

(The `Some slug -> … | None -> createNewTask intent` body below is unchanged.)

- [ ] **Step 3: Rewire `processNote` and the person match site**

In `processNote` (currently ~lines 494-511), delete the inline `existingNotes`/`buildPayload` and change the match to:

```fsharp
            let processNote (intent: string) : string =
                match Steps.matchNote deps.Chat deps.Embedder deps.Vault intent deps.NoteSimilarityFloor deps.NoteTopK with
                | Some slug ->
```

In the person `List.iter` block (currently ~lines 549-570), delete the inline `existingPeople`/`buildPayload`/`queryText` and change the match to:

```fsharp
                    match Steps.matchPerson deps.Chat deps.Embedder deps.Vault name classification.Contexts deps.PeopleSimilarityFloor deps.PeopleTopK with
                    | Some slug ->
```

(The `Some slug -> … addPersonAlias … | None -> <stub generation>` body is unchanged in this task.)

- [ ] **Step 4: Build and verify no stale references**

Run: `dotnet build 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `grep -nE "let private shortlistAndConfirm|shortlistAndConfirm deps" src/Nameless.TaskList.Core/Pipeline.fs`
Expected: no output (moved + all call sites rewired).

- [ ] **Step 5: Write the failing match tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
[<Fact>]
let ``Steps.matchTask confirms a shortlisted candidate`` () =
    let vault = FakeVault()
    vault.Seed("tasks/pending/sign-up-ethan-for-swimming.md",
               "---\ntype: Task\ntitle: Sign up Ethan for swimming\nstatus: pending\n---\nRegister Ethan.\n")
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])   // cosine 1.0 clears the floor
    let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"sign-up-ethan-for-swimming","confidence":0.9,"match_reason":"same","new_topic_title":null}""" ])
    match Steps.matchTask (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "Register Ethan for swimming" 0.5 5 with
    | Some slug -> Assert.Equal("sign-up-ethan-for-swimming", slug)
    | None -> failwith "expected a match"

[<Fact>]
let ``Steps.matchNote returns None when the model declines`` () =
    let vault = FakeVault()
    vault.Seed("notes/medical-aid-details.md", "---\ntype: Note\ntitle: Medical aid details\n---\nPolicy.\n")
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"different","new_topic_title":null}""" ])
    Assert.Equal(None, Steps.matchNote (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "Buy a birthday gift" 0.5 5)

[<Fact>]
let ``Steps.matchTask returns None with no candidates`` () =
    let vault = FakeVault()
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([])
    Assert.Equal(None, Steps.matchTask (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) "anything" 0.5 5)
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (prior Steps tests + 3 new).

Run: `dotnet test`
Expected: PASS — full suite green (behaviour-preserving).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract shortlistAndConfirm + matchTask/matchNote/matchPerson into Steps"
```

---

## Task 2: Extract the merge generators (`updateTask` / `updateNote`)

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `updateTask`, `updateNote`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (rewire the two update branches)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add update tests)

**Interfaces:**
- Consumes: `Steps.stripFences`, `Steps.EntityOutcome`; `Prompts.taskUpdateSystem`/`taskUpdateUser`/`noteUpdateSystem`/`noteUpdateUser`.
- Produces:
  - `Steps.updateTask : IChatClient -> (existingFile:string) -> (intent:string) -> (raw:string) -> Result<EntityOutcome<Task>, string>` (Error carries the stripped raw reply, for the pipeline's fallback body)
  - `Steps.updateNote : IChatClient -> (existingBody:string) -> (intent:string) -> (raw:string) -> string` (the model's updated body)

- [ ] **Step 1: Add `updateTask`/`updateNote` to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs`:

```fsharp
    /// Run the task-update prompt and parse the model's merged task. Ok = parsed record+body;
    /// Error carries the stripped raw reply (the pipeline falls back to the OLD record + that raw
    /// body on Error, preserving today's behaviour). Never throws on bad model output.
    let updateTask (chat: IChatClient) (existingFile: string) (intent: string) (raw: string) : Result<EntityOutcome<Task>, string> =
        let updatedRaw =
            Agent.runConversation chat [] Prompts.taskUpdateSystem (Prompts.taskUpdateUser existingFile intent raw)
            |> stripFences
        try
            let parsed = MarkdownFile.FromString updatedRaw
            match parsed.FrontMatter with
            | Some nfm -> Ok { Record = Frontmatter.deserialize<Task> nfm; Body = parsed.Content }
            | None -> Error updatedRaw
        with _ -> Error updatedRaw

    /// Run the note-update prompt; returns the model's updated body (noteUpdateSystem emits body-only).
    let updateNote (chat: IChatClient) (existingBody: string) (intent: string) (raw: string) : string =
        Agent.runConversation chat [] Prompts.noteUpdateSystem (Prompts.noteUpdateUser existingBody intent raw)
        |> stripFences
```

- [ ] **Step 2: Rewire the task-update branch in `processTask`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the task-update block (currently ~lines 409-420: the `let updatedRaw = …` and the `let newRec, newBody = try … with _ -> (t, updatedRaw)`) with:

```fsharp
                            let newRec, newBody =
                                match Steps.updateTask deps.Chat existingRaw intent msg.Content with
                                | Ok o -> o.Record, o.Body
                                | Error raw -> t, raw
```

(The `prank`/`mergedDue`/`mergedPriority`/`merged`/write below are unchanged.)

- [ ] **Step 3: Rewire the note-update branch in `processNote`**

In `processNote`, replace the `let mergedBody = Agent.runConversation … Prompts.noteUpdateSystem … |> Steps.stripFences` (currently ~lines 519-522) with:

```fsharp
                            let mergedBody = Steps.updateNote deps.Chat existing.Content intent msg.Content
```

- [ ] **Step 4: Write the failing update tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
[<Fact>]
let ``Steps.updateTask parses the merged task`` () =
    let md = "---\ntype: Task\ntitle: Pay school fees\nstatus: pending\npriority: high\ndue: 2026-07-01\ncontext: [finance]\npeople: []\n---\nPay the term 3 fees.\n"
    let chat = FakeChatClient([ Responses.final md ])
    match Steps.updateTask (chat :> IChatClient) "existing file" "fees due 1 July" "raw" with
    | Ok o -> Assert.Equal("high", o.Record.Priority); Assert.Equal("2026-07-01", o.Record.Due)
    | Error e -> failwithf "expected Ok, got Error %s" e

[<Fact>]
let ``Steps.updateTask returns Error with raw on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "no frontmatter here" ])
    match Steps.updateTask (chat :> IChatClient) "existing" "i" "r" with
    | Ok _ -> failwith "expected Error"
    | Error raw -> Assert.Contains("no frontmatter here", raw)

[<Fact>]
let ``Steps.updateNote returns the stripped body`` () =
    let chat = FakeChatClient([ Responses.final "```\n## Medical aid\nPolicy 12345, expires 2027.\n```" ])
    let body = Steps.updateNote (chat :> IChatClient) "old body" "expiry 2027" "raw"
    Assert.Contains("expires 2027", body)
    Assert.DoesNotContain("```", body)
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (prior + 3 new).

Run: `dotnet test`
Expected: PASS — full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.updateTask/updateNote merge generators"
```

---

## Task 3: Extract `Steps.createPersonStub`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (move `knownContexts`; add `createPersonStub`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (rewire the person stub-generation; reference `Steps.knownContexts`)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add person-stub tests)

**Interfaces:**
- Consumes: `Steps.GenInput`, `Steps.EntityOutcome`, `Steps.stripFences`; `Prompts.personStubSystem`.
- Produces:
  - `Steps.knownContexts : string list`
  - `Steps.createPersonStub : IChatClient -> GenInput -> EntityOutcome<Person>`

- [ ] **Step 1: Move `knownContexts` and add `createPersonStub` to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs`:

```fsharp
    /// The contexts a person can be filed under (role-derived). Public so the pipeline and the
    /// person-stub fallback share one definition.
    let knownContexts = [ "family"; "medical"; "school"; "finance"; "professional" ]

    /// Generate a person-stub record+body. Mirrors the former Pipeline personStubSystem call +
    /// interpret + fallback. The mention name travels in input.Intent; contexts in input.Contexts;
    /// the message path in input.MessagePath. The fallback Context is a single role-derived context
    /// ([| messageCtx |]), matching the pipeline.
    let createPersonStub (chat: IChatClient) (input: GenInput) : EntityOutcome<Person> =
        let user =
            sprintf "Person mentioned: %s\nMessage context: %s\nMentioned in: %s"
                input.Intent (String.concat ", " input.Contexts) input.MessagePath
        let raw = Agent.runConversation chat [] Prompts.personStubSystem user
        try
            let parsed = MarkdownFile.FromString (stripFences raw)
            match parsed.FrontMatter with
            | Some fm ->
                let p = Frontmatter.deserialize<Person> fm
                if not (System.String.IsNullOrWhiteSpace p.Title) then { Record = { p with Type = "Person" }; Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let messageCtx =
                input.Contexts
                |> Array.tryFind (fun c -> List.contains c knownContexts)
                |> Option.defaultValue "family"
            { Record =
                { Type = "Person"; Title = input.Intent; Role = ""; Context = [| messageCtx |]
                  Channel = ""; Phone = ""; Email = ""; Tags = [||]; Aliases = [||] }
              Body = sprintf "%s\n\n⚠ Stub — details to be completed." input.Intent }
```

- [ ] **Step 2: Delete Pipeline's private `knownContexts`; rewire the stub generation**

In `src/Nameless.TaskList.Core/Pipeline.fs`, delete the private `knownContexts` binding (currently ~line 130: `let private knownContexts = [ … ]`).

Update the `messageCtx` binding (currently ~line 537-540) and the `ctx` check (currently ~line 606) to reference `Steps.knownContexts`:

```fsharp
            let messageCtx =
                classification.Contexts
                |> Array.tryFind (fun c -> List.contains c Steps.knownContexts)
                |> Option.defaultValue "family"
```

and (in the `ctx` computation):

```fsharp
                            if List.contains candidate Steps.knownContexts then candidate else messageCtx
```

Replace the inline stub generation (currently ~lines 577-593: the `let user = sprintf "Person mentioned: …"`, `let raw = Agent.runConversation … personStubSystem user`, and the `let record, body = try … with _ -> (fallback)`) with a call to `Steps.createPersonStub`:

```fsharp
                    let outcome =
                        Steps.createPersonStub deps.Chat
                            { Intent = name; Raw = ""; ReferenceDate = ""; Contexts = classification.Contexts
                              Urgency = ""; TopicPath = ""; MessagePath = messagePath
                              PeopleSlugs = [||]; TaskPaths = [] }
                    let record, body = outcome.Record, outcome.Body
```

(Everything after — `canonicalSlug`, `resolvePerson`, `ctx`, `seededAliases`, `finalRecord`, the write — is unchanged.)

- [ ] **Step 3: Build + verify**

Run: `dotnet build 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `grep -nE "let private knownContexts|personStubSystem" src/Nameless.TaskList.Core/Pipeline.fs`
Expected: no output (moved; the personStubSystem call now lives in Steps).

- [ ] **Step 4: Write the failing person-stub tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
let private personInput name (contexts: string array) : Steps.GenInput =
    { Intent = name; Raw = ""; ReferenceDate = ""; Contexts = contexts; Urgency = ""
      TopicPath = ""; MessagePath = "messages/m.md"; PeopleSlugs = [||]; TaskPaths = [] }

[<Fact>]
let ``Steps.createPersonStub parses a model person`` () =
    let md = "---\ntype: Person\ntitle: Dr Brown\nrole: dentist\ncontext: [medical]\naliases: [Brown]\n---\nEthan's dentist. ⚠ Stub — details to be completed.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createPersonStub (chat :> IChatClient) (personInput "Dr Brown" [| "medical" |])
    Assert.Equal("Dr Brown", o.Record.Title)
    Assert.Equal("dentist", o.Record.Role)

[<Fact>]
let ``Steps.createPersonStub falls back to a single role-derived context`` () =
    let chat = FakeChatClient([ Responses.final "unparseable" ])
    let o = Steps.createPersonStub (chat :> IChatClient) (personInput "Coach Brian" [| "school"; "family" |])
    Assert.Equal("Coach Brian", o.Record.Title)
    Assert.Equal<string array>([| "school" |], o.Record.Context)   // first known context
    Assert.Contains("Stub", o.Body)
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (prior + 2 new).

Run: `dotnet test`
Expected: PASS — full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.createPersonStub (+ relocate knownContexts)"
```

---

## Task 4: Extract `Steps.extractRelationships`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `extractRelationships`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (rewire the relationship block)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add a relationship test)

**Interfaces:**
- Consumes: `Prompts.relationshipExtractSystem`, `Prompts.parseRelationships`, `Prompts.RelationshipExtraction`.
- Produces: `Steps.extractRelationships : IChatClient -> (resolvedSlugs:string list) -> (messageContent:string) -> Result<Prompts.RelationshipExtraction, string>`

- [ ] **Step 1: Add `extractRelationships` to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs`:

```fsharp
    /// Run the relationship-extraction prompt over the resolved co-mentioned slugs + the message,
    /// returning the parsed edges. Mirrors the former Pipeline call; the pipeline still does
    /// buildEdge/confidence-filter/reconcile/write.
    let extractRelationships (chat: IChatClient) (resolvedSlugs: string list) (messageContent: string) : Result<Prompts.RelationshipExtraction, string> =
        let user =
            sprintf "People mentioned (use these exact slugs for from/to): %s\nMessage: %s"
                (String.concat ", " resolvedSlugs) messageContent
        Prompts.parseRelationships (Agent.runConversation chat [] Prompts.relationshipExtractSystem user)
```

- [ ] **Step 2: Rewire the relationship block in Pipeline**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the inline `let user = sprintf "People mentioned …"` + `match Prompts.parseRelationships (Agent.runConversation deps.Chat [] Prompts.relationshipExtractSystem user) with` (currently ~lines 630-634) with:

```fsharp
                    match Steps.extractRelationships deps.Chat resolved msg.Content with
```

(The `Ok extraction when … -> for edge in … buildEdge/reconcile/write` body is unchanged.)

- [ ] **Step 3: Build + verify**

Run: `dotnet build 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `grep -nE "relationshipExtractSystem" src/Nameless.TaskList.Core/Pipeline.fs`
Expected: no output (the call now lives in Steps).

- [ ] **Step 4: Write the failing relationship test**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
[<Fact>]
let ``Steps.extractRelationships parses edges`` () =
    let chat = FakeChatClient([ Responses.final """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child","descriptor":"mother and son","confidence":"high"}]}""" ])
    match Steps.extractRelationships (chat :> IChatClient) [ "sarah-ashford"; "ethan-ashford" ] "Sarah picked up Ethan" with
    | Ok ex ->
        Assert.Equal(1, ex.Relationships.Length)
        Assert.Equal("parent-child", ex.Relationships.[0].Relation)
    | Error e -> failwithf "expected Ok, got Error %s" e
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (prior + 1 new).

Run: `dotnet test`
Expected: PASS — full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.extractRelationships"
```

---

## Task 5: Phase 3 scorers in Scoring.fs

**Files:**
- Modify: `eval/Nameless.TaskList.Eval/Scoring.fs` (add `scoreMatch`, `scoreNoteUpdate`, `scorePerson`, `scoreRelationships` + relationship helpers)
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs` (add tests)

**Interfaces:**
- Consumes: existing `norm`/`setF1`/`mean`/`FieldScore`/`CaseResult`/`genError`/`finish`/`frontmatterObj`/`scoreScalar`/`scoreArrayField`/`scoreTitleBody`/`expStr` (Phase 1/2); `Steps.EntityOutcome`; KB `Person`; `Prompts.RelationshipExtraction`; `Naming.slug`.
- Produces:
  - `Scoring.scoreMatch : Dataset.Case -> Result<string option, string> -> CaseResult`
  - `Scoring.scoreNoteUpdate : Dataset.Case -> Result<string, string> -> CaseResult`
  - `Scoring.scorePerson : Dataset.Case -> Result<Steps.EntityOutcome<Person>, string> -> CaseResult`
  - `Scoring.scoreRelationships : Dataset.Case -> Result<Prompts.RelationshipExtraction, string> -> CaseResult`

- [ ] **Step 1: Add the scorers + helpers to `Scoring.fs`**

Append to `eval/Nameless.TaskList.Eval/Scoring.fs` (inside `module Scoring`, after the Phase 2 scorers). `Person` is available via the existing `open Nameless.TaskList.Core.KnowledgeBase`; `Naming`/`Prompts` via `open Nameless.TaskList.Core`:

```fsharp
    /// Match decision: expected.decision "match" requires Ok (Some slug) with the right slug;
    /// "nomatch" requires Ok None; Error scores 0.
    let scoreMatch (case: Dataset.Case) (result: Result<string option, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok decision ->
            let want = expStr case.Expected "decision" |> Option.map norm |> Option.defaultValue ""
            let s =
                match want, decision with
                | "match", Some slug ->
                    let wantSlug = expStr case.Expected "slug" |> Option.map norm |> Option.defaultValue ""
                    if norm slug = wantSlug then 1.0 else 0.0
                | "nomatch", None -> 1.0
                | _ -> 0.0
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = s
              Fields = [ { Field = "decision"; Score = s; Detail = sprintf "want=%s got=%A" want decision } ]
              NoisePair = None; ParseError = None }

    /// Note-update: body-only assertions (bodyContains; titleMatches not applicable).
    let scoreNoteUpdate (case: Dataset.Case) (result: Result<string, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok body ->
            let fields = ResizeArray<FieldScore>()
            scoreTitleBody case "" body fields   // empty title -> titleMatches (if any) scores 0; cases use bodyContains
            finish case fields

    /// Person-stub: role (scalar); context/aliases/tags (arrays); title/body.
    let scorePerson (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Person>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let p = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "role" p.Role fields
                scoreArrayField fm "context" p.Context fields
                scoreArrayField fm "aliases" p.Aliases fields
                scoreArrayField fm "tags" p.Tags fields)
            scoreTitleBody case p.Title o.Body fields
            finish case fields

    let private symmetricRelations = set [ "sibling"; "partner"; "colleague"; "friend"; "other" ]

    /// Canonicalise an edge to "relation|a|b": directed relations keep from->to; symmetric ones
    /// use the sorted pair, so order does not matter.
    let private edgeKey (fromSlug: string) (toSlug: string) (relation: string) : string =
        let r = norm relation
        let a, b = norm fromSlug, norm toSlug
        if Set.contains r symmetricRelations then
            let lo, hi = if a <= b then a, b else b, a
            sprintf "%s|%s|%s" r lo hi
        else sprintf "%s|%s|%s" r a b

    /// Exact set F1 (no glob/substring) over canonical edge keys.
    let private exactSetF1 (expected: string list) (actual: string list) : float =
        match expected, actual with
        | [], [] -> 1.0
        | [], _ -> 0.0
        | _, [] -> 0.0
        | _ ->
            let es, acts = Set.ofList expected, Set.ofList actual
            let tp = Set.intersect es acts |> Set.count |> float
            let precision = tp / float (Set.count acts)
            let recall = tp / float (Set.count es)
            if precision + recall = 0.0 then 0.0 else 2.0 * precision * recall / (precision + recall)

    /// Relationship extraction: F1 over the canonical edge set (descriptor/confidence ignored).
    let scoreRelationships (case: Dataset.Case) (result: Result<Prompts.RelationshipExtraction, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok ex ->
            let actual =
                (if isNull ex.Relationships then [||] else ex.Relationships)
                |> Array.toList
                |> List.map (fun e -> edgeKey (Naming.slug e.From) (Naming.slug e.To) e.Relation)
            let expected =
                match case.Expected.TryGetProperty "relationships" with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    [ for x in v.EnumerateArray() ->
                        let g (k: string) = match x.TryGetProperty k with | true, s when s.ValueKind = JsonValueKind.String -> s.GetString() | _ -> ""
                        edgeKey (Naming.slug (g "from")) (Naming.slug (g "to")) (g "relation") ]
                | _ -> []
            let s = exactSetF1 expected actual
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = s
              Fields = [ { Field = "relationships"; Score = s; Detail = sprintf "exp=%A act=%A" expected actual } ]
              NoisePair = None; ParseError = None }
```

- [ ] **Step 2: Write the failing scorer tests**

Append to `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs`:

```fsharp
open Nameless.TaskList.Core.Prompts

let private relEdge from' to' relation : RelationshipEdge =
    { From = from'; To = to'; Relation = relation; Descriptor = null; Confidence = "high" }

[<Fact>]
let ``scoreMatch rewards the right slug and a correct no-match`` () =
    let m = genCase "task-match" """{"decision":"match","slug":"sign-up-ethan-for-swimming"}"""
    Assert.Equal(1.0, (Scoring.scoreMatch m (Ok (Some "sign-up-ethan-for-swimming"))).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreMatch m (Ok (Some "other"))).Score, 3)
    let nm = genCase "task-match" """{"decision":"nomatch"}"""
    Assert.Equal(1.0, (Scoring.scoreMatch nm (Ok None)).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreMatch nm (Ok (Some "x"))).Score, 3)

[<Fact>]
let ``scoreNoteUpdate checks bodyContains`` () =
    let c = genCase "note-update" """{"bodyContains":["expires 2027"]}"""
    Assert.Equal(1.0, (Scoring.scoreNoteUpdate c (Ok "## Medical aid\nPolicy expires 2027.")).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreNoteUpdate c (Ok "nothing relevant")).Score, 3)

[<Fact>]
let ``scoreRelationships F1 with symmetric pair order-insensitive`` () =
    let c = genCase "relationship-extract" """{"relationships":[{"from":"a","to":"b","relation":"sibling"}]}"""
    // symmetric: from/to swapped still matches
    let ex : RelationshipExtraction = { Relationships = [| relEdge "b" "a" "sibling" |] }
    Assert.Equal(1.0, (Scoring.scoreRelationships c (Ok ex)).Score, 3)
    // wrong relation -> 0
    let ex2 : RelationshipExtraction = { Relationships = [| relEdge "a" "b" "friend" |] }
    Assert.Equal(0.0, (Scoring.scoreRelationships c (Ok ex2)).Score, 3)

[<Fact>]
let ``scoreRelationships directed relation respects from-to`` () =
    let c = genCase "relationship-extract" """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child"}]}"""
    let swapped : RelationshipExtraction = { Relationships = [| relEdge "ethan-ashford" "sarah-ashford" "parent-child" |] }
    Assert.Equal(0.0, (Scoring.scoreRelationships c (Ok swapped)).Score, 3)   // direction matters
    let right : RelationshipExtraction = { Relationships = [| relEdge "sarah-ashford" "ethan-ashford" "parent-child" |] }
    Assert.Equal(1.0, (Scoring.scoreRelationships c (Ok right)).Score, 3)
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalScoringTests"`
Expected: PASS (prior + 4 new).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Scoring.fs tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs
git commit -m "feat: eval Phase 3 scorers (match/note-update/person/relationships)"
```

---

## Task 6: Runner wiring for the seven Phase 3 steps

**Files:**
- Modify: `eval/Nameless.TaskList.Eval/Runner.fs` (seven new `runCase` branches + a `personInput` helper)
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs` (add runner tests)

**Interfaces:**
- Consumes: `Steps.matchTask/matchNote/matchPerson/updateTask/updateNote/createPersonStub/extractRelationships`; `Scoring.scoreMatch/scoreTask/scoreNoteUpdate/scorePerson/scoreRelationships`; the existing `inputStr`/`inputStrArr` helpers.
- Produces: seven new `runCase` branches.

- [ ] **Step 1: Add the helper + seven branches to `Runner.fs`**

In `eval/Nameless.TaskList.Eval/Runner.fs`, add a private helper near `genInput`:

```fsharp
    let private personGenInput (case: Dataset.Case) : Steps.GenInput =
        { Intent = inputStr case "name" ""
          Raw = ""; ReferenceDate = ""
          Contexts = inputStrArr case "contexts"
          Urgency = ""; TopicPath = ""; MessagePath = ""
          PeopleSlugs = [||]; TaskPaths = [] }

    let private inputSlugList (case: Dataset.Case) : string list =
        inputStrArr case "slugs" |> Array.toList
```

Then add seven branches to the `match case.Step with` in `runCase` (`floor`/`topK` are the existing `runCase` params; `vault` is the seeded world from `Worlds.load`):

```fsharp
        | "task-match" ->
            let r = try Ok (Steps.matchTask chat embedder vault (inputStr case "intent" "") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "note-match" ->
            let r = try Ok (Steps.matchNote chat embedder vault (inputStr case "intent" "") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "person-match" ->
            let r = try Ok (Steps.matchPerson chat embedder vault (inputStr case "name" "") (inputStrArr case "contexts") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "task-update" ->
            let r = try Steps.updateTask chat (inputStr case "existingFile" "") (inputStr case "intent" "") (inputStr case "message" "") with ex -> Error ex.Message
            Scoring.scoreTask case r
        | "note-update" ->
            let r = try Ok (Steps.updateNote chat (inputStr case "existingBody" "") (inputStr case "intent" "") (inputStr case "message" "")) with ex -> Error ex.Message
            Scoring.scoreNoteUpdate case r
        | "person-stub-create" ->
            let r = try Ok (Steps.createPersonStub chat (personGenInput case)) with ex -> Error ex.Message
            Scoring.scorePerson case r
        | "relationship-extract" ->
            let r = try Steps.extractRelationships chat (inputSlugList case) (inputStr case "message" "") with ex -> Error ex.Message
            Scoring.scoreRelationships case r
```

- [ ] **Step 2: Write the failing runner tests**

Append to `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs` (reuses `runGen` from the Phase 2 additions, plus a match variant that seeds a world candidate):

```fsharp
[<Fact>]
let ``runCase scores a relationship-extract case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "relationship-extract"
                """{"slugs":["sarah-ashford","ethan-ashford"],"message":"Sarah picked up her son Ethan."}"""
                """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child"}]}"""
                """{"relationships":[{"from":"sarah-ashford","to":"ethan-ashford","relation":"parent-child","descriptor":"mother and son","confidence":"high"}]}"""
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a task-match case against a seeded candidate`` () =
    withDataset (fun root ->
        // seed a candidate task into the _base world so matchTask has something to shortlist
        let worldDir = Path.Combine(root, "_worlds", "_base", "tasks", "pending")
        Directory.CreateDirectory worldDir |> ignore
        File.WriteAllText(Path.Combine(worldDir, "sign-up-ethan-for-swimming.md"),
                          "---\ntype: Task\ntitle: Sign up Ethan for swimming\nstatus: pending\n---\nRegister Ethan.\n")
        Directory.CreateDirectory(Path.Combine(root, "task-match")) |> ignore
        File.WriteAllText(Path.Combine(root, "task-match", "c.json"),
            """{"id":"c","step":"task-match","world":"_base","input":{"intent":"Register Ethan for swimming"},"expected":{"decision":"match","slug":"sign-up-ethan-for-swimming"}}""")
        let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"sign-up-ethan-for-swimming","confidence":0.9,"match_reason":"same","new_topic_title":null}""" ])
        let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])
        let case = Dataset.load root [ "task-match" ] |> List.head
        let r = Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case
        Assert.Equal(1.0, r.Score, 3))
```

- [ ] **Step 3: Run the tests + build**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalRunnerTests"`
Expected: PASS (prior + 2 new).

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Runner.fs tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs
git commit -m "feat: eval runner wiring for Phase 3 steps (match/update/person-stub/relationships)"
```

---

## Task 7: Seed the Phase 3 dataset + extend the integrity guard

**Files:**
- Create in the `ashford-family` world: `eval/dataset/_worlds/ashford-family/people/family/ethan-ashford.md`, `.../tasks/pending/sign-up-ethan-for-swimming.md`, `.../notes/medical-aid-details.md`
- Create cases under `eval/dataset/{task-match,note-match,person-match,task-update,note-update,person-stub-create,relationship-extract}/`
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs` (extend `knownSteps`)
- Modify: `eval/dataset/README.md` (document the Phase 3 step schemas)

**Interfaces:**
- Consumes: `Dataset.load`, `Worlds.load`. No new production code.

- [ ] **Step 1: Add the candidate fixtures to the `ashford-family` world**

`eval/dataset/_worlds/ashford-family/people/family/ethan-ashford.md`:

```markdown
---
type: Person
title: Ethan Ashford
role: son
context: family
aliases: ["Ethan"]
---
The KB owner's son.
```

`eval/dataset/_worlds/ashford-family/tasks/pending/sign-up-ethan-for-swimming.md`:

```markdown
---
type: Task
title: Sign up Ethan for swimming
status: pending
priority: medium
due: ""
context: [family]
people: [ethan-ashford]
---
Register Ethan for the term's swimming lessons.
```

`eval/dataset/_worlds/ashford-family/notes/medical-aid-details.md`:

```markdown
---
type: Note
title: Medical aid details
context: [medical]
tags: [insurance]
---
## Medical aid
Membership number MA-4471829.
```

- [ ] **Step 2: Author the match cases**

`eval/dataset/task-match/same-action.json`:

```json
{
  "id": "same-action",
  "step": "task-match",
  "tags": ["same-action-restated"],
  "world": "ashford-family",
  "input": { "intent": "Register Ethan for swimming lessons" },
  "expected": { "decision": "match", "slug": "sign-up-ethan-for-swimming" }
}
```

`eval/dataset/task-match/different-action.json`:

```json
{
  "id": "different-action",
  "step": "task-match",
  "tags": ["distinct-action"],
  "world": "ashford-family",
  "input": { "intent": "Buy Sarah a birthday present" },
  "expected": { "decision": "nomatch" }
}
```

`eval/dataset/note-match/same-subject.json`:

```json
{
  "id": "same-subject",
  "step": "note-match",
  "tags": ["same-record"],
  "world": "ashford-family",
  "input": { "intent": "Our medical aid membership number" },
  "expected": { "decision": "match", "slug": "medical-aid-details" }
}
```

`eval/dataset/person-match/new-person.json`:

```json
{
  "id": "new-person",
  "step": "person-match",
  "tags": ["distinct-person"],
  "world": "ashford-family",
  "input": { "name": "Coach Brian", "contexts": ["school"] },
  "expected": { "decision": "nomatch" }
}
```

- [ ] **Step 3: Author the update cases**

`eval/dataset/task-update/raise-priority-and-due.json`:

```json
{
  "id": "raise-priority-and-due",
  "step": "task-update",
  "tags": ["merge-due", "merge-priority"],
  "world": "ashford-family",
  "input": {
    "existingFile": "---\ntype: Task\ntitle: Sign up Ethan for swimming\nstatus: pending\npriority: medium\ndue: \"\"\ncontext: [family]\npeople: [ethan-ashford]\n---\nRegister Ethan for the term's swimming lessons.\n",
    "intent": "Swimming sign-up closes this Friday — it is now urgent",
    "message": "Reminder: swimming registration closes Friday 3 July, don't miss it!"
  },
  "expected": {
    "frontmatter": { "status": "pending", "title": "Sign up Ethan for swimming" },
    "titleMatches": "[Ss]wimming",
    "bodyContains": ["swimming"]
  }
}
```

`eval/dataset/note-update/add-expiry.json`:

```json
{
  "id": "add-expiry",
  "step": "note-update",
  "tags": ["fold-in-fact"],
  "world": "ashford-family",
  "input": {
    "existingBody": "## Medical aid\nMembership number MA-4471829.",
    "intent": "Medical aid expires end of 2027",
    "message": "Note the medical aid is valid until 31 December 2027."
  },
  "expected": { "bodyContains": ["MA-4471829", "2027"] }
}
```

- [ ] **Step 4: Author the person-stub-create case**

`eval/dataset/person-stub-create/dentist.json`:

```json
{
  "id": "dentist",
  "step": "person-stub-create",
  "tags": ["role-to-context", "stub-body"],
  "world": "ashford-family",
  "input": { "name": "Dr Brown", "contexts": ["medical"] },
  "expected": {
    "frontmatter": { "context": ["medical"] },
    "titleMatches": "[Bb]rown",
    "bodyContains": ["Stub"]
  }
}
```

- [ ] **Step 5: Author the relationship-extract cases**

`eval/dataset/relationship-extract/parent-child.json`:

```json
{
  "id": "parent-child",
  "step": "relationship-extract",
  "tags": ["directed"],
  "world": "ashford-family",
  "input": { "slugs": ["sarah-ashford", "ethan-ashford"], "message": "Sarah picked up her son Ethan from swimming." },
  "expected": { "relationships": [ { "from": "sarah-ashford", "to": "ethan-ashford", "relation": "parent-child" } ] }
}
```

`eval/dataset/relationship-extract/none.json` (no stated relationship → expect no edges):

```json
{
  "id": "none",
  "step": "relationship-extract",
  "tags": ["no-edge"],
  "world": "ashford-family",
  "input": { "slugs": ["sarah-ashford", "dr-naidoo"], "message": "Sarah asked what time the pharmacy closes." },
  "expected": { "relationships": [] }
}
```

- [ ] **Step 6: Extend the integrity test's known-steps set**

In `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs`, change `knownSteps` to include all thirteen steps:

```fsharp
let private knownSteps =
    set [ "classify"; "topic-match"
          "task-create"; "event-create"; "commitment-create"; "note-create"
          "task-match"; "note-match"; "person-match"
          "task-update"; "note-update"; "person-stub-create"; "relationship-extract" ]
```

- [ ] **Step 7: Document the Phase 3 schemas in the README**

In `eval/dataset/README.md`, add to the "Case schema" section:

```markdown
- **task-match / note-match / person-match** `input`: `intent` (task/note) or `name` + `contexts`
  (person); the case's `world` seeds the candidate entities/people. `expected`:
  `{"decision":"match","slug":"…"}` or `{"decision":"nomatch"}`. Needs the live embedder.
- **task-update / note-update** `input`: `existingFile` (task) or `existingBody` (note) + `intent` +
  `message`. `expected`: task = `frontmatter`/`titleMatches`/`bodyContains`; note = `bodyContains` (body-only).
- **person-stub-create** `input`: `name` + `contexts`. `expected`: `frontmatter` (`role`/`context`/
  `aliases`) + `titleMatches`/`bodyContains`.
- **relationship-extract** `input`: `slugs` (co-mentioned resolved slugs) + `message`. `expected`:
  `{"relationships":[{"from","to","relation"}, …]}`, scored as an edge-set F1 (directed relations keep
  from→to; symmetric relations are order-insensitive; descriptor/confidence ignored).
```

- [ ] **Step 8: Run the integrity tests + full suite**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalDatasetIntegrityTests"`
Expected: PASS (3 tests; every committed case — including the nine new ones — parses, names a known step, world loads).

Run: `dotnet test`
Expected: PASS — full suite green.

- [ ] **Step 9: (Optional, needs Ollama) smoke-run the Phase 3 steps**

If a local Ollama is available:
Run: `dotnet run --project eval/Nameless.TaskList.Eval -- --steps task-match,note-match,person-match,task-update,note-update,person-stub-create,relationship-extract --report /tmp/eval-p3.md`
Expected: a scorecard with the seven new step rows; writes `/tmp/eval-p3.md` + `.json`. (No Ollama → exit 2 with a clear message; acceptable.)

- [ ] **Step 10: Commit**

```bash
git add eval/dataset tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs
git commit -m "feat: seed Phase 3 eval gold dataset (match/update/person-stub/relationships)"
```

---

## Final verification

- [ ] Run `dotnet build` — all projects compile, 0 warnings.
- [ ] Run `dotnet test` — full default suite green (existing + new Steps/eval tests; no Ollama needed).
- [ ] Confirm `git status` clean and the branch holds the seven task commits.
- [ ] (If Ollama available) one real `dotnet run --project eval/Nameless.TaskList.Eval` over all steps to eyeball the complete scorecard (classify, topic-match, the four creators, and the seven Phase 3 steps).
