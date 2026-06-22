# Remaining Entity Writers (Increment A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write the Event, Commitment, and Note files the classifier already extracts (but the spine drops), plus Person-stub files for mentioned-but-unknown people, by extending the existing per-message pipeline.

**Architecture:** Add four `[<CLIMutable>]` domain records + naming helpers + four prompt constants. Refactor the spine's bespoke task-creation loop into a shared, generic `writeEntities` helper (validate → deterministic fallback → collision-guard → write), then drive Events/Commitments/Notes through it and add a parallel `writePersonStubs` flow. All writes stay in `Pipeline.fs`; the model only calls read-only tools. No new ports, adapters, config, or web-host changes.

**Tech Stack:** F# / .NET 10, Markdig + YamlDotNet (markdown/YAML), System.Text.Json, xUnit.

## Global Constraints

- Target framework `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-22-entity-writers-design.md`. KB conventions authoritative in `docs/DESIGN.md` (§4 schemas, §7 prompts, §8 naming).
- Writes are create / overwrite-body only — **never delete** vault files; **never overwrite** an existing `people/` file.
- The model only ever calls read-only tools. All file writes happen in `Pipeline.fs`.
- Idempotency is already handled by the spine's top-of-pipeline message-file-existence guard — do not add new idempotency logic; the new writes inherit it.
- **No new ports, adapters, configuration, or web-host changes.** `PipelineResult.Processed` stays `(topic: string * tasks: string list)` — do not change it (that would ripple into the web host, which is out of scope). New entities are verified via vault-file assertions, not the result value.
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope: `Channel.last_processed`, index regeneration, digests, DESIGN §9 enhancements, live-service integration tests.

The existing records (for reference — you will extend the first two):
- `Message = { Type; Channel; Timestamp; Sender; Noise; Topic; SpawnedTasks: string[]; ProcessedBy }`
- `Topic = { Type; Title; Status; Context: string[]; Channel; People: string[]; FirstSeen; LastUpdated; SpawnedTasks: string[]; MessageRefs: string[] }`
- `Task = { Type; Title; Status; Priority; Due; Context: string[]; People: string[]; Topic; SourceMessage }`

---

### Task 1: Domain records, record extensions, and naming helpers

**Files:**
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs` (add records after `Message` at line 116; extend `Message`/`Topic`; add `Naming` helpers at end of the `Naming` module)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (update the three record literals so the build stays green)
- Modify: `tests/Nameless.TaskList.Core.Tests/CodecTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`

**Interfaces:**
- Consumes: existing `Frontmatter`, `MarkdownFile`, `Naming.slug`.
- Produces:
  - Records `Event`, `Commitment`, `Note`, `Person` (exact fields in Step 3).
  - `Message` gains `SpawnedEvents: string array` and `SpawnedNotes: string array`; `Topic` gains `SpawnedEvents: string array`.
  - `Naming.eventPath : System.DateTime -> string -> string`, `Naming.commitmentPath : string -> string`, `Naming.notePath : string -> string`, `Naming.personPath : string -> string -> string`.

- [ ] **Step 1: Write the failing naming tests**

Append to `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`:

```fsharp
[<Fact>]
let ``eventPath is date-pathed by year and month`` () =
    let w = DateTime(2026, 7, 19, 14, 0, 0, DateTimeKind.Utc)
    Assert.Equal("events/2026/07/ethans-birthday-party-2026-07-19.md", Naming.eventPath w "Ethan's birthday party")

[<Fact>]
let ``commitmentPath slugs under commitments`` () =
    Assert.Equal("commitments/pay-school-fees.md", Naming.commitmentPath "Pay school fees")

[<Fact>]
let ``notePath slugs under notes`` () =
    Assert.Equal("notes/ethan-allergies.md", Naming.notePath "Ethan allergies")

[<Fact>]
let ``personPath nests under people and context`` () =
    Assert.Equal("people/medical/dr-naidoo.md", Naming.personPath "medical" "dr-naidoo")
