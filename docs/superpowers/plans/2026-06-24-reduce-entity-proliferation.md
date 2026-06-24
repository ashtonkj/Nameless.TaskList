# Reducing Entity Proliferation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the per-message pipeline prefer updating related existing entities over creating new ones — alias-aware people with role-driven context, durable-reference-only notes with match/merge, and tighter topic matching.

**Architecture:** Three independent changes to `Nameless.TaskList.Core` and prompts. People gain an `aliases` field and deterministic name+alias resolution with a safe self-grow gate and role-driven context. Notes become durable-reference-only (tightened classifier criteria) and gain embedding-shortlist → LLM-confirm → LLM-merge, mirroring the existing topic flow. Topic matching is tuned via prompt + config to consolidate. All logic stays in `Pipeline.fs`/`Prompts.fs`; adapters and the host only gain config wiring.

**Tech Stack:** F# / .NET 10, System.Text.Json, YamlDotNet (frontmatter), xUnit with in-memory `FakeVault`/`FakeChatClient`/`FakeEmbedder`.

## Global Constraints

- `dotnet build` AND `dotnet test` must both pass before every commit.
- KB records that cross a serialization boundary stay public and `[<CLIMutable>]` (a `private` record serializes to `{}`); `Note` and `Person` are already `[<CLIMutable>]`.
- New frontmatter fields mirror the snake_case YAML key as a PascalCase record field and rely on `UnderscoredNamingConvention` (so `Aliases` ↔ `aliases`) — no explicit attribute.
- Person matching is DETERMINISTIC: exact match on `Naming.slug` over a person's title + aliases. No fuzzy/embedding/LLM identity guessing. The only "merge" gate is exact slug-equality on the LLM-proposed canonical title.
- Role → context mapping (person): doctor/dentist/specialist/physio/nurse → `medical`; teacher/principal/coach/tutor → `school`; accountant/advisor/banker/broker → `finance`; colleague/manager/client/boss → `professional`; relative/friend/neighbour → `family`; unknown role → message's first known context, else `family`. `knownContexts = [family; medical; school; finance; professional]`.
- Config values, verbatim: `NoteMatch:TopK` = 5, `NoteMatch:SimilarityFloor` = 0.35 (new); `TopicMatch:SimilarityFloor` = 0.35 (changed from 0.5); topic match confidence cutoff 0.6 (in the prompt, was 0.75).
- Notes are durable cross-topic reference facts only; per-message observations fold into the topic. The note match flow reuses the `TopicMatch` record and `Prompts.parseTopicMatch` (its `topic_slug`/`new_topic_title` fields carry the matched-note-slug / new-note-title) — this reuse is intentional.
- The `FakeChatClient` dequeues scripted `ChatResponse`s in call order; every LLM call the pipeline makes must be scripted. Keep `tasks/events/commitments` empty in tests not exercising them to minimize call count.

---

### Task 1: Alias-aware, role-contextual people

Add an `aliases` field to `Person`, replace exact-single-slug resolution with a name+alias index, add a safe self-grow gate, and make context role-driven.

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs:147-155` (add `Aliases` to `Note`? no — `Person` at 158-166)
- Modify: `src/Nameless.TaskList.Core/Prompts.fs:144-156` (`personStubSystem`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs:50-51` (`personExists` → index) and `:391-421` (person step)
- Modify: `tests/Nameless.TaskList.Core.Tests/CodecTests.fs:65` (add `Aliases` to the test Person)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (append new tests)

**Interfaces:**
- Produces: `Person` record gains `Aliases: string array` (8th field, after `Tags`). Person step now: resolves a mention via title+alias slugs; on miss, generates a stub and either appends an alias to a canonical-title match or creates a new person filed by canonical-title slug under a role-derived context.

- [ ] **Step 1: Add `Aliases` to the `Person` record**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, the `Person` record (currently ends with `Tags: string array`) becomes:

```fsharp
[<CLIMutable>]
type Person =
    { Type: string
      Title: string
      Role: string
      Context: string array
      Channel: string
      Phone: string
      Email: string
      Tags: string array
      Aliases: string array }
```

