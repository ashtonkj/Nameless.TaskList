# Prompt Evaluation System — Phase 2 Implementation Plan (generative creators)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the eval to the four generative creators (task / event / commitment / note) by extracting their generation core into shared `Steps.create*` functions and adding deterministic per-type field-assertion scorers, dataset, and runner wiring.

**Architecture:** Mirror Phase 1's faithfulness move. Relocate the shared generation helpers + extract each creator's "build prompt → run model → interpret + fallback → record+body" into `Steps.fs` (returning the existing `EntityOutcome<'T>`), with all linkage passed in as parameters and all file-writing / match-and-merge machinery left in `Pipeline.fs` (behaviour-preserving). The eval calls the same `Steps.create*` with neutral stub linkage and scores only the model-generated fields.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2 (+ Xunit.SkippableFact), System.Text.Json, the existing `Nameless.TaskList.Eval` console project and `Nameless.TaskList.Core` Steps/Scoring/Runner modules.

## Global Constraints

- Target framework `net10.0` (every project).
- The Steps extraction MUST be **behaviour-preserving** for `Pipeline.processMessage` — the full `dotnet test` suite stays green with NO test-logic changes (new StepsTests/eval tests are additive).
- Deterministic scoring only — no LLM-as-judge.
- The eval (`eval/Nameless.TaskList.Eval`) stays a console project, NOT a default `dotnet test` project; `dotnet build` still compiles it.
- `Steps.create*` returns the **full record** (linkage set from its parameters); the eval passes **neutral stub linkage** (empty topic/message paths, empty people/taskPaths) and the scorer asserts ONLY model-generated fields — never Topic/SourceMessage/TasksLinked/People(linkage).
- Make every edit to a covered file through the Verevoir MCP `write_file`/`edit_file` (built-in Edit is an acceptable fallback ONLY for a `.fsproj` when MCP rejects the XML escaping — note it in the report). Run builds/tests via Bash.
- No real PII in any committed dataset/world file (anonymised stand-ins only).
- Person-stub generation and all person/match/relationship steps are OUT OF SCOPE (Phase 3).

---

## Task 1: Relocate shared generation helpers into Steps

Pure relocation that de-risks Task 2. Move four helpers out of `Pipeline.fs` into `Steps.fs` and point Pipeline at them, plus add the `GenInput` record Tasks 2–3 consume. No behaviour change.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add the moved helpers + `GenInput`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (delete the moved private copies / local closure; reference `Steps.*`)

**Interfaces:**
- Produces (all in `module Steps`):
  - `type EntityOutcome<'T> = { Record: 'T; Body: string }` (public)
  - `Steps.stripFences : string -> string`
  - `Steps.slugifyPeople : string array -> string array`
  - `Steps.urgencyToPriority : string -> string`
  - `type GenInput = { Intent: string; Raw: string; ReferenceDate: string; Contexts: string array; Urgency: string; TopicPath: string; MessagePath: string; PeopleSlugs: string array; TaskPaths: string list }`

- [ ] **Step 1: Add the moved helpers + GenInput to `Steps.fs`**

In `src/Nameless.TaskList.Core/Steps.fs`, inside `module Steps`, add near the top (after the `open` lines, before `classify`). These are copied verbatim from their current `Pipeline.fs` definitions:

```fsharp
    /// Strip surrounding code fences / leading prose from a model reply.
    let stripFences (text: string) =
        let trimmed = (if isNull text then "" else text).Trim()
        let idx = trimmed.IndexOf("```")
        if idx >= 0 then
            let afterFirst = trimmed.IndexOf('\n', idx)
            let lastFence = trimmed.LastIndexOf("```")
            if afterFirst > 0 && lastFence > afterFirst then trimmed.[afterFirst..lastFence - 1].Trim()
            else trimmed
        else trimmed

    /// Map an urgency string to a priority value.
    let urgencyToPriority (u: string) =
        match (if isNull u then "" else u).ToLowerInvariant() with
        | "critical" -> "critical"
        | "high" -> "high"
        | "low" -> "low"
        | _ -> "medium"

    /// Canonicalize a people array to distinct non-empty slugs (matching person filenames).
    let slugifyPeople (a: string array) =
        if isNull a then [||]
        else a |> Array.map Naming.slug |> Array.filter (fun s -> s <> "") |> Array.distinct

    /// The outcome of a generation step: the parsed record plus its markdown body.
    type EntityOutcome<'T> = { Record: 'T; Body: string }

    /// Inputs a generative creator needs: the model-facing fields (intent, raw message,
    /// pre-formatted reference timestamp, contexts, urgency) plus the pipeline-owned linkage
    /// the creator stamps onto the record (topic/message paths, people slugs, linked task paths).
    /// The eval passes neutral stubs for the linkage fields.
    type GenInput =
        { Intent: string
          Raw: string
          ReferenceDate: string
          Contexts: string array
          Urgency: string
          TopicPath: string
          MessagePath: string
          PeopleSlugs: string array
          TaskPaths: string list }