```

- [ ] **Step 2: Write the failing codec tests**

Append to `tests/Nameless.TaskList.Core.Tests/CodecTests.fs`:

```fsharp
[<Fact>]
let ``Event round-trips through serialize and FromString`` () =
    let original : Event =
        { Type = "Event"; Title = "Sports day"; When = "2026-06-20T09:00:00+02:00"; AllDay = false
          Context = [| "school" |]; Location = "Field"; People = [| "ethan" |]
          Topic = "topics/active/x.md"; TasksLinked = [| "tasks/pending/y.md" |]; ReminderDaysBefore = 3 }
    let file = MarkdownFile.ToString (Frontmatter.serialize original) "Body."
    let back = Frontmatter.deserialize<Event> (MarkdownFile.FromString file).FrontMatter.Value
    Assert.Equal(original.Title, back.Title)
    Assert.Equal(original.When, back.When)
    Assert.Equal<string array>(original.Context, back.Context)

[<Fact>]
let ``Commitment and Note and Person round-trip`` () =
    let c : Commitment =
        { Type = "Commitment"; Title = "Q3 fees"; Status = "unresolved"; Priority = "high"; Due = "2026-07-01"
          Context = [| "finance" |]; Topic = ""; TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = "messages/x/y.md" }
    let cb = Frontmatter.deserialize<Commitment> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize c) "b")).FrontMatter.Value
    Assert.Equal(7, cb.EscalateAfterDays)

    let n : Note =
        { Type = "Note"; Title = "Allergy"; Context = [| "medical" |]; PeopleLinked = [| "ethan" |]
          Tags = [| "allergy" |]; Source = "messages/x/y.md"; LastVerified = "" }
    let nb = Frontmatter.deserialize<Note> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize n) "b")).FrontMatter.Value
    Assert.Equal<string array>([| "allergy" |], nb.Tags)

    let p : Person =
        { Type = "Person"; Title = "Dr Naidoo"; Role = "Paediatrician"; Context = [| "medical" |]
          Channel = ""; Phone = ""; Email = ""; Tags = [| "doctor" |] }
    let pb = Frontmatter.deserialize<Person> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize p) "b")).FrontMatter.Value
    Assert.Equal("Paediatrician", pb.Role)

[<Fact>]
let ``Message carries spawned events and notes`` () =
    let m : Message =
        { Type = "Message"; Channel = "wife"; Timestamp = "2026-06-15T14:17:45+02:00"; Sender = "Wife"
          Noise = false; Topic = "topics/active/x.md"; SpawnedTasks = [| "t" |]
          SpawnedEvents = [| "e" |]; SpawnedNotes = [| "n" |]; ProcessedBy = "m" }
    let back = Frontmatter.deserialize<Message> (MarkdownFile.FromString (MarkdownFile.ToString (Frontmatter.serialize m) "b")).FrontMatter.Value
    Assert.Equal<string array>([| "e" |], back.SpawnedEvents)
    Assert.Equal<string array>([| "n" |], back.SpawnedNotes)
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests|FullyQualifiedName~CodecTests"`
Expected: FAIL — `Event`/`Commitment`/`Note`/`Person` not defined; `Naming.eventPath` etc. not defined; `Message`/`Topic` missing fields.

- [ ] **Step 4: Add the new records and extend Message/Topic**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, extend the `Topic` record (add `SpawnedEvents` after `SpawnedTasks`) so lines 104-105 become:

```fsharp
      SpawnedTasks: string array
      SpawnedEvents: string array
      MessageRefs: string array }
```

Extend the `Message` record (add two fields after `SpawnedTasks`) so it reads:

```fsharp
      SpawnedTasks: string array
      SpawnedEvents: string array
      SpawnedNotes: string array
      ProcessedBy: string }
```

Immediately after the `Message` record (before `module Frontmatter`), add:

```fsharp
[<CLIMutable>]
type Event =
    { Type: string
      Title: string
      When: string
      AllDay: bool
      Context: string array
      Location: string
      People: string array
      Topic: string
      TasksLinked: string array
      ReminderDaysBefore: int }

[<CLIMutable>]
type Commitment =
    { Type: string
      Title: string
      Status: string
      Priority: string
      Due: string
      Context: string array
      Topic: string
      TaskAssigned: string
      EscalateAfterDays: int
      SourceMessage: string }

[<CLIMutable>]
type Note =
    { Type: string
      Title: string
      Context: string array
      PeopleLinked: string array
      Tags: string array
      Source: string
      LastVerified: string }

[<CLIMutable>]
type Person =
    { Type: string
      Title: string
      Role: string
      Context: string array
      Channel: string
      Phone: string
      Email: string
      Tags: string array }
```