- [ ] **Step 2: Fix the two existing `Person` construction sites so the project compiles**

In `tests/Nameless.TaskList.Core.Tests/CodecTests.fs:65`, add `Aliases` to the literal (place it after `Tags`):

```fsharp
        { Type = "Person"; Title = "Dr Naidoo"; Role = "Paediatrician"; Context = [| "medical" |]
          Channel = ""; Phone = ""; Email = ""; Tags = [| "paediatrician" |]; Aliases = [| "Naidoo" |] }
```

In `src/Nameless.TaskList.Core/Pipeline.fs:411`, the fallback person record gains `Aliases = [||]` (full record is rewritten in Step 6, but add the field now if you build between steps).

- [ ] **Step 3: Write the failing person tests**

Append to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
// A message whose sender mentions one person, with configurable people_mentioned.
let private personMessage () : ChatMessage =
    { sampleMessage () with Content = "Took Ethan to see the doctor today" }

let private personDeps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }

let private seedPerson (vault: FakeVault) (path: string) (title: string) (context: string) (aliases: string list) =
    let aliasYaml = if List.isEmpty aliases then "[]" else "[" + String.concat ", " aliases + "]"
    vault.Seed(path, sprintf "---\ntype: Person\ntitle: %s\nrole: spouse\ncontext: [%s]\nchannel: \"\"\nphone: \"\"\nemail: \"\"\ntags: []\naliases: %s\n---\nstub\n" title context aliasYaml)