```

- [ ] **Step 2: Delete the now-moved helpers from Pipeline and the local `slugifyPeople`**

In `src/Nameless.TaskList.Core/Pipeline.fs`:
- Delete the private `stripFences` function (currently ~lines 111-120).
- Delete the private `urgencyToPriority` function (currently ~lines 122-128).
- Delete the private `EntityOutcome<'T>` type (currently line 185): `type private EntityOutcome<'T> = { Record: 'T; Body: string }`.
- Delete the local `slugifyPeople` closure inside `processMessage` (the `let slugifyPeople (a: string array) = ...` block, ~lines 399-401) and, on the line that builds `peopleSlugs`, change it to use the Steps function:

```fsharp
            let peopleSlugs = Steps.slugifyPeople classification.PeopleMentioned
```

- [ ] **Step 3: Point Pipeline's references at `Steps.*`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, update the remaining references (the EntitySpec machinery still in place):
- `EntitySpec<'T>`'s `Interpret` field type and `writeEntities` now use `Steps.EntityOutcome<'T>`:

```fsharp
    type private EntitySpec<'T> =
        { Prompt: string
          BuildUser: string -> string
          Interpret: string -> string -> Steps.EntityOutcome<'T>   // stripped reply, intent -> outcome
          BasePath: 'T -> string
          TitleOf: 'T -> string }
```

- In `writeEntities`, change `let outcome = spec.Interpret (stripFences raw) intent` to:

```fsharp
            let outcome = spec.Interpret (Steps.stripFences raw) intent
```

- Replace every remaining `stripFences ` call in Pipeline with `Steps.stripFences ` (the task-update, note-update, person-stub, and topic-update sites). Replace every remaining `urgencyToPriority ` with `Steps.urgencyToPriority ` (the task and commitment fallbacks), and every remaining `slugifyPeople ` with `Steps.slugifyPeople ` (the task and event `Interpret` closures).

- [ ] **Step 4: Build and verify no stale references remain**

Run: `dotnet build 2>&1 | tail -3`
Expected: Build succeeded, 0 warnings.

Run: `grep -nE "let private stripFences|let private urgencyToPriority|type private EntityOutcome|let slugifyPeople " src/Nameless.TaskList.Core/Pipeline.fs`
Expected: no output (all moved out).

Run: `grep -nE "[^.]\bstripFences\b|[^.]\burgencyToPriority\b|[^.]\bslugifyPeople\b" src/Nameless.TaskList.Core/Pipeline.fs`
Expected: every hit is prefixed with `Steps.` (no bare calls remain).

- [ ] **Step 5: Run the full suite (behaviour-preserving)**

Run: `dotnet test`
Expected: PASS — the full suite (Core.Tests + Tests) stays green; 293 tests, unchanged behaviour.

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs
git commit -m "refactor: relocate generation helpers (stripFences/slugifyPeople/urgencyToPriority/EntityOutcome) into Steps"
```

---

## Task 2: Extract `Steps.createTask` / `createEvent` / `createCommitment`

Move the generation core of the three `EntitySpec` creators into `Steps`, and refactor `EntitySpec` so its single `Generate` field obtains the outcome from `Steps.create*`. Behaviour-preserving.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `createTask`, `createEvent`, `createCommitment`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (refactor `EntitySpec` + `writeEntities`; rewire the three specs)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add creator tests)

**Interfaces:**
- Consumes: `Steps.GenInput`, `Steps.EntityOutcome`, `Steps.stripFences`, `Steps.slugifyPeople`, `Steps.urgencyToPriority` (Task 1).
- Produces:
  - `Steps.createTask : IChatClient -> GenInput -> EntityOutcome<Task>`
  - `Steps.createEvent : IChatClient -> GenInput -> EntityOutcome<Event>`
  - `Steps.createCommitment : IChatClient -> GenInput -> EntityOutcome<Commitment>`

- [ ] **Step 1: Add the three create functions to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs` (inside `module Steps`, after `matchTopic`). The bodies reproduce the current `taskSpec`/`eventSpec`/`commitmentSpec` `Prompt`+`BuildUser`+`Interpret` exactly, substituting `input.*` for the former captured variables:

```fsharp
    /// Generate a Task record+body from one intent. Mirrors the former Pipeline taskSpec
    /// (prompt + user message + parse/fallback). Linkage (Topic/SourceMessage/People) is set
    /// from `input`; the model only supplies title/description/status/priority/due/context/body.
    let createTask (chat: IChatClient) (input: GenInput) : EntityOutcome<Task> =
        let user =
            sprintf "Message intent: %s\nRaw message: %s\nMessage reference date (resolve relative dates like \"tomorrow\" against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.Urgency input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.taskCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let t = Frontmatter.deserialize<Task> fm
                if not (System.String.IsNullOrWhiteSpace t.Title) then
                    { Record = { t with Type = "Task"; Description = (if System.String.IsNullOrWhiteSpace t.Description then input.Intent else t.Description); Topic = input.TopicPath; SourceMessage = input.MessagePath; People = slugifyPeople t.People }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Task =
                { Type = "Task"; Title = input.Intent; Description = input.Intent; Status = "pending"
                  Priority = urgencyToPriority input.Urgency; Due = ""
                  Context = input.Contexts; People = input.PeopleSlugs
                  Topic = input.TopicPath; SourceMessage = input.MessagePath }
            { Record = fb; Body = input.Intent }

    /// Generate an Event record+body from one intent. Mirrors the former Pipeline eventSpec,
    /// including the ensureDated path: an unparseable `when` falls back to the reference date
    /// and appends the "date inferred" body flag.
    let createEvent (chat: IChatClient) (input: GenInput) : EntityOutcome<Event> =
        let user =
            sprintf "Event intent: %s\nRaw message: %s\nMessage reference date: %s\nContext(s): %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.eventCreateSystem user)
        let flag = "\n\n_Date inferred from message; please confirm._"
        let ensureDated (e: Event) (body: string) =
            match System.DateTimeOffset.TryParse(e.When) with
            | true, _ -> { Record = e; Body = body }
            | _ -> { Record = { e with When = input.ReferenceDate }; Body = body + flag }
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let e = Frontmatter.deserialize<Event> fm
                if not (System.String.IsNullOrWhiteSpace e.Title) then
                    ensureDated { e with Type = "Event"; Description = (if System.String.IsNullOrWhiteSpace e.Description then input.Intent else e.Description); Topic = input.TopicPath; TasksLinked = Array.ofList input.TaskPaths; People = slugifyPeople e.People } parsed.Content
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Event =
                { Type = "Event"; Title = input.Intent; Description = input.Intent; When = input.ReferenceDate; AllDay = true
                  Context = input.Contexts; Location = ""; People = input.PeopleSlugs
                  Topic = input.TopicPath; TasksLinked = Array.ofList input.TaskPaths; ReminderDaysBefore = 3 }
            { Record = fb; Body = input.Intent + flag }

    /// Generate a Commitment record+body from one intent. Mirrors the former Pipeline commitmentSpec.
    let createCommitment (chat: IChatClient) (input: GenInput) : EntityOutcome<Commitment> =
        let user =
            sprintf "Commitment intent: %s\nRaw message: %s\nReference date (resolve relative dates against this): %s\nContext(s): %s\nUrgency: %s\nSource message file: %s"
                input.Intent input.Raw input.ReferenceDate (String.concat ", " input.Contexts) input.Urgency input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.commitmentCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let c = Frontmatter.deserialize<Commitment> fm
                if not (System.String.IsNullOrWhiteSpace c.Title) then
                    { Record = { c with Type = "Commitment"; Description = (if System.String.IsNullOrWhiteSpace c.Description then input.Intent else c.Description); Topic = input.TopicPath; SourceMessage = input.MessagePath }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            let fb : Commitment =
                { Type = "Commitment"; Title = input.Intent; Description = input.Intent; Status = "unresolved"
                  Priority = urgencyToPriority input.Urgency; Due = ""
                  Context = input.Contexts; Topic = input.TopicPath
                  TaskAssigned = ""; EscalateAfterDays = 7; SourceMessage = input.MessagePath }
            { Record = fb; Body = input.Intent }
```

- [ ] **Step 2: Refactor `EntitySpec` + `writeEntities` to a single `Generate`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the `EntitySpec` type (from Task 1's updated shape) with:

```fsharp
    type private EntitySpec<'T> =
        { Generate: string -> Steps.EntityOutcome<'T>   // intent -> outcome
          BasePath: 'T -> string
          TitleOf: 'T -> string }
```

In `writeEntities`, replace the first two lines of the per-intent map (the `let raw = ...` and `let outcome = spec.Interpret ...`) with:

```fsharp
        |> List.map (fun intent ->
            let outcome = spec.Generate intent
            let text = MarkdownFile.ToString (Frontmatter.serialize outcome.Record) outcome.Body
```

(The rest of `writeEntities` — `basePath`, `newSlug`, collision logic, write — is unchanged.)

- [ ] **Step 3: Rewire the three specs to call `Steps.create*`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the `taskSpec` binding with:

```fsharp
            let taskSpec : EntitySpec<Task> =
                { Generate =
                    (fun intent ->
                        Steps.createTask deps.Chat
                            { Intent = intent; Raw = msg.Content; ReferenceDate = isoTimestamp msg.Timestamp
                              Contexts = classification.Contexts; Urgency = classification.Urgency
                              TopicPath = topicPath; MessagePath = messagePath
                              PeopleSlugs = peopleSlugs; TaskPaths = [] })
                  BasePath = (fun t -> Naming.taskPath t.Title)
                  TitleOf = (fun t -> t.Title) }
```

Replace the `eventSpec` binding with (note `TaskPaths = taskPaths`):

```fsharp
            let eventSpec : EntitySpec<Event> =
                { Generate =
                    (fun intent ->
                        Steps.createEvent deps.Chat
                            { Intent = intent; Raw = msg.Content; ReferenceDate = isoTimestamp msg.Timestamp
                              Contexts = classification.Contexts; Urgency = classification.Urgency
                              TopicPath = topicPath; MessagePath = messagePath
                              PeopleSlugs = peopleSlugs; TaskPaths = taskPaths })
                  BasePath = (fun e -> Naming.eventPath (parseWhen e.When msg.Timestamp) e.Title)
                  TitleOf = (fun e -> e.Title) }
```

Replace the `commitmentSpec` binding with:

```fsharp
            let commitmentSpec : EntitySpec<Commitment> =
                { Generate =
                    (fun intent ->
                        Steps.createCommitment deps.Chat
                            { Intent = intent; Raw = msg.Content; ReferenceDate = isoTimestamp msg.Timestamp
                              Contexts = classification.Contexts; Urgency = classification.Urgency
                              TopicPath = topicPath; MessagePath = messagePath
                              PeopleSlugs = peopleSlugs; TaskPaths = [] })
                  BasePath = (fun c -> Naming.commitmentPath c.Title)
                  TitleOf = (fun c -> c.Title) }
```

(Delete the now-unused `flag`/`ensureDated` lines that lived inside the old `eventSpec.Interpret` — they moved into `Steps.createEvent`. `parseWhen` stays in Pipeline; it is used by `eventSpec.BasePath`.)

- [ ] **Step 4: Write the failing creator tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
let private genInput intent : Steps.GenInput =
    { Intent = intent; Raw = intent; ReferenceDate = "2026-06-24T12:00:00+02:00"
      Contexts = [| "medical" |]; Urgency = "medium"
      TopicPath = "topics/active/t.md"; MessagePath = "messages/m.md"
      PeopleSlugs = [||]; TaskPaths = [] }

[<Fact>]
let ``Steps.createTask parses a model task and stamps linkage`` () =
    let md = "---\ntype: Task\ntitle: Book flu vaccine\nstatus: pending\npriority: medium\ndue: 2026-07-03\ncontext: [medical]\npeople: []\n---\nBook the jab.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createTask (chat :> IChatClient) (genInput "Book flu vaccine")
    Assert.Equal("Book flu vaccine", o.Record.Title)
    Assert.Equal("pending", o.Record.Status)
    Assert.Equal("2026-07-03", o.Record.Due)
    Assert.Equal("topics/active/t.md", o.Record.Topic)        // linkage stamped from input
    Assert.Equal("messages/m.md", o.Record.SourceMessage)

[<Fact>]
let ``Steps.createTask falls back on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "sorry, I cannot do that" ])
    let o = Steps.createTask (chat :> IChatClient) (genInput "Pay the fees")
    Assert.Equal("Pay the fees", o.Record.Title)              // fallback = intent
    Assert.Equal("pending", o.Record.Status)
    Assert.Equal("medium", o.Record.Priority)

[<Fact>]
let ``Steps.createEvent infers date and flags body when when is missing`` () =
    let md = "---\ntype: Event\ntitle: School picnic\nall_day: true\ncontext: [school]\n---\nBring a teddy.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createEvent (chat :> IChatClient) (genInput "School picnic")
    Assert.Equal("2026-06-24T12:00:00+02:00", o.Record.When)  // fell back to reference date
    Assert.Contains("Date inferred", o.Body)

[<Fact>]
let ``Steps.createCommitment parses status unresolved`` () =
    let md = "---\ntype: Commitment\ntitle: Return the form\nstatus: unresolved\npriority: medium\ndue: 2026-07-01\ncontext: [school]\n---\nOwe the school a signed form.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createCommitment (chat :> IChatClient) (genInput "Return the form")
    Assert.Equal("unresolved", o.Record.Status)
    Assert.Equal("2026-07-01", o.Record.Due)
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (the 4 prior Steps tests + 4 new = 8).

Run: `dotnet test`
Expected: PASS — full suite green (behaviour-preserving extraction; `PipelineTests` unchanged and passing).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.createTask/createEvent/createCommitment (shared with eval)"
```

---

## Task 3: Extract `Steps.createNote`

The note creator does not use `EntitySpec`; extract its generation core (`noteCreateSystem` run + `interpretNote`) into `Steps.createNote`.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `createNote`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (rewire `createNewNote`; delete `interpretNote`)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add note test)

**Interfaces:**
- Consumes: `Steps.GenInput`, `Steps.EntityOutcome` (Task 1).
- Produces: `Steps.createNote : IChatClient -> GenInput -> EntityOutcome<Note>`

- [ ] **Step 1: Add `createNote` to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs` (inside `module Steps`). Reproduces the former `createNewNote` prompt + `interpretNote` exactly; `PeopleLinked`/`Source` are linkage set from `input`:

```fsharp
    /// Generate a Note record+body from one intent. Mirrors the former Pipeline createNewNote +
    /// interpretNote. Source/PeopleLinked are pipeline-owned (set from input); the model supplies
    /// title/description/context/tags/body.
    let createNote (chat: IChatClient) (input: GenInput) : EntityOutcome<Note> =
        let user =
            sprintf "Note intent: %s\nRaw message: %s\nContext(s): %s\nSource message file: %s"
                input.Intent input.Raw (String.concat ", " input.Contexts) input.MessagePath
        let stripped = stripFences (Agent.runConversation chat [] Prompts.noteCreateSystem user)
        try
            let parsed = MarkdownFile.FromString stripped
            match parsed.FrontMatter with
            | Some fm ->
                let n = Frontmatter.deserialize<Note> fm
                if not (System.String.IsNullOrWhiteSpace n.Title) then
                    { Record = { n with Type = "Note"; Description = (if System.String.IsNullOrWhiteSpace n.Description then input.Intent else n.Description); Source = input.MessagePath; PeopleLinked = input.PeopleSlugs }
                      Body = parsed.Content }
                else raise (System.Exception("empty title"))
            | None -> raise (System.Exception("no frontmatter"))
        with _ ->
            { Record =
                { Type = "Note"; Title = input.Intent; Description = input.Intent; Context = input.Contexts
                  PeopleLinked = input.PeopleSlugs; Tags = [||]
                  Source = input.MessagePath; LastVerified = "" }
              Body = input.Intent }
```

- [ ] **Step 2: Rewire `createNewNote`; delete `interpretNote`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, delete the `interpretNote` function (currently ~lines 564-579), and replace the `createNewNote` body with a call into Steps:

```fsharp
            let createNewNote (intent: string) : string =
                let outcome =
                    Steps.createNote deps.Chat
                        { Intent = intent; Raw = msg.Content; ReferenceDate = isoTimestamp msg.Timestamp
                          Contexts = classification.Contexts; Urgency = classification.Urgency
                          TopicPath = ""; MessagePath = messagePath
                          PeopleSlugs = peopleSlugs; TaskPaths = [] }
                let text = MarkdownFile.ToString (Frontmatter.serialize outcome.Record) outcome.Body
                let path = freePath deps.Vault (Naming.notePath outcome.Record.Title)
                deps.Vault.Write(path, text)
                path
```

(`processNote`'s match-and-merge path is unchanged; it still calls `createNewNote` on the no-match branch.)

- [ ] **Step 3: Write the failing note test**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
[<Fact>]
let ``Steps.createNote parses a note and stamps source`` () =
    let md = "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\ntags: [insurance]\n---\n## Medical aid\nPolicy 12345.\n"
    let chat = FakeChatClient([ Responses.final md ])
    let o = Steps.createNote (chat :> IChatClient) (genInput "Medical aid number is 12345")
    Assert.Equal("Medical aid details", o.Record.Title)
    Assert.Equal<string array>([| "medical" |], o.Record.Context)
    Assert.Equal("messages/m.md", o.Record.Source)            // linkage from input
    Assert.Contains("Policy 12345", o.Body)
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (9 tests).

Run: `dotnet test`
Expected: PASS — full suite green (behaviour-preserving).

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.createNote (shared with eval)"
```

---

## Task 4: Generative scorers in Scoring.fs

Add four per-type scorers + shared field-assertion helpers, reusing the Phase 1 `setF1`/`mean`/`FieldScore`/`CaseResult`.

**Files:**
- Modify: `eval/Nameless.TaskList.Eval/Scoring.fs` (add helpers + `scoreTask`/`scoreEvent`/`scoreCommitment`/`scoreNote`)
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs` (add tests)

**Interfaces:**
- Consumes: `Dataset.Case`; `Steps.EntityOutcome<'T>`; the KB records `Task`/`Event`/`Commitment`/`Note`.
- Produces (in `module Scoring`):
  - `Scoring.scoreTask : Dataset.Case -> Result<Steps.EntityOutcome<Nameless.TaskList.Core.KnowledgeBase.Task>, string> -> CaseResult`
  - `Scoring.scoreEvent : Dataset.Case -> Result<Steps.EntityOutcome<...Event>, string> -> CaseResult`
  - `Scoring.scoreCommitment : Dataset.Case -> Result<Steps.EntityOutcome<...Commitment>, string> -> CaseResult`
  - `Scoring.scoreNote : Dataset.Case -> Result<Steps.EntityOutcome<...Note>, string> -> CaseResult`

- [ ] **Step 1: Add the shared assertion helpers + four scorers to `Scoring.fs`**

Append to `eval/Nameless.TaskList.Eval/Scoring.fs` (inside `module Scoring`, after `scoreTopic`). `norm`, `setF1`, `mean`, `FieldScore`, `CaseResult` already exist in this module:

```fsharp
    open Nameless.TaskList.Core.KnowledgeBase

    /// The `expected.frontmatter` object, when present.
    let private frontmatterObj (case: Dataset.Case) : JsonElement option =
        match case.Expected.TryGetProperty "frontmatter" with
        | true, v when v.ValueKind = JsonValueKind.Object -> Some v
        | _ -> None

    /// Render a JSON scalar (string/bool/number) to the string form the record field compares as.
    let private jsonScalar (v: JsonElement) : string =
        match v.ValueKind with
        | JsonValueKind.String -> v.GetString()
        | JsonValueKind.True -> "true"
        | JsonValueKind.False -> "false"
        | _ -> v.GetRawText()

    /// Score one scalar frontmatter field (exact, normalised) when the case asserts it.
    let private scoreScalar (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v ->
            let exp = jsonScalar v
            let s = if norm exp = norm actual then 1.0 else 0.0
            fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
        | _ -> ()

    /// Score one array frontmatter field via set-F1 when the case asserts it.
    let private scoreArrayField (fm: JsonElement) (key: string) (actual: string array) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let exp = [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
            let act = if isNull actual then [] else List.ofArray actual
            fields.Add { Field = key; Score = setF1 exp act; Detail = sprintf "exp=%A act=%A" exp act }
        | _ -> ()

    /// Score a date/datetime field: equal instants (DateTimeOffset) when both parse, else exact string.
    let private scoreDateField (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let exp = v.GetString()
            let s =
                match System.DateTimeOffset.TryParse exp, System.DateTimeOffset.TryParse (if isNull actual then "" else actual) with
                | (true, a), (true, b) -> if a = b then 1.0 else 0.0
                | _ -> if norm exp = norm actual then 1.0 else 0.0
            fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
        | _ -> ()

    /// Score `titleMatches` (regex) and `bodyContains` (all substrings present) when asserted.
    let private scoreTitleBody (case: Dataset.Case) (title: string) (body: string) (fields: ResizeArray<FieldScore>) =
        match case.Expected.TryGetProperty "titleMatches" with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let ok = Regex.IsMatch((if isNull title then "" else title), v.GetString())
            fields.Add { Field = "titleMatches"; Score = (if ok then 1.0 else 0.0); Detail = sprintf "title=%s" title }
        | _ -> ()
        match case.Expected.TryGetProperty "bodyContains" with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let needles = [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
            let b = norm body
            let ok = needles |> List.forall (fun n -> b.Contains(norm n))
            fields.Add { Field = "bodyContains"; Score = (if ok then 1.0 else 0.0); Detail = sprintf "needles=%A" needles }
        | _ -> ()

    let private finish (case: Dataset.Case) (fields: ResizeArray<FieldScore>) : CaseResult =
        let fl = List.ofSeq fields
        { Id = case.Id; Step = case.Step; Tags = case.Tags
          Score = mean (fl |> List.map (fun f -> f.Score)); Fields = fl
          NoisePair = None; ParseError = None }

    let private genError (case: Dataset.Case) (e: string) : CaseResult =
        { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0; Fields = []
          NoisePair = None; ParseError = Some e }

    let scoreTask (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Task>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let t = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "status" t.Status fields
                scoreScalar fm "priority" t.Priority fields
                scoreArrayField fm "context" t.Context fields
                scoreDateField fm "due" t.Due fields)
            scoreTitleBody case t.Title o.Body fields
            finish case fields

    let scoreEvent (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Event>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let e = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "all_day" (string e.AllDay) fields
                scoreScalar fm "location" e.Location fields
                scoreScalar fm "reminder_days_before" (string e.ReminderDaysBefore) fields
                scoreArrayField fm "context" e.Context fields
                scoreDateField fm "when" e.When fields)
            scoreTitleBody case e.Title o.Body fields
            finish case fields

    let scoreCommitment (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Commitment>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let c = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "status" c.Status fields
                scoreScalar fm "priority" c.Priority fields
                scoreScalar fm "task_assigned" c.TaskAssigned fields
                scoreScalar fm "escalate_after_days" (string c.EscalateAfterDays) fields
                scoreArrayField fm "context" c.Context fields
                scoreDateField fm "due" c.Due fields)
            scoreTitleBody case c.Title o.Body fields
            finish case fields

    let scoreNote (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Note>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let n = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreArrayField fm "context" n.Context fields
                scoreArrayField fm "tags" n.Tags fields)
            scoreTitleBody case n.Title o.Body fields
            finish case fields
```

Note: the `open Nameless.TaskList.Core.KnowledgeBase` line must sit with the other `open`s at the top of the module (move it up there rather than mid-module if the compiler objects to a mid-module `open`).

- [ ] **Step 2: Write the failing generative-scorer tests**

Append to `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs`:

```fsharp
open Nameless.TaskList.Core.KnowledgeBase

let private genCase (step: string) (expectedJson: string) : Dataset.Case =
    let doc = JsonDocument.Parse(sprintf """{"id":"g","step":"%s","expected":%s}""" step expectedJson)
    let root = doc.RootElement.Clone()
    { Id = "g"; Step = step; Tags = []; World = "_base"
      Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }

let private taskOutcome status priority due title body : Steps.EntityOutcome<Task> =
    { Record = { Type = "Task"; Title = title; Description = ""; Status = status; Priority = priority
                 Due = due; Context = [| "medical" |]; People = [||]; Topic = ""; SourceMessage = "" }
      Body = body }

[<Fact>]
let ``scoreTask perfect generation scores 1`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending","priority":"medium","context":["medical"],"due":"2026-07-03"},"titleMatches":"^Book\\b","bodyContains":["flu vaccine"]}"""
    let r = Scoring.scoreTask case (Ok (taskOutcome "pending" "medium" "2026-07-03" "Book flu vaccine" "Book the flu vaccine soon"))
    Assert.Equal(1.0, r.Score, 3)

[<Fact>]
let ``scoreTask penalises wrong due and bad title`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending","due":"2026-07-03"},"titleMatches":"^Book\\b"}"""
    // status right (1), due wrong (0), title wrong (0) -> mean 1/3
    let r = Scoring.scoreTask case (Ok (taskOutcome "pending" "low" "2026-07-10" "Maybe do the thing" ""))
    Assert.Equal(1.0/3.0, r.Score, 3)

[<Fact>]
let ``scoreTask generation error scores 0`` () =
    let case = genCase "task-create" """{"frontmatter":{"status":"pending"}}"""
    let r = Scoring.scoreTask case (Error "agent exceeded iterations")
    Assert.Equal(0.0, r.Score, 3)
    Assert.True(r.ParseError.IsSome)

[<Fact>]
let ``scoreEvent matches when by instant and all_day`` () =
    let case = genCase "event-create" """{"frontmatter":{"all_day":false,"when":"2026-06-22T10:00:00+02:00"}}"""
    let ev : Steps.EntityOutcome<Event> =
        { Record = { Type = "Event"; Title = "Meeting"; Description = ""; When = "2026-06-22T10:00:00+02:00"; AllDay = false
                     Context = [||]; Location = ""; People = [||]; Topic = ""; TasksLinked = [||]; ReminderDaysBefore = 3 }
          Body = "" }
    Assert.Equal(1.0, (Scoring.scoreEvent case (Ok ev)).Score, 3)
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalScoringTests"`
Expected: PASS (the prior EvalScoringTests + 4 new).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Scoring.fs tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs
git commit -m "feat: eval generative scorers (task/event/commitment/note field assertions)"
```

---

## Task 5: Runner wiring for the four generative steps

**Files:**
- Modify: `eval/Nameless.TaskList.Eval/Runner.fs` (add four step branches + a reference-date helper + an input string-array reader)
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs` (add a generative runner test)

**Interfaces:**
- Consumes: `Steps.createTask/createEvent/createCommitment/createNote`, `Scoring.scoreTask/scoreEvent/scoreCommitment/scoreNote`, `Worlds.load`.
- Produces: four new `runCase` branches (`task-create`/`event-create`/`commitment-create`/`note-create`).

- [ ] **Step 1: Add the helpers + four branches to `Runner.fs`**

In `eval/Nameless.TaskList.Eval/Runner.fs`, add these private helpers near the existing `inputStr` (top of `module Runner`):

```fsharp
    let private inputStrArr (case: Dataset.Case) (name: string) : string array =
        match case.Input.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [| for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() |]
        | _ -> [||]

    /// Normalise a case referenceDate (a bare date or datetime) to a SAST (+02:00) noon ISO
    /// timestamp string, the form the create prompts and the event date-inference expect.
    let private isoRef (s: string) : string =
        match System.DateTimeOffset.TryParse s with
        | true, dto -> System.DateTimeOffset(dto.Year, dto.Month, dto.Day, 12, 0, 0, System.TimeSpan.FromHours 2.0).ToString("yyyy-MM-ddTHH:mm:sszzz")
        | _ -> s

    /// Build the generation input from a case, with neutral stub linkage (the eval scores only
    /// model-generated fields, never linkage).
    let private genInput (case: Dataset.Case) : Steps.GenInput =
        { Intent = inputStr case "intent" (inputStr case "message" "")
          Raw = inputStr case "message" ""
          ReferenceDate = isoRef (inputStr case "referenceDate" "")
          Contexts = inputStrArr case "contexts"
          Urgency = inputStr case "urgency" "medium"
          TopicPath = ""; MessagePath = ""; PeopleSlugs = [||]; TaskPaths = [] }
```

Then add four branches to the `match case.Step with` in `runCase` (alongside `classify`/`topic-match`). Generation can throw (e.g. the agent iteration guard); wrap in `try/with` so a throw scores 0 rather than crashing the run:

```fsharp
        | "task-create" ->
            let r = try Ok (Steps.createTask chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreTask case r
        | "event-create" ->
            let r = try Ok (Steps.createEvent chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreEvent case r
        | "commitment-create" ->
            let r = try Ok (Steps.createCommitment chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreCommitment case r
        | "note-create" ->
            let r = try Ok (Steps.createNote chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreNote case r
```

(These steps do not use the embedder or the world vault tools, so they ignore `embedder`/the seeded vault — that is fine; `runCase` already seeds the vault for every case.)

- [ ] **Step 2: Write the failing generative runner tests (one per step)**

Append to `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs` (the `withDataset` helper from the existing file creates a temp dataset with a `_base` world). A small helper writes a case + scripts one model reply and returns the scored result, so each step gets a deterministic offline wiring test:

```fsharp
/// Write a single generative case under <root>/<step>/c.json, script one model reply, run it.
let private runGen (root: string) (step: string) (inputJson: string) (expectedJson: string) (reply: string) : Scoring.CaseResult =
    Directory.CreateDirectory(Path.Combine(root, step)) |> ignore
    File.WriteAllText(Path.Combine(root, step, "c.json"),
        sprintf """{"id":"c","step":"%s","world":"_base","input":%s,"expected":%s}""" step inputJson expectedJson)
    let chat = FakeChatClient([ Responses.final reply ])
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let case = Dataset.load root [ step ] |> List.head
    Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case

[<Fact>]
let ``runCase scores a task-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "task-create"
                """{"intent":"Book Ethan's flu vaccine before next Friday","message":"...","referenceDate":"2026-06-24","contexts":["medical"],"urgency":"medium"}"""
                """{"frontmatter":{"status":"pending","priority":"medium","context":["medical"],"due":"2026-07-03"},"titleMatches":"^Book\\b","bodyContains":["flu vaccine"]}"""
                "---\ntype: Task\ntitle: Book Ethan's flu vaccine\nstatus: pending\npriority: medium\ndue: 2026-07-03\ncontext: [medical]\npeople: []\n---\nBook the flu vaccine.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores an event-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "event-create"
                """{"intent":"Dentist at 10am on 22 June","message":"...","referenceDate":"2026-06-18","contexts":["medical"],"urgency":"medium"}"""
                """{"frontmatter":{"all_day":false,"when":"2026-06-22T10:00:00+02:00"},"titleMatches":"[Dd]entist"}"""
                "---\ntype: Event\ntitle: Dentist appointment\nwhen: 2026-06-22T10:00:00+02:00\nall_day: false\ncontext: [medical]\n---\nDentist visit.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a commitment-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "commitment-create"
                """{"intent":"Return the signed form by 1 July","message":"...","referenceDate":"2026-06-24","contexts":["school"],"urgency":"medium"}"""
                """{"frontmatter":{"status":"unresolved","due":"2026-07-01"},"bodyContains":["form"]}"""
                "---\ntype: Commitment\ntitle: Return signed form\nstatus: unresolved\npriority: medium\ndue: 2026-07-01\ncontext: [school]\n---\nReturn the signed form.\n"
        Assert.Equal(1.0, r.Score, 3))

[<Fact>]
let ``runCase scores a note-create case end to end`` () =
    withDataset (fun root ->
        let r =
            runGen root "note-create"
                """{"intent":"Medical aid number MA-4471829","message":"...","referenceDate":"2026-06-24","contexts":["medical"],"urgency":"low"}"""
                """{"frontmatter":{"context":["medical"]},"bodyContains":["MA-4471829"]}"""
                "---\ntype: Note\ntitle: Medical aid details\ncontext: [medical]\ntags: []\n---\nMembership MA-4471829.\n"
        Assert.Equal(1.0, r.Score, 3))
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalRunnerTests"`
Expected: PASS (the prior runner test + 4 new).

Run: `dotnet build`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Runner.fs tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs
git commit -m "feat: eval runner wiring for generative steps (task/event/commitment/note-create)"
```

---

## Task 6: Seed the generative dataset + extend the integrity guard

**Files:**
- Create: `eval/dataset/task-create/{flu-vaccine,owner-task-pay-fees}.json`
- Create: `eval/dataset/event-create/{dentist-appointment,picnic-all-day}.json`
- Create: `eval/dataset/commitment-create/return-signed-form.json`
- Create: `eval/dataset/note-create/medical-aid-details.json`
- Modify: `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs` (extend `knownSteps`)
- Modify: `eval/dataset/README.md` (document the generative case schema)

**Interfaces:**
- Consumes: `Dataset.load`, `Worlds.load`. No new production code.

- [ ] **Step 1: Author the task-create cases**

`eval/dataset/task-create/flu-vaccine.json`:

```json
{
  "id": "flu-vaccine",
  "step": "task-create",
  "tags": ["due-resolution", "verb-first-title"],
  "world": "ashford-family",
  "input": {
    "message": "Don't forget to book Ethan's flu vaccine before next Friday.",
    "intent": "Book Ethan's flu vaccine before next Friday",
    "referenceDate": "2026-06-24",
    "contexts": ["medical"],
    "urgency": "medium"
  },
  "expected": {
    "frontmatter": { "status": "pending", "priority": "medium", "context": ["medical"], "due": "2026-07-03" },
    "titleMatches": "^(Book|Schedule|Call)\\b",
    "bodyContains": ["flu vaccine"]
  }
}
```

`eval/dataset/task-create/owner-task-pay-fees.json`:

```json
{
  "id": "owner-task-pay-fees",
  "step": "task-create",
  "tags": ["verb-first-title", "no-date"],
  "world": "ashford-family",
  "input": {
    "message": "The term 3 school fees still need paying.",
    "intent": "Pay the term 3 school fees",
    "referenceDate": "2026-06-24",
    "contexts": ["finance", "school"],
    "urgency": "high"
  },
  "expected": {
    "frontmatter": { "status": "pending", "priority": "high" },
    "titleMatches": "^Pay\\b",
    "bodyContains": ["fees"]
  }
}
```

- [ ] **Step 2: Author the event-create cases**

`eval/dataset/event-create/dentist-appointment.json`:

```json
{
  "id": "dentist-appointment",
  "step": "event-create",
  "tags": ["sast-offset", "timed-event"],
  "world": "ashford-family",
  "input": {
    "message": "Ethan has a dentist appointment on 22 June at 10am.",
    "intent": "Ethan's dentist appointment on 22 June at 10am",
    "referenceDate": "2026-06-18",
    "contexts": ["medical"],
    "urgency": "medium"
  },
  "expected": {
    "frontmatter": { "all_day": false, "when": "2026-06-22T10:00:00+02:00", "context": ["medical"] },
    "titleMatches": "[Dd]entist"
  }
}
```

`eval/dataset/event-create/picnic-all-day.json` (an occurrence with no specific time → the model should mark it all-day). Note: we assert `all_day: true`, NOT our internal "date inferred" flag text — that flag only appears when the model fails to supply any date, which we cannot reliably force from a real model, so asserting it would conflate model quality with a pipeline artifact:

```json
{
  "id": "picnic-all-day",
  "step": "event-create",
  "tags": ["all-day", "no-specific-time"],
  "world": "ashford-family",
  "input": {
    "message": "There's a class picnic on the 30th — no set time yet.",
    "intent": "Class picnic on the 30th",
    "referenceDate": "2026-06-24",
    "contexts": ["school"],
    "urgency": "low"
  },
  "expected": {
    "frontmatter": { "all_day": true },
    "titleMatches": "[Pp]icnic"
  }
}
```

- [ ] **Step 3: Author the commitment-create case**

`eval/dataset/commitment-create/return-signed-form.json`:

```json
{
  "id": "return-signed-form",
  "step": "commitment-create",
  "tags": ["status-unresolved", "due-resolution"],
  "world": "ashford-family",
  "input": {
    "message": "I owe the school the signed consent form by the 1st of July.",
    "intent": "Return the signed consent form to the school by 1 July",
    "referenceDate": "2026-06-24",
    "contexts": ["school"],
    "urgency": "medium"
  },
  "expected": {
    "frontmatter": { "status": "unresolved", "due": "2026-07-01", "context": ["school"] },
    "bodyContains": ["form"]
  }
}
```

- [ ] **Step 4: Author the note-create case**

`eval/dataset/note-create/medical-aid-details.json` (title is a short LABEL, the fact is in the body):

```json
{
  "id": "medical-aid-details",
  "step": "note-create",
  "tags": ["note-as-label", "durable-fact"],
  "world": "ashford-family",
  "input": {
    "message": "For reference, our medical aid membership number is MA-4471829.",
    "intent": "Medical aid membership number MA-4471829",
    "referenceDate": "2026-06-24",
    "contexts": ["medical"],
    "urgency": "low"
  },
  "expected": {
    "frontmatter": { "context": ["medical"] },
    "titleMatches": "^(?!.*MA-4471829).+$",
    "bodyContains": ["MA-4471829"]
  }
}
```

- [ ] **Step 5: Extend the integrity test's known-steps set**

In `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs`, change the `knownSteps` set to include the four new steps:

```fsharp
let private knownSteps = set [ "classify"; "topic-match"; "task-create"; "event-create"; "commitment-create"; "note-create" ]
```

- [ ] **Step 6: Document the generative schema in the dataset README**

In `eval/dataset/README.md`, add to the "Case schema" section (after the existing classify/topic-match bullets):

```markdown
- **task-create / event-create / commitment-create / note-create** `input`: `message`, `intent`,
  `referenceDate`, `contexts` (array), `urgency`. `expected`: a `frontmatter` object of model-generated
  fields to assert (`status`/`priority`/`context`/`due` for tasks; `all_day`/`when`/`location`/
  `reminder_days_before`/`context` for events; `status`/`priority`/`due`/`task_assigned`/
  `escalate_after_days`/`context` for commitments; `context`/`tags` for notes), plus optional
  `titleMatches` (regex) and `bodyContains` (substrings). Linkage fields
  (topic/source_message/tasks_linked/people) are never asserted.
```

- [ ] **Step 7: Run the integrity tests + full suite**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalDatasetIntegrityTests"`
Expected: PASS (3 tests; every committed case — now including the six generative ones — parses, names a known step, and its world loads).

Run: `dotnet test`
Expected: PASS — full suite green.

- [ ] **Step 8: (Optional, needs Ollama) smoke-run the generative steps**

If a local Ollama with the configured model is available:
Run: `dotnet run --project eval/Nameless.TaskList.Eval -- --steps task-create,event-create,commitment-create,note-create --report /tmp/eval-gen.md`
Expected: a scorecard with the four generative step rows; writes `/tmp/eval-gen.md` + `.json`. (No Ollama → exit 2 with a clear message; acceptable, not a build failure.)

- [ ] **Step 9: Commit**

```bash
git add eval/dataset tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs
git commit -m "feat: seed generative eval gold dataset (task/event/commitment/note-create)"
```

---

## Final verification

- [ ] Run `dotnet build` — all projects compile, 0 warnings.
- [ ] Run `dotnet test` — full default suite green (existing + new Steps/eval tests; no Ollama needed).
- [ ] Confirm `git status` clean and the branch holds the six task commits.
- [ ] (If Ollama available) one real `dotnet run --project eval/Nameless.TaskList.Eval -- --steps task-create,event-create,commitment-create,note-create` to eyeball the generative scorecard.