- [ ] **Step 5: Add the naming helpers**

At the end of the `Naming` module in `KnowledgeBase.fs` (after `topicPath`), add:

```fsharp
    let eventPath (whenTs: System.DateTime) (title: string) : string =
        sprintf "events/%04d/%02d/%s-%04d-%02d-%02d.md"
            whenTs.Year whenTs.Month (slug title) whenTs.Year whenTs.Month whenTs.Day

    let commitmentPath (title: string) : string =
        sprintf "commitments/%s.md" (slug title)

    let notePath (title: string) : string =
        sprintf "notes/%s.md" (slug title)

    let personPath (context: string) (personSlug: string) : string =
        sprintf "people/%s/%s.md" context personSlug
```

- [ ] **Step 6: Update Pipeline.fs record literals to keep the build green**

In `src/Nameless.TaskList.Core/Pipeline.fs`, the noise-path `Message` literal (around line 45) must include the new fields:

```fsharp
                let record : Message =
                    { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                      Sender = msg.SenderName; Noise = true; Topic = ""
                      SpawnedTasks = [||]; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
```

The new-topic `Topic` literal (around line 67) must include `SpawnedEvents`:

```fsharp
                    let topicRecord : Topic =
                        { Type = "Topic"; Title = matchResult.NewTopicTitle; Status = "active"
                          Context = classification.Contexts; Channel = channelSlug
                          People = classification.PeopleMentioned
                          FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                          SpawnedTasks = [||]; SpawnedEvents = [||]; MessageRefs = [||] }
```

The signal-path `Message` literal (around line 145) must include the new fields (events/notes stay empty for now):

```fsharp
            let messageRecord : Message =
                { Type = "Message"; Channel = channelSlug; Timestamp = isoTimestamp msg.Timestamp
                  Sender = msg.SenderName; Noise = false; Topic = topicPath
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = [||]; SpawnedNotes = [||]; ProcessedBy = deps.Model }
```

The topic-update merge uses `{ t with ... }` and needs no change (it carries `t.SpawnedEvents` unchanged).

- [ ] **Step 7: Run all Core tests to verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS — new naming + codec tests pass; all existing tests still pass.

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core/KnowledgeBase.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add Event/Commitment/Note/Person records, extend Message/Topic, add naming"
```

---

### Task 2: Shared `writeEntities` helper (refactor the task step)

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs`

**Interfaces:**
- Consumes: `IVault`, `IChatClient`, `Agent.runConversation`, `Frontmatter`, `MarkdownFile`, the records from Task 1.
- Produces (module-level, `private` — used by Tasks 3-5):
  - `stripFences : string -> string`
  - `urgencyToPriority : string -> string`
  - `freePath : IVault -> string -> string` (inserts `-2`, `-3`, … before `.md` on collision)
  - `EntityOutcome<'T> = { Record: 'T; Body: string }`
  - `EntitySpec<'T> = { Prompt: string; BuildUser: string -> string; Interpret: string -> string -> EntityOutcome<'T>; BasePath: 'T -> string }`
  - `writeEntities : PipelineDeps -> EntitySpec<'T> -> string list -> string list`

This task is a **behavior-preserving refactor**: task creation must produce the same files at the same paths. Note one intentional change — a valid task reply's frontmatter is now re-serialized canonically from the parsed record (the model's *body* is preserved; extra/unknown frontmatter keys are dropped). If an existing assertion checks the task file's raw frontmatter verbatim, update it to assert parsed fields instead.

- [ ] **Step 1: Add module-level helpers and the writeEntities generic**

In `src/Nameless.TaskList.Core/Pipeline.fs`, immediately after `let private isoTimestamp ...` (line 21) and before `processMessage`, add:

```fsharp
    /// Strip surrounding code fences / leading prose from a model reply.
    let private stripFences (text: string) =
        let trimmed = (if isNull text then "" else text).Trim()
        let idx = trimmed.IndexOf("```")
        if idx >= 0 then
            let afterFirst = trimmed.IndexOf('\n', idx)
            let lastFence = trimmed.LastIndexOf("```")
            if afterFirst > 0 && lastFence > afterFirst then trimmed.[afterFirst..lastFence - 1].Trim()
            else trimmed
        else trimmed

    /// Map an urgency string to a priority value.
    let private urgencyToPriority (u: string) =
        match (if isNull u then "" else u).ToLowerInvariant() with
        | "critical" -> "critical"
        | "high" -> "high"
        | "low" -> "low"
        | _ -> "medium"

    /// Find a collision-free path by inserting -2, -3, ... before the ".md" extension.
    let private freePath (vault: IVault) (basePath: string) =
        if not (vault.Exists basePath) then basePath
        else
            let stem = if basePath.EndsWith(".md") then basePath.[.. basePath.Length - 4] else basePath
            let rec tryN n =
                let candidate = sprintf "%s-%d.md" stem n
                if not (vault.Exists candidate) then candidate else tryN (n + 1)
            tryN 2

    type private EntityOutcome<'T> = { Record: 'T; Body: string }

    type private EntitySpec<'T> =
        { Prompt: string
          BuildUser: string -> string
          Interpret: string -> string -> EntityOutcome<'T>   // stripped reply, intent -> outcome
          BasePath: 'T -> string }

    /// Run a per-type generation prompt for each intent: validate (or fall back),
    /// canonicalize, collision-guard the path, write, and return the written paths.
    let private writeEntities (deps: PipelineDeps) (spec: EntitySpec<'T>) (intents: string list) : string list =
        intents
        |> List.map (fun intent ->
            let raw = Agent.runConversation deps.Chat [] spec.Prompt (spec.BuildUser intent)
            let outcome = spec.Interpret (stripFences raw) intent
            let text = MarkdownFile.ToString (Frontmatter.serialize outcome.Record) outcome.Body
            let path = freePath deps.Vault (spec.BasePath outcome.Record)
            deps.Vault.Write(path, text)
            path)
```

- [ ] **Step 2: Replace the inline task block with a task EntitySpec**

In `processMessage`, delete the entire current task block — the inline `stripFences`, `urgencyToPriority`, `freeTaskPath` definitions and the `let taskPaths = classification.Entities.Tasks |> Array.toList |> List.map (...)` expression (the region from the `// --- Step: create one task file per task intent ---` comment through the end of the `taskPaths` binding). Replace it with:

```fsharp
            // --- Step: create task files via the shared entity writer ---
            let taskSpec : EntitySpec<Task> =
                { Prompt = Prompts.taskCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Message intent: %s\nRaw message: %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let t = Frontmatter.deserialize<Task> fm
                                if not (System.String.IsNullOrWhiteSpace t.Title) then { Record = t; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Task =
                                { Type = "Task"; Title = intent; Status = "pending"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; People = classification.PeopleMentioned
                                  Topic = topicPath; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun t -> Naming.taskPath t.Title) }

            let taskPaths = writeEntities deps taskSpec (List.ofArray classification.Entities.Tasks)
```

- [ ] **Step 3: Run the pipeline tests to verify behavior is preserved**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS. If the signal-path test fails because it asserted the task file's raw frontmatter verbatim, change that assertion to parse the file and check `Frontmatter.deserialize<Task>(...).Title = "Call Acrobranch"` (the body and path are unchanged). Re-run until green.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all green.

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "refactor: extract shared writeEntities helper from task step"
```

---

### Task 3: Event writer

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `eventCreateSystem`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (event spec + wire + Message/Topic links)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `writeEntities`, `EntitySpec`, `EntityOutcome`, `stripFences`, `Naming.eventPath`, the `Event` record.
- Produces: `eventPaths : string list` in `processMessage`; the signal-path `Message.SpawnedEvents` and the topic-update `Topic.SpawnedEvents` now carry them.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``signal message with an event writes a date-pathed event and links it`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["school"],"intent":"sports day","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":[],"events":["Ethan sports day on the 20th"],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Ethan sports day"}"""
    let eventFile = Responses.final "---\ntype: Event\ntitle: Ethan sports day\nwhen: 2026-06-20T09:00:00+02:00\nall_day: false\ncontext:\n  - school\n---\nAt the school field."
    let topicBody = Responses.final "## Current understanding\nSports day.\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; eventFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let eventPath = "events/2026/06/ethan-sports-day-2026-06-20.md"
        Assert.True(vault.Files.ContainsKey(eventPath))
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains(eventPath, vault.Files.[msgKey])
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``event with no date falls back to the message date and is flagged`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"party","action_required":true,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":["A party sometime"],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Party"}"""
    let eventFile = Responses.final "---\ntype: Event\ntitle: A party\nall_day: true\ncontext:\n  - family\n---\nNo date given."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; eventFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // sampleMessage timestamp is 2026-06-15
    let key = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("events/2026/06/a-party-2026-06-15"))
    Assert.Contains("inferred", vault.Files.[key])
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — no event file is written (events are not yet created).

- [ ] **Step 3: Add the event-creation prompt**

In `src/Nameless.TaskList.Core/Prompts.fs`, after `taskCreateSystem`, add:

```fsharp
    let eventCreateSystem = """You are creating an Event entry for a personal knowledge base.
Generate the YAML frontmatter and a brief body for a new event file.

Rules:
- title: short noun phrase naming the occurrence
- when: ISO 8601 datetime. The reference date of the source message is provided; resolve
  relative dates ("next Friday", "the 20th") against it. If only a date is known, use 00:00 and set all_day: true.
- all_day: true when no specific time was given, else false
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- location: a place name if mentioned, else ""
- people: array of person slugs relevant to the event (use [] if none)
- reminder_days_before: integer, default 3

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 4: Wire the event writer into the pipeline**

In `src/Nameless.TaskList.Core/Pipeline.fs`, immediately after the `let taskPaths = writeEntities ...` binding (from Task 2), add:

```fsharp
            // --- Step: create event files (date-pathed; undated events fall back to the message date) ---
            let parseWhen (s: string) (fallback: System.DateTime) =
                match System.DateTime.TryParse(s) with
                | true, dt -> dt
                | _ -> fallback

            let eventSpec : EntitySpec<Event> =
                { Prompt = Prompts.eventCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Event intent: %s\nRaw message: %s\nMessage reference date: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (isoTimestamp msg.Timestamp) (String.concat ", " classification.Contexts) messagePath)
                  Interpret =
                    (fun stripped intent ->
                        let flag = "\n\n_Date inferred from message; please confirm._"
                        let ensureDated (e: Event) (body: string) =
                            match System.DateTime.TryParse(e.When) with
                            | true, _ -> { Record = e; Body = body }
                            | _ -> { Record = { e with When = isoTimestamp msg.Timestamp }; Body = body + flag }
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let e = Frontmatter.deserialize<Event> fm
                                if not (System.String.IsNullOrWhiteSpace e.Title) then ensureDated e parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Event =
                                { Type = "Event"; Title = intent; When = isoTimestamp msg.Timestamp; AllDay = true
                                  Context = classification.Contexts; Location = ""; People = classification.PeopleMentioned
                                  Topic = topicPath; TasksLinked = Array.ofList taskPaths; ReminderDaysBefore = 3 }
                            { Record = fb; Body = intent + flag })
                  BasePath = (fun e -> Naming.eventPath (parseWhen e.When msg.Timestamp) e.Title) }

            let eventPaths = writeEntities deps eventSpec (List.ofArray classification.Entities.Events)
```

- [ ] **Step 5: Link events into the Message and Topic**

In the signal-path `Message` literal, change `SpawnedEvents = [||]` to `SpawnedEvents = Array.ofList eventPaths`:

```fsharp
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = Array.ofList eventPaths; SpawnedNotes = [||]; ProcessedBy = deps.Model }
```

In the topic-update merge (`{ t with ... }`), add the events to `SpawnedEvents`:

```fsharp
                    let merged =
                        { t with
                            LastUpdated = isoTimestamp msg.Timestamp
                            MessageRefs = Array.append t.MessageRefs [| messagePath |]
                            SpawnedTasks = Array.append t.SpawnedTasks (Array.ofList taskPaths)
                            SpawnedEvents = Array.append t.SpawnedEvents (Array.ofList eventPaths) }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS — both new event tests pass; existing pipeline tests still pass.

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all green.

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: write Event files from the classifier and link them to message/topic"
```

---

### Task 4: Commitment writer

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `commitmentCreateSystem`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (commitment spec + wire)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `writeEntities`, `EntitySpec`, `Naming.commitmentPath`, the `Commitment` record.
- Produces: `commitmentPaths : string list` in `processMessage` (written to the vault; not referenced by Message/Topic per DESIGN).

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``signal message with a commitment writes a commitment file`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["finance"],"intent":"fees due","action_required":true,"urgency":"high","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":["Q3 school fees due 1 July"],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"School fees"}"""
    let commitmentFile = Responses.final "---\ntype: Commitment\ntitle: Q3 school fees\nstatus: unresolved\npriority: high\ndue: 2026-07-01\ncontext:\n  - finance\nescalate_after_days: 7\n---\nPay by EFT."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; commitmentFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.True(vault.Files.ContainsKey("commitments/q3-school-fees.md"))
    Assert.Contains("unresolved", vault.Files.["commitments/q3-school-fees.md"])
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — no commitment file written.

- [ ] **Step 3: Add the commitment-creation prompt**

In `Prompts.fs`, after `eventCreateSystem`, add:

```fsharp
    let commitmentCreateSystem = """You are creating a Commitment entry for a personal knowledge base.
A commitment is an obligation that exists but does not yet have an assigned task.

Rules:
- title: short noun phrase naming the obligation
- status: always "unresolved" for a new commitment
- priority: infer from context and urgency — default "medium"
- due: ISO 8601 date if a deadline is known, else ""
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- task_assigned: null
- escalate_after_days: integer, default 7

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 4: Wire the commitment writer**

In `Pipeline.fs`, immediately after the `let eventPaths = writeEntities ...` binding (Task 3), add:

```fsharp
            // --- Step: create commitment files ---
            let commitmentSpec : EntitySpec<Commitment> =
                { Prompt = Prompts.commitmentCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Commitment intent: %s\nRaw message: %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) classification.Urgency messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let c = Frontmatter.deserialize<Commitment> fm
                                if not (System.String.IsNullOrWhiteSpace c.Title) then { Record = c; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Commitment =
                                { Type = "Commitment"; Title = intent; Status = "unresolved"
                                  Priority = urgencyToPriority classification.Urgency; Due = ""
                                  Context = classification.Contexts; Topic = topicPath
                                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = messagePath }
                            { Record = fb; Body = intent })
                  BasePath = (fun c -> Naming.commitmentPath c.Title) }

            let commitmentPaths = writeEntities deps commitmentSpec (List.ofArray classification.Entities.Commitments)
            ignore commitmentPaths
```

(`ignore commitmentPaths` documents that commitments are persisted but intentionally not linked into the Message/Topic per DESIGN §4.5.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all green.

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: write Commitment files from the classifier"
```

---

### Task 5: Note writer

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `noteCreateSystem`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (note spec + wire + Message link)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `writeEntities`, `EntitySpec`, `Naming.notePath`, the `Note` record.
- Produces: `notePaths : string list`; the signal-path `Message.SpawnedNotes` now carries them.

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``signal message with a note writes a note file and links it`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"allergy fact","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":["Ethan is allergic to penicillin"]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Ethan health"}"""
    let noteFile = Responses.final "---\ntype: Note\ntitle: Ethan penicillin allergy\ncontext:\n  - medical\ntags:\n  - allergy\n---\nConfirmed 2023."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; noteFile; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    let notePath = "notes/ethan-penicillin-allergy.md"
    Assert.True(vault.Files.ContainsKey(notePath))
    let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
    Assert.Contains(notePath, vault.Files.[msgKey])
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — no note file written.

- [ ] **Step 3: Add the note-creation prompt**

In `Prompts.fs`, after `commitmentCreateSystem`, add:

```fsharp
    let noteCreateSystem = """You are creating a Note entry for a personal knowledge base.
A note captures a fact or piece of reference information worth keeping.

Rules:
- title: short noun phrase naming the fact
- context: array — choose from [family, medical, school, finance, professional, personal-kb]
- tags: array of short lowercase tags (use [] if none)
- Body: 1–3 sentences capturing the fact, including any specifics (numbers, names, dates).

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 4: Wire the note writer and link into the Message**

In `Pipeline.fs`, immediately after the `ignore commitmentPaths` line (Task 4), add:

```fsharp
            // --- Step: create note files ---
            let noteSpec : EntitySpec<Note> =
                { Prompt = Prompts.noteCreateSystem
                  BuildUser =
                    (fun intent ->
                        sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                            intent msg.Content (String.concat ", " classification.Contexts) messagePath)
                  Interpret =
                    (fun stripped intent ->
                        try
                            let parsed = MarkdownFile.FromString stripped
                            match parsed.FrontMatter with
                            | Some fm ->
                                let n = Frontmatter.deserialize<Note> fm
                                if not (System.String.IsNullOrWhiteSpace n.Title) then { Record = n; Body = parsed.Content }
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            let fb : Note =
                                { Type = "Note"; Title = intent; Context = classification.Contexts
                                  PeopleLinked = classification.PeopleMentioned; Tags = [||]
                                  Source = messagePath; LastVerified = "" }
                            { Record = fb; Body = intent })
                  BasePath = (fun n -> Naming.notePath n.Title) }

            let notePaths = writeEntities deps noteSpec (List.ofArray classification.Entities.Notes)
```

In the signal-path `Message` literal, change `SpawnedNotes = [||]` to `SpawnedNotes = Array.ofList notePaths`:

```fsharp
                  SpawnedTasks = Array.ofList taskPaths; SpawnedEvents = Array.ofList eventPaths; SpawnedNotes = Array.ofList notePaths; ProcessedBy = deps.Model }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all green.

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: write Note files from the classifier and link them to the message"
```

---

### Task 6: Person-stub writer

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add `personStubSystem`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (existence check + `writePersonStubs` flow + wire)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`

**Interfaces:**
- Consumes: `IVault`, `Agent.runConversation`, `stripFences`, `Naming.slug`, `Naming.personPath`, the `Person` record.
- Produces: person-stub files under `people/{context}/{slug}.md` for mentioned people not already present.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``mentioned unknown person gets a stub`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"see doctor","action_required":true,"urgency":"medium","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Doctor visit"}"""
    let personStub = Responses.final "---\ntype: Person\ntitle: Dr Naidoo\nrole: Paediatrician\ncontext:\n  - medical\n---\nEthan's paediatrician. ⚠ Stub — details to be completed."
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; personStub; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.True(vault.Files.ContainsKey("people/medical/dr-naidoo.md"))

[<Fact>]
let ``existing person is not overwritten and no stub LLM call is made`` () =
    let vault = FakeVault()
    vault.Seed("people/medical/dr-naidoo.md", "---\ntype: Person\ntitle: Dr Naidoo\n---\nExisting.")
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["medical"],"intent":"see doctor","action_required":true,"urgency":"medium","people_mentioned":["Dr Naidoo"],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.2,"match_reason":"new","new_topic_title":"Doctor visit"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    // No personStub response scripted: if the pipeline tried to create one, the queue would underflow.
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    Assert.Equal("---\ntype: Person\ntitle: Dr Naidoo\n---\nExisting.", vault.Files.["people/medical/dr-naidoo.md"])
    Assert.Equal(3, chat.Calls)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — first test: no `people/medical/dr-naidoo.md` written. (The second test may currently pass trivially; both must pass after Step 4.)

- [ ] **Step 3: Add the person-stub prompt**

In `Prompts.fs`, after `noteCreateSystem`, add (verbatim from DESIGN §7.5):

```fsharp
    let personStubSystem = """You are creating a person stub entry for a personal knowledge base.
A new person has been mentioned in a message. Create a minimal person file
based on the available information.

Rules:
- title: full name if known, role if name not known (e.g. "Ethan's Class Teacher")
- role: their relationship to the KB owner or their professional role
- context: infer from the message context — choose from [family, medical, school, finance, professional]
- All unknown fields should be null or omitted
- Body: 1 sentence describing who this person is and how they relate to the KB owner.
  End with: "⚠ Stub — details to be completed."

Respond ONLY with a complete markdown file (frontmatter between --- fences, then body). No explanation."""
```

- [ ] **Step 4: Add the existence check and person-stub flow, and wire it in**

In `Pipeline.fs`, add module-level helpers after `freePath` (from Task 2):

```fsharp
    let private knownContexts = [ "family"; "medical"; "school"; "finance"; "professional" ]

    /// True if a person file for this slug exists under any known context directory.
    let private personExists (vault: IVault) (personSlug: string) =
        knownContexts |> List.exists (fun ctx -> vault.Exists(Naming.personPath ctx personSlug))
```

Then, in `processMessage`, immediately after the `let notePaths = writeEntities ...` binding (Task 5) and before the `// --- Step: write the message record ...` block, add:

```fsharp
            // --- Step: create Person-stub files for mentioned people not already in the vault ---
            classification.PeopleMentioned
            |> Array.toList
            |> List.iter (fun name ->
                let personSlug = Naming.slug name
                if not (System.String.IsNullOrWhiteSpace personSlug) && not (personExists deps.Vault personSlug) then
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
                                if not (System.String.IsNullOrWhiteSpace p.Title) then p, parsed.Content
                                else raise (System.Exception("empty title"))
                            | None -> raise (System.Exception("no frontmatter"))
                        with _ ->
                            { Type = "Person"; Title = name; Role = ""; Context = [| "family" |]
                              Channel = ""; Phone = ""; Email = ""; Tags = [||] },
                            sprintf "%s\n\n⚠ Stub — details to be completed." name
                    let ctx =
                        if not (isNull record.Context) && record.Context.Length > 0
                           && not (System.String.IsNullOrWhiteSpace record.Context.[0])
                        then record.Context.[0] else "family"
                    deps.Vault.Write(Naming.personPath ctx personSlug,
                                     MarkdownFile.ToString (Frontmatter.serialize record) body))
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS — both person tests pass; existing pipeline tests still pass.

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS, all green.

```bash
git add src/Nameless.TaskList.Core/Prompts.fs src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: create Person-stub files for mentioned-but-unknown people"
```

---

### Task 7: Whole-solution verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Run all tests across both test projects**

Run: `dotnet test`
Expected: all pass (Core tests grown by Tasks 1, 3, 4, 5, 6; endpoint tests unchanged).

- [ ] **Step 3: Confirm no out-of-scope changes**

Run: `git diff --stat main..HEAD`
Expected: changes only under `src/Nameless.TaskList.Core/`, `tests/Nameless.TaskList.Core.Tests/`, and `docs/`. **No** changes under `src/Nameless.TaskList/` (the web host) — `PipelineResult` and the endpoint are unchanged.

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Task 1 covers spec §3 (records + extensions) and §4 (naming). Task 2 covers §6.1 (shared helper). Tasks 3-5 cover §3/§5/§6.2 (Event/Commitment/Note writers + the §6.4 undated-event fallback in Task 3). Task 6 covers §6.3 (person-stub flow) and §5 (`personStubSystem`). §7 (idempotency/collisions/deletes) is inherited (no new logic) and exercised by the collision guard in `writeEntities` + the person existence guard. §8 (testing) is distributed across the tasks. Task 7 is the §9 "no web-host changes" guard.
- **Ordering invariant:** in `processMessage` the bindings must appear in this order so later steps can reference earlier results: `taskPaths` → `eventPaths` → `commitmentPaths` (+`ignore`) → `notePaths` → person-stub loop → message write → topic update. Events reference `taskPaths` (in `TasksLinked`); the message references tasks/events/notes; the topic update references tasks/events.
- **Type consistency:** `EntitySpec<'T>`/`EntityOutcome<'T>`/`writeEntities` are defined once (Task 2) and reused verbatim by Tasks 3-5. The fallback records use exactly the Task-1 field names.
- **Known intentional behavior change:** Task 2 re-serializes entity frontmatter canonically from the parsed record (preserving the body). If any pre-existing test asserted raw task frontmatter, it is updated in Task 2 Step 3.
- **Deferred (do NOT build here):** `Channel.last_processed`, index regeneration, digests, DESIGN §9 items, live integration tests, and any change to `PipelineResult`/the web host.
```