[<Fact>]
let ``person mention matching a recorded alias does not create a duplicate`` () =
    let vault = FakeVault()
    seedPerson vault "people/family/sarah-smith.md" "Sarah Smith" "family" ["Mom"]
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"note from mom","action_required":false,"urgency":"low","people_mentioned":["Mom"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Note from mom"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // No person-stub response scripted: the mention must resolve to the existing alias and skip the LLM.
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal<string list>([ "people/family/sarah-smith.md" ], peopleFiles)
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``new surface form whose canonical title matches an existing person appends an alias`` () =
    let vault = FakeVault()
    seedPerson vault "people/family/sarah-smith.md" "Sarah Smith" "family" []
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"sarah update","action_required":false,"urgency":"low","people_mentioned":["Sarah"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Sarah update"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Sarah Smith\nrole: spouse\ncontext: [family]\naliases: []\n---\nSarah is the owner's wife. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let peopleFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("people/")) |> List.ofSeq
        Assert.Equal<string list>([ "people/family/sarah-smith.md" ], peopleFiles)   // no new file
        Assert.Contains("Sarah", vault.Files.["people/family/sarah-smith.md"])        // alias recorded
        Assert.Contains("aliases", vault.Files.["people/family/sarah-smith.md"])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a doctor is filed under the medical context, not family`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"saw the paediatrician","action_required":false,"urgency":"low","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Paediatrician visit"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Dr Naidoo\nrole: paediatrician\ncontext: [medical]\naliases: []\n---\nEthan's paediatrician. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = personDeps (FakeMessages(Some(personMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.ContainsKey("people/medical/dr-naidoo.md"))
        Assert.False(vault.Files.ContainsKey("people/family/dr-naidoo.md"))
    | other -> failwithf "expected Processed, got %A" other
```

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: the three new tests FAIL (duplicate people created / wrong context) or the project fails to build until Steps 5-6 land.

- [ ] **Step 5: Update `personStubSystem` for role-driven context**

Replace `personStubSystem` (`src/Nameless.TaskList.Core/Prompts.fs:144-156`) with:

```fsharp
    let personStubSystem = """You are creating a person stub entry for a personal knowledge base.
A new person has been mentioned in a message. Create a minimal person file
based on the available information.

Rules:
- title: the person's canonical full name if known (e.g. "Dr Naidoo", "Sarah Smith"); if no name is known, use their role (e.g. "Ethan's Class Teacher"). Always prefer the canonical name over a nickname or relationship word.
- role: their relationship to the KB owner or their professional role.
- context: choose by the person's ROLE, not the chat it appeared in — one of [family, medical, school, finance, professional]:
    doctor / dentist / specialist / physio / nurse -> medical
    teacher / principal / coach / tutor -> school
    accountant / advisor / banker / broker -> finance
    colleague / manager / client / boss -> professional
    relative / friend / neighbour -> family
  If the role is genuinely unknown, omit context (leave it empty) and the pipeline will fall back.
- aliases: array of other surface forms this person is referred to by (nicknames, relationship words like "Mom", first-name-only). Use [] if none.
- All other unknown fields should be null or omitted.
- Body: 1 sentence describing who this person is and how they relate to the KB owner.
  End with: "⚠ Stub — details to be completed."

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 6: Replace the person resolution + creation step in `Pipeline.fs`**

Replace `personExists` (`src/Nameless.TaskList.Core/Pipeline.fs:50-51`):

```fsharp
    // Build a (slug -> person file path) index over every person's title + aliases.
    let private peopleIndex (vault: IVault) : (string * string list) list =
        vault.ListFilesRecursive "people"
        |> List.choose (fun path ->
            try
                let mf = MarkdownFile.FromString (vault.Read path)
                match mf.FrontMatter with
                | Some fm ->
                    let p = Frontmatter.deserialize<Person> fm
                    let aliasSlugs = if isNull p.Aliases then [] else p.Aliases |> Array.toList |> List.map Naming.slug
                    let keys = (Naming.slug p.Title :: aliasSlugs) |> List.filter (fun s -> s <> "")
                    Some (path, keys)
                | None -> None
            with _ -> None)

    /// Resolve a mention slug to an existing person file via title or alias (exact, normalized).
    let private resolvePerson (index: (string * string list) list) (mentionSlug: string) : string option =
        index |> List.tryPick (fun (path, keys) -> if List.contains mentionSlug keys then Some path else None)
```

Replace the person step (`src/Nameless.TaskList.Core/Pipeline.fs:391-421`, the `classification.PeopleMentioned |> Array.toList |> List.iter (...)` block) with:

```fsharp
            // --- Step: resolve mentioned people (alias-aware) and create stubs only when genuinely new ---
            let messageCtx =
                classification.Contexts
                |> Array.tryFind (fun c -> List.contains c knownContexts)
                |> Option.defaultValue "family"
            let index = peopleIndex deps.Vault
            classification.PeopleMentioned
            |> Array.toList
            |> List.iter (fun name ->
                let mentionSlug = Naming.slug name
                if System.String.IsNullOrWhiteSpace mentionSlug then ()
                elif (resolvePerson index mentionSlug).IsSome then ()   // already known by name or alias
                else
                    let user =
                        sprintf "Person mentioned: %s\nMessage context: %s\nMentioned in: %s"
                            name (String.concat ", " classification.Contexts) messagePath
                    let raw = Agent.runConversation deps.Chat [] Prompts.personStubSystem user
                    let record, body =
                        try
                            let parsed = MarkdownFile.FromString (stripFences raw)
                            match parsed.FrontMatter with
                            | Some fm ->
                                let p = Frontmatter.deserialize<Person> fm
                                if not (System.String.IsNullOrWhiteSpace p.Title) then { p with Type = "Person" }, parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            { Type = "Person"; Title = name; Role = ""; Context = [| messageCtx |]
                              Channel = ""; Phone = ""; Email = ""; Tags = [||]; Aliases = [||] },
                            sprintf "%s\n\n⚠ Stub — details to be completed." name
                    let canonicalSlug = Naming.slug record.Title
                    match resolvePerson index canonicalSlug with
                    | Some existingPath ->
                        // Canonical name already exists — record this surface form as an alias instead of duplicating.
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
                    | None ->
                        // Genuinely new person: file by canonical slug under a role-derived context.
                        let ctx =
                            let candidate =
                                if not (isNull record.Context) && record.Context.Length > 0
                                   && not (System.String.IsNullOrWhiteSpace record.Context.[0])
                                then record.Context.[0] else messageCtx
                            if List.contains candidate knownContexts then candidate else messageCtx
                        // If the surface mention differs from the canonical title, seed it as an alias.
                        let seededAliases =
                            if mentionSlug <> canonicalSlug && not (System.String.IsNullOrWhiteSpace name) then
                                let existing = if isNull record.Aliases then [||] else record.Aliases
                                if existing |> Array.exists (fun a -> Naming.slug a = mentionSlug) then existing
                                else Array.append existing [| name.Trim() |]
                            else (if isNull record.Aliases then [||] else record.Aliases)
                        let finalRecord = { record with Aliases = seededAliases }
                        deps.Vault.Write(Naming.personPath ctx canonicalSlug,
                                         MarkdownFile.ToString (Frontmatter.serialize finalRecord) body))
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass, including the three new person tests.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/CodecTests.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: alias-aware person matching with role-driven context

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 2: Durable-reference notes with match-and-merge

Tighten the classifier's note criteria, and replace unconditional note creation with embedding-shortlist → LLM-confirm → LLM-merge (mirroring topics). Adds `NoteMatch` config + `PipelineDeps` fields.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (`classifySystem` note line; `noteCreateSystem`; add `noteMatchSystem`, `noteUpdateSystem`, `noteUpdateUser`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs:8-17` (`PipelineDeps` fields), `:363-389` (note step)
- Modify: `src/Nameless.TaskList/Program.fs:60-65` (`buildDeps`) and the `appsettings.json` (NoteMatch keys)
- Modify: `src/Nameless.TaskList/appsettings.json`
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (the `deps` helper + every inline `PipelineDeps` literal: lines ~33, 262, 280, 294, 314, 334, 352, 366, 386 — add the two new fields)
- Modify: `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` (its `PipelineDeps` construction)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (append note match/merge tests)

**Interfaces:**
- Consumes: `Note` record, `Naming.notePath`, `Similarity.cosine`, `Prompts.parseTopicMatch`, `freePath`, `stripFences`.
- Produces: `PipelineDeps` gains `NoteTopK: int` and `NoteSimilarityFloor: float` (after `SimilarityFloor`). Note step returns one path per note intent (matched-and-merged, or newly created), feeding `messageRecord.SpawnedNotes`.

- [ ] **Step 1: Add `NoteTopK`/`NoteSimilarityFloor` to `PipelineDeps` and every constructor**

In `src/Nameless.TaskList.Core/Pipeline.fs`, the `PipelineDeps` record gains two fields after `SimilarityFloor`:

```fsharp
    type PipelineDeps =
        { Messages: IMessageSource
          Vault: IVault
          Chat: IChatClient
          Model: string
          Embedder: IEmbedder
          TopK: int
          SimilarityFloor: float
          NoteTopK: int
          NoteSimilarityFloor: float
          Vision: IVision
          Transcriber: ITranscriber }
```

Then add `NoteTopK = ...; NoteSimilarityFloor = ...` to every construction site:
- `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` — the `deps` helper (line ~33) and EACH inline literal (lines ~262, 280, 294, 314, 334, 352, 366, 386) and the new `personDeps` helper from Task 1: append `NoteTopK = 5; NoteSimilarityFloor = 0.5` (place next to `TopK`/`SimilarityFloor`). The two fields are identical in every test literal.
- `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` — add `NoteTopK = 5; NoteSimilarityFloor = 0.35` to its `PipelineDeps` literal.
- `src/Nameless.TaskList/Program.fs` `buildDeps` (line ~60): handled in Step 7.

Build after this step to confirm every literal is updated: `dotnet build` (expect success, behavior unchanged).

- [ ] **Step 2: Write the failing note tests**

Append to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
let private noteDeps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // constant vector => cosine 1.0, always shortlisted
      TopK = 5; SimilarityFloor = 0.5; NoteTopK = 5; NoteSimilarityFloor = 0.35
      Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber") :> ITranscriber }

[<Fact>]
let ``a note matching an existing note merges into it instead of creating a new file`` () =
    let vault = FakeVault()
    vault.Seed("notes/medical-aid-details.md", "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\npeople_linked: []\ntags: []\nsource: \"\"\nlast_verified: \"\"\n---\n## Membership\nDiscovery Health, plan Classic.\n")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"medical aid membership number is 12345","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Discovery Health membership number 12345"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Medical aid number"}"""
    let noteMatch = Responses.final """{"match":true,"topic_slug":"medical-aid-details","confidence":0.9,"match_reason":"same note","new_topic_title":null}"""
    let noteMerged = Responses.final "## Membership\nDiscovery Health, plan Classic. Membership number 12345."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; noteMatch; noteMerged; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let noteFiles = vault.Files.Keys |> Seq.filter (fun k -> k.StartsWith("notes/")) |> List.ofSeq
        Assert.Equal<string list>([ "notes/medical-aid-details.md" ], noteFiles)   // no new note file
        Assert.Contains("12345", vault.Files.["notes/medical-aid-details.md"])     // merged content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``a note with no existing notes creates a new note file`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"record allergy","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Ethan is allergic to penicillin"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Allergy"}"""
    let noteFile = Responses.final "---\ntype: Note\ntitle: Ethan penicillin allergy\ncontext: [medical]\ntags: [allergy]\n---\nEthan is allergic to penicillin."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // Empty notes dir => no shortlist => no noteMatch call; just noteCreate.
    let chat = FakeChatClient([ classify; topicMatch; noteFile; topicBody ])
    let d = noteDeps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        Assert.True(vault.Files.ContainsKey("notes/ethan-penicillin-allergy.md"))
    | other -> failwithf "expected Processed, got %A" other
```

- [ ] **Step 3: Run the note tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: the merge test FAILS (a second note file is created) until the match flow lands; build may fail until Step 5/6 add `noteMatchSystem`/`noteUpdateSystem`.

- [ ] **Step 4: Tighten note criteria in the prompts**

In `src/Nameless.TaskList.Core/Prompts.fs`, replace the `"notes"` line inside `classifySystem`'s JSON template (currently `"notes": ["any factual information worth storing"]`) with:

```
    "notes": ["only DURABLE reference facts worth keeping long-term and across conversations — account/policy/membership numbers, addresses, contact details, medical records, standing preferences. Do NOT create notes for per-message observations, status updates, or anything specific to a single ongoing conversation; those belong to the topic. Empty array if none."]
```

Replace `noteCreateSystem` (`Prompts.fs:133-142`) with:

```fsharp
    let noteCreateSystem = """You are creating a Note entry for a personal knowledge base.
A Note is a DURABLE, evolving reference document for facts that stay useful across many conversations
(e.g. account numbers, contact details, medical records, standing preferences) — not a per-message log.

Rules:
- title: short noun phrase naming the reference subject (e.g. "Medical aid details").
- context: array — choose from [family, medical, school, finance, professional, personal-kb].
- tags: array of short lowercase tags (use [] if none).
- Body: organise the fact under a short markdown section heading; include specifics (numbers, names, dates).

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 5: Add `noteMatchSystem`, `noteUpdateSystem`, and `noteUpdateUser`**

In `src/Nameless.TaskList.Core/Prompts.fs`, after `topicUpdateSystem` (around line 171), add:

```fsharp
    let noteMatchSystem = """You are a knowledge base assistant. Decide whether a new durable fact
belongs to an existing reference note, or whether it is a new note.

You are given the new note's intent and a list of candidate notes (slug, title, summary).
Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of the matched note, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation",
  "new_topic_title": "if match is false, a concise title for the new note, else null"
}

Rules:
- Match only if the new fact is about the same subject as an existing note (e.g. another detail of the same account, person, or record).
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object."""

    let noteUpdateSystem = """You are updating a durable reference note in a personal knowledge base.
You are given the current note body and a new fact to incorporate.

Rewrite the note body to fold in the new fact. Keep it concise and organised under markdown section
headings. Preserve existing facts; correct them only if the new information supersedes them.

Respond ONLY with the updated markdown body (no frontmatter, no explanation)."""

    let noteUpdateUser (existingBody: string) (intent: string) (raw: string) : string =
        sprintf "Current note body:\n%s\n\nNew fact (intent):\n%s\n\nSource message raw text:\n%s"
            existingBody intent raw
```

- [ ] **Step 6: Replace the note step in `Pipeline.fs`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the note `EntitySpec`/`writeEntities` block (`:363-389`, from `// --- Step: create note files ---` through `let notePaths = writeEntities deps noteSpec ...`) with:

```fsharp
            // --- Step: notes (durable reference only) — match an existing note and merge, else create ---
            let interpretNote (stripped: string) (intent: string) : Note * string =
                try
                    let parsed = MarkdownFile.FromString stripped
                    match parsed.FrontMatter with
                    | Some fm ->
                        let n = Frontmatter.deserialize<Note> fm
                        if not (System.String.IsNullOrWhiteSpace n.Title) then { n with Type = "Note"; Source = messagePath }, parsed.Content
                        else raise (System.Exception("empty title"))
                    | None -> raise (System.Exception("no frontmatter"))
                with _ ->
                    { Type = "Note"; Title = intent; Context = classification.Contexts
                      PeopleLinked = classification.PeopleMentioned; Tags = [||]
                      Source = messagePath; LastVerified = "" }, intent

            let createNewNote (intent: string) : string =
                let raw =
                    Agent.runConversation deps.Chat [] Prompts.noteCreateSystem
                        (sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) messagePath)
                let record, body = interpretNote (stripFences raw) intent
                let text = MarkdownFile.ToString (Frontmatter.serialize record) body
                let path = freePath deps.Vault (Naming.notePath record.Title)
                deps.Vault.Write(path, text)
                path

            let existingNotes =
                deps.Vault.ListFiles "notes"
                |> List.choose (fun path ->
                    try
                        let mf = MarkdownFile.FromString (deps.Vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let n = Frontmatter.deserialize<Note> fm
                            Some (System.IO.Path.GetFileNameWithoutExtension(path), n.Title, mf.Content.Trim())
                        | None -> None
                    with _ -> None)

            let processNote (intent: string) : string =
                let shortlist =
                    if List.isEmpty existingNotes then None
                    else
                        try
                            let iv = deps.Embedder.Embed intent
                            existingNotes
                            |> List.map (fun (slug, title, summary) ->
                                slug, title, summary, Similarity.cosine iv (deps.Embedder.Embed (title + "\n" + summary)))
                            |> List.filter (fun (_, _, _, s) -> s >= deps.NoteSimilarityFloor)
                            |> List.sortByDescending (fun (_, _, _, s) -> s)
                            |> List.truncate deps.NoteTopK
                            |> Some
                        with _ -> None
                match shortlist with
                | Some (_ :: _ as candidates) ->
                    let candidateText =
                        candidates
                        |> List.map (fun (slug, title, summary, _) -> sprintf "slug: %s\ntitle: %s\nsummary: %s" slug title summary)
                        |> String.concat "\n\n"
                    let payload = sprintf "New note intent: %s\n\nCandidate notes:\n%s" intent candidateText
                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] Prompts.noteMatchSystem payload) with
                    | Error _ -> createNewNote intent
                    | Ok m ->
                        let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                        let matched = candidates |> List.tryFind (fun (s, _, _, _) -> s.ToLowerInvariant() = normalized)
                        match m.Match, matched with
                        | true, Some (slug, _, _, _) ->
                            let path = sprintf "notes/%s.md" slug
                            try
                                let existing = MarkdownFile.FromString (deps.Vault.Read path)
                                match existing.FrontMatter with
                                | Some fm ->
                                    let n = Frontmatter.deserialize<Note> fm
                                    let mergedBody =
                                        Agent.runConversation deps.Chat [] Prompts.noteUpdateSystem
                                            (Prompts.noteUpdateUser existing.Content intent msg.Content)
                                    let merged =
                                        { n with
                                            LastVerified = isoTimestamp msg.Timestamp
                                            Context = Array.append (if isNull n.Context then [||] else n.Context) classification.Contexts |> Array.distinct
                                            PeopleLinked = Array.append (if isNull n.PeopleLinked then [||] else n.PeopleLinked) classification.PeopleMentioned |> Array.distinct }
                                    deps.Vault.Write(path, MarkdownFile.ToString (Frontmatter.serialize merged) mergedBody)
                                    path
                                | None -> createNewNote intent
                            with _ -> createNewNote intent
                        | _ -> createNewNote intent
                | _ -> createNewNote intent

            let notePaths = classification.Entities.Notes |> Array.toList |> List.map processNote
```

> Note: `isoTimestamp` is the existing helper used at `Pipeline.fs:193`. `freePath`, `stripFences`, `Similarity.cosine`, and `Naming.notePath` are already in scope.

- [ ] **Step 7: Wire `NoteMatch` config in the host**

In `src/Nameless.TaskList/Program.fs` `buildDeps` (line ~60-65), add the two fields read from config (mirroring `TopK`/`SimilarityFloor`):

```fsharp
        let buildDeps (messages: IMessageSource) (vault: IVault) (chat: IChatClient)
                      (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) : PipelineDeps =
            let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
            let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let noteTopK = match System.Int32.TryParse(cfg.["NoteMatch:TopK"]) with | true, v -> v | _ -> 5
            let noteFloor = match System.Double.TryParse(cfg.["NoteMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            { Messages = messages; Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
              Embedder = embedder; TopK = topK; SimilarityFloor = floor
              NoteTopK = noteTopK; NoteSimilarityFloor = noteFloor
              Vision = vision; Transcriber = transcriber }
```

In `src/Nameless.TaskList/appsettings.json`, add the `NoteMatch` section next to `TopicMatch` (the `TopicMatch:SimilarityFloor` change to 0.35 is Task 3):

```json
  "NoteMatch": { "TopK": 5, "SimilarityFloor": 0.35 },
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass, including the two new note tests and Task 1's person tests.

- [ ] **Step 9: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json tests/Nameless.TaskList.Core.Tests/PipelineTests.fs tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs
git commit -m "feat: durable-reference notes with match-and-merge

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 3: Tighten topic matching

Rebalance the topic-match prompt toward matching and lower the auto-create floor. Prompt + config only; no code-path change.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs:68-88` (`topicMatchSystem`)
- Modify: `src/Nameless.TaskList/appsettings.json` (`TopicMatch:SimilarityFloor` 0.5 → 0.35)
- Modify: `src/Nameless.TaskList/Program.fs` (the `TopicMatch:SimilarityFloor` fallback default — already set to 0.35 in Task 2 Step 7; if not, set it here)

**Interfaces:**
- Consumes: nothing new. Behavioral tuning only; the topic-match code path and `parseTopicMatch` are unchanged.

- [ ] **Step 1: Rewrite `topicMatchSystem`**

Replace `topicMatchSystem` (`src/Nameless.TaskList.Core/Prompts.fs:68-88`) with:

```fsharp
    let topicMatchSystem = """You are a knowledge base assistant. Your job is to decide whether an incoming message
belongs to an existing open topic, or whether it represents a new topic.

You will be given the extracted intent of the new message. You may call the get_topics tool
to list active topics and get_topic to read one in full.

Respond ONLY with a JSON object:

{
  "match": true/false,
  "topic_slug": "slug of matched topic, or null if no match",
  "confidence": 0.0,
  "match_reason": "brief explanation of why this matches or why no match was found",
  "new_topic_title": "if match is false, suggest a concise title for the new topic, else null"
}

Rules:
- Prefer matching an existing topic when the message concerns the same subject, incident, event, person, or thread — including follow-ups, status updates, corrections, and related questions.
- A follow-up about the same incident (e.g. another update on the same gate fault, or a new detail about the same trip) is the SAME topic, even if the wording differs.
- Only create a new topic when the message clearly introduces a distinct subject not covered by any candidate.
- A confidence below 0.6 should result in match: false.
- Do not add explanation outside the JSON object.

Examples:
- A topic "13th Street gate fault" and a message "the gate motor is slow again" -> same topic.
- A topic "Ethan birthday party" and a message about "the party cake order" -> same topic.
- A topic "school fees" and a message about "school sports day" -> different topics."""
```

- [ ] **Step 2: Lower the topic similarity floor (config + host default)**

In `src/Nameless.TaskList/appsettings.json`, change `TopicMatch`:

```json
  "TopicMatch": { "TopK": 5, "SimilarityFloor": 0.35 },
```

Confirm the host fallback default in `src/Nameless.TaskList/Program.fs` `buildDeps` reads `| _ -> 0.35` for `TopicMatch:SimilarityFloor` (set in Task 2 Step 7; if an earlier value remains, change it to `0.35`).

- [ ] **Step 3: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests pass. No new unit test is added here — the topic-match code path and `parseTopicMatch` are unchanged, and tests inject `SimilarityFloor` explicitly via `PipelineDeps`, so they are unaffected by the appsettings default. The prompt rebalance is LLM-judgment, validated empirically by the clean re-run (fewer fragmented topics), per the spec.

- [ ] **Step 4: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList/appsettings.json src/Nameless.TaskList/Program.fs
git commit -m "feat: tighten topic matching to consolidate related messages

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

## Self-Review

**Spec coverage:**
- A1 tighten note extraction → Task 2 Step 4. ✓
- A1 tighten `noteCreateSystem` → Task 2 Step 4. ✓
- A2 note match-vs-update (list notes, embedding shortlist, LLM confirm reusing `TopicMatch`/`parseTopicMatch`, merge via `noteUpdateSystem`, refresh `last_verified`/union `people_linked`+`context`, keep `source`/`tags`) → Task 2 Steps 5-6. ✓
- B1 lower `TopicMatch:SimilarityFloor` 0.5→0.35 → Task 3 Step 2. ✓
- B2 rebalance topic prompt, confidence 0.75→0.6, examples → Task 3 Step 1. ✓
- C1 add `aliases` to Person + DESIGN → Task 1 Step 1 (DESIGN doc text addition noted below). ✓
- C2 deterministic name+alias index resolution → Task 1 Step 6 (`peopleIndex`/`resolvePerson`). ✓
- C3 self-grow alias on canonical-title-slug match; else create by canonical slug → Task 1 Step 6. ✓
- C4 role-driven context prompt + message-context fallback (not hard-coded family) → Task 1 Steps 5-6. ✓
- Config table (`NoteMatch:TopK`/`SimilarityFloor` new; `TopicMatch:SimilarityFloor` changed) → Task 2 Step 7, Task 3 Step 2. ✓
- Testing (note match→merge, note no-match→create, person alias resolution, person self-grow, role context) → Task 1 Step 3, Task 2 Step 2. ✓
- Out of scope (tasks/events/commitments dedup, fuzzy person matching, retroactive consolidation) → not implemented. ✓

> Note on DESIGN.md: the spec calls for adding `aliases` to the §4.10-area Person schema example. This is a docs touch; fold it into Task 1 (add `aliases: [mom, mum]` to the Person YAML example in `docs/DESIGN.md` if a Person example is present near §4.10) — non-blocking for the build.

**Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows full code or an exact, uniform edit (the `PipelineDeps` field addition lists every line number and the identical two-field addition). ✓

**Type consistency:** `Person.Aliases: string array`, `PipelineDeps.NoteTopK: int`/`NoteSimilarityFloor: float`, `peopleIndex : IVault -> (string * string list) list`, `resolvePerson : (string*string list) list -> string -> string option`, `processNote : string -> string`, `Prompts.noteUpdateUser : string -> string -> string -> string` are used consistently across tasks and tests. Note flow reuses `TopicMatch`/`parseTopicMatch` per the global constraint. ✓
