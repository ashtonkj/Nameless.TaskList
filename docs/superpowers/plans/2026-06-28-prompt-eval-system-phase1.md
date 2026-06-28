# Prompt Evaluation System — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone CLI that scores the classify and topic-match LLM steps against a hand-labelled gold set and emits a scorecard, so we can detect quality drift and compare models — exercising the *exact* production prompts via shared step functions.

**Architecture:** Extract the per-step LLM calls (`classify`, `matchTopic`) out of `Pipeline.processMessage` into a new `Steps` module in Core, so `processMessage` and the eval call the same code (single source of truth for prompt+parse+tool-loop). A new `eval/Nameless.TaskList.Eval` console project loads JSON gold cases, seeds an in-memory vault from a per-case "world" of anonymised markdown (so the read-only tools return valid, consistent context), runs each case through the `Steps` functions against live Ollama, scores deterministically, and writes a markdown + JSON scorecard with optional baseline diff and a threshold exit code.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2 + Xunit.SkippableFact (existing), System.Text.Json, the existing `OllamaChatClient` / `OllamaEmbedder` adapters.

## Global Constraints

- Target framework: `net10.0` (every project).
- The Steps extraction MUST be **behaviour-preserving** for `processMessage` — the existing `dotnet test` suite (Core.Tests + Tests) stays green with no test logic changes except the two `Pipeline.stripEndearments` references updated to `Steps.stripEndearments`.
- Writes to covered files go through the Verevoir MCP (`write_file`/`edit_file`), never shell redirection or built-in Write/Edit, to keep the shared cache correct.
- Eval scoring is **deterministic only** — no LLM-as-judge.
- The eval is **not** a default `dotnet test` project; it only *runs* against live Ollama. `dotnet build` must still compile it.
- New Core source files are inserted into `Nameless.TaskList.Core.fsproj` in this compile order: `... Prompts.fs, Relationships.fs, Similarity.fs, Steps.fs, Pipeline.fs ...` (Steps after Similarity, before Pipeline).
- Ollama wiring in the eval MUST mirror production determinism: build `OllamaChatClient(http, url, model, numCtx, temperature)` with `NumCtx`/`Temperature` from config (defaults 16384 / 0.0), not the back-compat 3-arg ctor.
- Money/PII: no real names, numbers, or addresses in any committed dataset/world file.

---

## Task 1: Extract `Steps.classify` from Pipeline

**Files:**
- Create: `src/Nameless.TaskList.Core/Steps.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (add `Steps.fs` before `Pipeline.fs`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs:36-45` (remove moved `endearments`/`stripEndearments`), `:357-369` (call `Steps.classify`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs:665-666` (`Pipeline.stripEndearments` → `Steps.stripEndearments`)
- Create: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (add `StepsTests.fs`)

**Interfaces:**
- Produces:
  - `type Steps.ClassifyError = { Message: string; Raw: string }`
  - `Steps.stripEndearments : string array -> string array`
  - `Steps.classify : IChatClient -> IVault -> (history: string) -> (content: string) -> Result<Prompts.Classification, Steps.ClassifyError>`

- [ ] **Step 1: Create `Steps.fs` with the classify step (and moved endearment helpers)**

Create `src/Nameless.TaskList.Core/Steps.fs`:

```fsharp
namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Ports

/// Per-step LLM calls extracted from Pipeline.processMessage so the pipeline and the
/// eval harness call exactly the same prompt+parse+tool-loop code (single source of truth).
module Steps =

    /// Terms of endearment / pet names a partner uses ("hey pookie") are not identifiable
    /// people — left in, they spawn a junk person file. Drop them from a mentions list.
    let private endearments =
        set [ "pookie"; "babe"; "baby"; "bae"; "boo"; "hun"; "hon"; "honey"; "sweetie"
              "sweetheart"; "sweetpea"; "love"; "lovey"; "lovie"; "darling"; "dear"; "dearest"
              "hubby"; "wifey"; "snookums"; "cutie"; "pumpkin"; "sugar"; "my love"; "my dear"
              "my darling"; "my dear wife"; "my dear husband" ]

    let stripEndearments (people: string array) : string array =
        if isNull people then [||]
        else people |> Array.filter (fun p ->
            not (endearments.Contains((if isNull p then "" else p).Trim().ToLowerInvariant())))

    /// A classify failure carries both the parse error and the raw model reply, so the
    /// pipeline can keep its exact [classify-error] log line (id/chat/reason/raw).
    type ClassifyError = { Message: string; Raw: string }

    /// Classify + extract from one message. Tool-enabled (get_contexts/get_people/
    /// get_relationships over the vault). Applies the endearment strip the pipeline relied on.
    let classify (chat: IChatClient) (vault: IVault) (history: string) (content: string)
        : Result<Prompts.Classification, ClassifyError> =
        let classifyTools = [ Tools.getContexts vault; Tools.getPeople vault; Tools.getRelationships vault ]
        let reply =
            Agent.runConversation chat classifyTools Prompts.classifySystem (Prompts.classifyUser history content)
        match Prompts.parseClassification reply with
        | Error e -> Error { Message = e; Raw = (if isNull reply then "<null>" else reply) }
        | Ok c -> Ok { c with PeopleMentioned = stripEndearments c.PeopleMentioned }
```

- [ ] **Step 2: Register `Steps.fs` in the Core project file**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add the compile line immediately before `Pipeline.fs`:

```xml
        <Compile Include="Similarity.fs" />
        <Compile Include="Steps.fs" />
        <Compile Include="Pipeline.fs" />
```

- [ ] **Step 3: Remove the moved helpers from Pipeline and call `Steps.classify`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, delete the `endearments` value and `stripEndearments` function (currently lines 35-45 — the doc comment, the `set [...]`, and the `let stripEndearments ...` block). Leave `isAutomatedNoise` and everything else intact.

Then replace the classify block (currently lines 357-369):

```fsharp
            // --- Step: classify (tool-enabled, may call get_contexts) ---
            let classifyTools = [ Tools.getContexts deps.Vault; Tools.getPeople deps.Vault; Tools.getRelationships deps.Vault ]
            let classifyReply =
                Agent.runConversation deps.Chat classifyTools Prompts.classifySystem (Prompts.classifyUser historyText msg.Content)
            match Prompts.parseClassification classifyReply with
            | Error e ->
                eprintfn "[classify-error] msg=%s chat=%s: %s\n  raw model output: %s"
                    id chatJid e ((if isNull classifyReply then "<null>" else classifyReply.Trim()).Replace("\n", " "))
                LlmError e
            | Ok classification ->

            // Drop terms of endearment the model mistakes for named people.
            let classification = { classification with PeopleMentioned = stripEndearments classification.PeopleMentioned }
```

with:

```fsharp
            // --- Step: classify (tool-enabled; endearment-strip applied inside) ---
            match Steps.classify deps.Chat deps.Vault historyText msg.Content with
            | Error err ->
                eprintfn "[classify-error] msg=%s chat=%s: %s\n  raw model output: %s"
                    id chatJid err.Message (err.Raw.Trim().Replace("\n", " "))
                LlmError err.Message
            | Ok classification ->
```

- [ ] **Step 4: Update the two test references to the moved helper**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (the `stripEndearments` test, ~line 665-666):

```fsharp
let ``stripEndearments removes pet names but keeps real names`` () =
    let out = Steps.stripEndearments [| "Pookie"; "Dr Greef"; " babe "; "Nancy"; "LOVE" |]
```

(Leave the `Pipeline.isAutomatedNoise` test unchanged — that helper stays on Pipeline.)

- [ ] **Step 5: Write the failing Steps.classify test**

Create `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.StepsTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private classifyJson =
    """{"noise":false,"contexts":["family"],"intent":"Wife asks to call the school",
        "action_required":true,"urgency":"medium",
        "people_mentioned":["Nancy","pookie"],
        "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

[<Fact>]
let ``Steps.classify parses and strips endearments`` () =
    let chat = FakeChatClient([ Responses.final classifyJson ])
    let vault = FakeVault()
    match Steps.classify (chat :> IChatClient) (vault :> IVault) "" "Can you call the school?" with
    | Ok c ->
        Assert.False(c.Noise)
        Assert.Equal<string array>([| "Nancy" |], c.PeopleMentioned)   // "pookie" dropped
    | Error e -> failwithf "expected Ok, got Error %s" e.Message

[<Fact>]
let ``Steps.classify returns Error with raw on unparseable reply`` () =
    let chat = FakeChatClient([ Responses.final "I cannot help with that." ])
    let vault = FakeVault()
    match Steps.classify (chat :> IChatClient) (vault :> IVault) "" "hello" with
    | Ok _ -> failwith "expected Error"
    | Error e -> Assert.Contains("cannot help", e.Raw)
```

Add `StepsTests.fs` to `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (after `PromptsTests.fs`, before the test runner consumes it — order is not significant for test files but keep it grouped with the other unit tests):

```xml
        <Compile Include="StepsTests.fs" />
```

- [ ] **Step 6: Run the tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (2 tests). Then full suite:
Run: `dotnet test`
Expected: PASS — all existing tests still green (behaviour-preserving).

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj \
        src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj \
        tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "refactor: extract Steps.classify from Pipeline (single source of truth)"
```

---

## Task 2: Extract `Steps.matchTopic` from Pipeline

**Files:**
- Modify: `src/Nameless.TaskList.Core/Steps.fs` (add `understandingOf`, `titleFromIntent`, `TopicDecision`, `matchTopic`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (remove the moved `understandingOf`/`titleFromIntent`; replace the `activeTopics`/`shortlist`/`topicOutcome` block with a `Steps.matchTopic` call)
- Modify: `tests/Nameless.TaskList.Core.Tests/StepsTests.fs` (add matchTopic tests)

**Interfaces:**
- Consumes: `Steps.classify` (Task 1).
- Produces:
  - `type Steps.TopicDecision = MatchExisting of slug: string | CreateTopic of title: string`
  - `Steps.matchTopic : IChatClient -> IEmbedder -> IVault -> (topK: int) -> (similarityFloor: float) -> (intent: string) -> Result<Steps.TopicDecision, string>`

- [ ] **Step 1: Add the topic-match step to `Steps.fs`**

Append to `src/Nameless.TaskList.Core/Steps.fs` (inside `module Steps`, after `classify`):

```fsharp
    /// Extract the "## Current understanding" section of a topic body (fallback: whole body).
    let private understandingOf (body: string) =
        let b = if isNull body then "" else body
        let marker = "## Current understanding"
        let i = b.IndexOf(marker)
        if i < 0 then b.Trim()
        else
            let after = b.Substring(i + marker.Length)
            let next = after.IndexOf("\n## ")
            (if next < 0 then after else after.Substring(0, next)).Trim()

    /// A concise topic title derived from an intent (used only by the clearly-new fast path).
    let private titleFromIntent (intent: string) =
        let s = (if isNull intent then "" else intent).Trim()
        if s.Length <= 60 then s else s.Substring(0, 60).Trim()

    /// The decision the topic-match step produces. File creation stays in the pipeline; this
    /// is only the prompt-driven routing choice (match an existing topic, or make a new one).
    type TopicDecision =
        | MatchExisting of slug: string
        | CreateTopic of title: string

    /// Decide whether `intent` matches an existing active topic (embedding shortlist + LLM
    /// confirm; tool-enabled fallback when embedding is unavailable). Reads active topics from
    /// the vault; never writes. Behaviour mirrors the former Pipeline topicOutcome exactly.
    let matchTopic
        (chat: IChatClient) (embedder: IEmbedder) (vault: IVault)
        (topK: int) (similarityFloor: float) (intent: string)
        : Result<TopicDecision, string> =

        let topicFiles = vault.ListFiles "topics/active"
        let activeTopics =
            topicFiles
            |> List.choose (fun path ->
                try
                    let mf = MarkdownFile.FromString (vault.Read path)
                    match mf.FrontMatter with
                    | Some fm ->
                        let t = Frontmatter.deserialize<Topic> fm
                        if (if isNull t.Status then "active" else t.Status).Trim().ToLowerInvariant() = "archived" then None
                        else Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, understandingOf mf.Content)
                    | None -> None
                with _ -> None)

        // Some candidates (possibly empty) when embedding works; None to fall back to tools.
        let shortlist =
            if List.isEmpty topicFiles then None
            elif List.isEmpty activeTopics then Some []
            else
                try
                    let intentVec = embedder.Embed intent
                    activeTopics
                    |> List.map (fun (slug, title, und) ->
                        let score = Similarity.cosine intentVec (embedder.Embed (title + "\n" + und))
                        (slug, title, und, score))
                    |> List.filter (fun (_, _, _, s) -> s >= similarityFloor)
                    |> List.sortByDescending (fun (_, _, _, s) -> s)
                    |> List.truncate topK
                    |> Some
                with _ -> None

        match shortlist with
        | Some [] ->
            Ok (CreateTopic (titleFromIntent intent))
        | Some candidates ->
            let candidateText =
                candidates
                |> List.map (fun (slug, title, und, _) -> sprintf "slug: %s\ntitle: %s\nunderstanding: %s" slug title und)
                |> String.concat "\n\n"
            let payload = sprintf "New message intent: %s\n\nCandidate topics:\n%s" intent candidateText
            match Prompts.parseTopicMatch (Agent.runConversation chat [] Prompts.topicMatchSystem payload) with
            | Error e -> Error e
            | Ok m ->
                let normalized = (if isNull m.TopicSlug then "" else m.TopicSlug).Trim().ToLowerInvariant()
                let matched = candidates |> List.tryFind (fun (s, _, _, _) -> s.ToLowerInvariant() = normalized)
                match m.Match, matched with
                | true, Some (slug, _, _, _) -> Ok (MatchExisting slug)
                | _ ->
                    let title = if System.String.IsNullOrWhiteSpace m.NewTopicTitle then titleFromIntent intent else m.NewTopicTitle
                    Ok (CreateTopic title)
        | None ->
            let topicTools = [ Tools.getTopics vault; Tools.getTopic vault ]
            let reply = Agent.runConversation chat topicTools Prompts.topicMatchSystem (sprintf "New message intent: %s" intent)
            match Prompts.parseTopicMatch reply with
            | Error e -> Error e
            | Ok m ->
                if m.Match && not (System.String.IsNullOrWhiteSpace m.TopicSlug) then Ok (MatchExisting m.TopicSlug)
                else Ok (CreateTopic m.NewTopicTitle)
```

- [ ] **Step 2: Remove the moved helpers from Pipeline**

In `src/Nameless.TaskList.Core/Pipeline.fs`, delete the now-duplicated private helpers `understandingOf` (currently ~lines 186-195) and `titleFromIntent` (~lines 197-200). They now live in `Steps`.

- [ ] **Step 3: Replace the Pipeline topic block with a `Steps.matchTopic` call**

In `Pipeline.fs`, replace the whole region from `// Active topics: (slug, title, understanding) ...` through the `match topicOutcome with ... | Ok (_, topicPath, isNewTopic) ->` (currently lines 417-487), keeping the `createNewTopic` closure (lines 405-415) intact above it, with:

```fsharp
            // --- Step: topic match (shared with the eval harness) ---
            match Steps.matchTopic deps.Chat deps.Embedder deps.Vault deps.TopK deps.SimilarityFloor classification.Intent with
            | Error e ->
                eprintfn "[topic-match-error] msg=%s chat=%s: %s" id chatJid e
                LlmError e
            | Ok decision ->

            let topicPath, isNewTopic =
                match decision with
                | Steps.MatchExisting slug -> Naming.topicPath slug, false
                | Steps.CreateTopic title -> let (_, p) = createNewTopic title in p, true
```

(Everything after `| Ok (_, topicPath, isNewTopic) ->` — task creation onward — is unchanged; it already only uses `topicPath` and `isNewTopic`.)

- [ ] **Step 4: Write the failing matchTopic tests**

Append to `tests/Nameless.TaskList.Core.Tests/StepsTests.fs`:

```fsharp
let private topicFile (title: string) =
    sprintf "---\ntype: Topic\ntitle: %s\nstatus: active\n---\n## Current understanding\nThe estate gate is faulty.\n" title

[<Fact>]
let ``Steps.matchTopic matches an existing topic via shortlist + confirm`` () =
    let vault = FakeVault()
    vault.Seed("topics/active/13th-street-gate-fault.md", topicFile "13th Street gate fault")
    // Constant embedding => cosine = 1.0, clears the floor.
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":true,"topic_slug":"13th-street-gate-fault","confidence":0.9,"match_reason":"same gate","new_topic_title":null}""" ])
    match Steps.matchTopic (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) 5 0.5 "the gate motor is slow again" with
    | Ok (Steps.MatchExisting slug) -> Assert.Equal("13th-street-gate-fault", slug)
    | other -> failwithf "expected MatchExisting, got %A" other

[<Fact>]
let ``Steps.matchTopic creates a new topic when no topic files exist`` () =
    let vault = FakeVault()   // no topics/active at all -> tool-enabled fallback
    let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
    let chat = FakeChatClient([ Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"School fees 2026"}""" ])
    match Steps.matchTopic (chat :> IChatClient) (embedder :> IEmbedder) (vault :> IVault) 5 0.5 "school fees are due" with
    | Ok (Steps.CreateTopic title) -> Assert.Equal("School fees 2026", title)
    | other -> failwithf "expected CreateTopic, got %A" other
```

- [ ] **Step 5: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~StepsTests"`
Expected: PASS (4 tests).
Run: `dotnet test`
Expected: PASS — full suite still green (behaviour-preserving extraction).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Steps.fs src/Nameless.TaskList.Core/Pipeline.fs \
        tests/Nameless.TaskList.Core.Tests/StepsTests.fs
git commit -m "refactor: extract Steps.matchTopic from Pipeline (shared with eval)"
```

---

## Task 3: Scaffold the eval console project

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj`
- Create: `eval/Nameless.TaskList.Eval/Config.fs`
- Create: `eval/Nameless.TaskList.Eval/Program.fs`
- Modify: `Nameless.TaskList.slnx` (add an `/eval/` folder + the project)
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (ProjectReference to the eval project so its pure modules are unit-testable)

**Interfaces:**
- Produces:
  - `Nameless.TaskList.Eval.Config.repoRoot : string`
  - `Nameless.TaskList.Eval.Config.OllamaConfig = { Url: string; Model: string; EmbedModel: string; NumCtx: int; Temperature: float }`
  - `Nameless.TaskList.Eval.Config.loadOllama : unit -> OllamaConfig`

- [ ] **Step 1: Create the project file**

Create `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <!-- A tool, not a test project: dotnet build compiles it; it only runs against live Ollama. -->
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="Config.fs" />
        <Compile Include="Program.fs" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\Nameless.TaskList.Core\Nameless.TaskList.Core.fsproj" />
    </ItemGroup>

</Project>
```

(Later tasks insert `Worlds.fs`, `Dataset.fs`, `Scoring.fs`, `Report.fs`, `Runner.fs` before `Program.fs`.)

- [ ] **Step 2: Create the config reader**

Create `eval/Nameless.TaskList.Eval/Config.fs` (reads the host `appsettings.json`, mirroring the integration suite's approach):

```fsharp
namespace Nameless.TaskList.Eval

open System
open System.IO
open System.Text.Json

module Config =

    let rec private findRoot (dir: string) =
        if isNull dir then failwith "could not locate Nameless.TaskList.slnx above the eval assembly"
        elif File.Exists(Path.Combine(dir, "Nameless.TaskList.slnx")) then dir
        else findRoot (Path.GetDirectoryName dir)

    let repoRoot = findRoot AppContext.BaseDirectory
    let private hostSettings = Path.Combine(repoRoot, "src", "Nameless.TaskList", "appsettings.json")

    type OllamaConfig =
        { Url: string; Model: string; EmbedModel: string; NumCtx: int; Temperature: float }

    let loadOllama () : OllamaConfig =
        let doc = JsonDocument.Parse(File.ReadAllText hostSettings)
        let ollama = doc.RootElement.GetProperty("Ollama")
        let str (k: string) (d: string) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> d
        let intOf (k: string) (d: int) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> d
        let floatOf (k: string) (d: float) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
            | _ -> d
        { Url = str "Url" "http://localhost:11434"
          Model = str "Model" "granite4.1:8b"
          EmbedModel = str "EmbedModel" "nomic-embed-text"
          NumCtx = intOf "NumCtx" 16384
          Temperature = floatOf "Temperature" 0.0 }
```

- [ ] **Step 3: Create a placeholder Program**

Create `eval/Nameless.TaskList.Eval/Program.fs`:

```fsharp
module Nameless.TaskList.Eval.Program

open Nameless.TaskList.Eval

[<EntryPoint>]
let main _argv =
    let cfg = Config.loadOllama ()
    printfn "Nameless.TaskList eval — model=%s url=%s (scaffold)" cfg.Model cfg.Url
    0
```

- [ ] **Step 4: Add the project to the solution**

In `Nameless.TaskList.slnx`, add an `/eval/` folder with the project (after the `/tests/` folder block):

```xml
  <Folder Name="/eval/">
    <Project Path="eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj" />
  </Folder>
```

- [ ] **Step 5: Reference the eval project from Core.Tests**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add to the `ProjectReference` ItemGroup (alongside the existing Core reference) so later tasks can unit-test the eval's pure modules:

```xml
        <ProjectReference Include="..\..\eval\Nameless.TaskList.Eval\Nameless.TaskList.Eval.fsproj" />
```

- [ ] **Step 6: Build and run the scaffold**

Run: `dotnet build`
Expected: Build succeeds (all projects, including the new eval).
Run: `dotnet run --project eval/Nameless.TaskList.Eval`
Expected: prints `Nameless.TaskList eval — model=granite4.1:8b url=http://localhost:11434 (scaffold)` and exits 0.

- [ ] **Step 7: Commit**

```bash
git add eval/Nameless.TaskList.Eval Nameless.TaskList.slnx \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: scaffold Nameless.TaskList.Eval console project + config reader"
```

---

## Task 4: World loader + in-memory vault

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Worlds.fs`
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Worlds.fs` before `Program.fs`)
- Create: `eval/dataset/_worlds/_base/contexts/family.md` (+ a second base file, see Step 1)
- Create: `eval/dataset/_worlds/world-a/people/medical/dr-naidoo.md`
- Create: `tests/Nameless.TaskList.Core.Tests/EvalWorldsTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj` (add `EvalWorldsTests.fs`)

**Interfaces:**
- Produces:
  - `Nameless.TaskList.Eval.Worlds.InMemoryVault` (implements `IVault`)
  - `Nameless.TaskList.Eval.Worlds.load : (datasetRoot: string) -> (world: string) -> IVault` — seeds `_base` then overlays the named world (named wins on path collision); a `world` of `"_base"` or `""` yields base only.

- [ ] **Step 1: Create two starter world files**

These let the loader test assert overlay behaviour. Create `eval/dataset/_worlds/_base/contexts/family.md`:

```markdown
---
type: Context
name: family
priority_weight: 3
---
Family matters: spouse, children, household, relatives.
```

Create `eval/dataset/_worlds/world-a/people/medical/dr-naidoo.md`:

```markdown
---
type: Person
title: Dr Naidoo
role: paediatrician
context: medical
aliases: []
---
Paediatrician. ⚠ Stub — details to be completed.
```

- [ ] **Step 2: Create the in-memory vault + world loader**

Create `eval/Nameless.TaskList.Eval/Worlds.fs`:

```fsharp
namespace Nameless.TaskList.Eval

open System.Collections.Generic
open System.IO
open Nameless.TaskList.Core.Ports

module Worlds =

    /// Prefix-based in-memory vault, matching the test FakeVault's ListFiles semantics, so the
    /// read-only Tools.* operate over a world exactly as they do over a real FileSystemVault.
    type InMemoryVault(files: IDictionary<string, string>) =
        interface IVault with
            member _.Exists(relPath) = files.ContainsKey(relPath)
            member _.Read(relPath) = files.[relPath]
            member _.Write(relPath, content) = files.[relPath] <- content
            member _.ListFiles(relDir) =
                let prefix = relDir.TrimEnd('/') + "/"
                files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq
            member _.ListFilesRecursive(relDir) =
                let prefix = relDir.TrimEnd('/') + "/"
                files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq

    /// Read every *.md under `worldDir` into (vault-relative path, content). The vault-relative
    /// path is the path under the world root with '\' normalised to '/'.
    let private readWorldDir (worldDir: string) : (string * string) list =
        if not (Directory.Exists worldDir) then []
        else
            Directory.GetFiles(worldDir, "*.md", SearchOption.AllDirectories)
            |> Array.map (fun full ->
                let rel = Path.GetRelativePath(worldDir, full).Replace('\\', '/')
                rel, File.ReadAllText full)
            |> Array.toList

    /// Seed `_base`, then overlay the named world (named files win on path collision).
    let load (datasetRoot: string) (world: string) : IVault =
        let worldsRoot = Path.Combine(datasetRoot, "_worlds")
        let files = Dictionary<string, string>()
        for (path, content) in readWorldDir (Path.Combine(worldsRoot, "_base")) do
            files.[path] <- content
        if not (System.String.IsNullOrWhiteSpace world) && world <> "_base" then
            for (path, content) in readWorldDir (Path.Combine(worldsRoot, world)) do
                files.[path] <- content
        InMemoryVault(files) :> IVault
```

Register it in the project file before `Program.fs`:

```xml
        <Compile Include="Config.fs" />
        <Compile Include="Worlds.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 3: Write the failing world-loader test**

Create `tests/Nameless.TaskList.Core.Tests/EvalWorldsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalWorldsTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval
open Xunit

let private datasetRoot =
    Path.Combine(Config.repoRoot, "eval", "dataset")

[<Fact>]
let ``base world exposes contexts to get-contexts shape`` () =
    let v = Worlds.load datasetRoot "_base"
    Assert.True(v.Exists "contexts/family.md")
    Assert.Contains("contexts/family.md", v.ListFiles "contexts")

[<Fact>]
let ``named world overlays base`` () =
    let v = Worlds.load datasetRoot "world-a"
    // base file still present
    Assert.True(v.Exists "contexts/family.md")
    // world-a person present and discoverable recursively
    Assert.Contains("people/medical/dr-naidoo.md", v.ListFilesRecursive "people")
```

Add to `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`:

```xml
        <Compile Include="EvalWorldsTests.fs" />
```

- [ ] **Step 4: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalWorldsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add eval tests/Nameless.TaskList.Core.Tests/EvalWorldsTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval world loader + in-memory overlay vault"
```

---

## Task 5: Dataset model + case loader

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Dataset.fs`
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Dataset.fs` before `Program.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/EvalDatasetTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: nothing from earlier eval tasks (pure parsing).
- Produces:
  - `Nameless.TaskList.Eval.Dataset.HistoryTurn = { Sender: string; Content: string; MediaType: string }`
  - `Nameless.TaskList.Eval.Dataset.Case = { Id: string; Step: string; Tags: string list; World: string; Input: JsonElement; Expected: JsonElement; SourcePath: string }`
  - `Nameless.TaskList.Eval.Dataset.load : (datasetRoot: string) -> (steps: string list) -> Case list` — globs `<datasetRoot>/<step>/*.json` for the requested steps (or all step dirs when `steps` is empty), parsing each; `World` defaults to `"_base"` when absent.

- [ ] **Step 1: Create the dataset model + loader**

Create `eval/Nameless.TaskList.Eval/Dataset.fs`:

```fsharp
namespace Nameless.TaskList.Eval

open System.IO
open System.Text.Json

module Dataset =

    type HistoryTurn = { Sender: string; Content: string; MediaType: string }

    /// A gold case. Input/Expected are kept as raw JSON elements so each step's scorer reads the
    /// fields it cares about without a shared schema for every step.
    type Case =
        { Id: string
          Step: string
          Tags: string list
          World: string
          Input: JsonElement
          Expected: JsonElement
          SourcePath: string }

    let private str (e: JsonElement) (name: string) (d: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> d

    let private strList (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
        | _ -> []

    let private parseCase (path: string) : Case =
        let doc = JsonDocument.Parse(File.ReadAllText path)
        let root = doc.RootElement.Clone()   // Clone so it outlives the JsonDocument 'use' scope
        let input = match root.TryGetProperty "input" with | true, v -> v | _ -> root
        let expected = match root.TryGetProperty "expected" with | true, v -> v | _ -> root
        { Id = str root "id" (Path.GetFileNameWithoutExtension path)
          Step = str root "step" ""
          Tags = strList root "tags"
          World = str root "world" "_base"
          Input = input
          Expected = expected
          SourcePath = path }

    /// Load cases for the requested steps (empty list = every step directory present).
    let load (datasetRoot: string) (steps: string list) : Case list =
        let stepDirs =
            if not (List.isEmpty steps) then steps
            else
                Directory.GetDirectories(datasetRoot)
                |> Array.map Path.GetFileName
                |> Array.filter (fun n -> not (n.StartsWith "_"))   // skip _worlds
                |> Array.toList
        stepDirs
        |> List.collect (fun step ->
            let dir = Path.Combine(datasetRoot, step)
            if not (Directory.Exists dir) then []
            else
                Directory.GetFiles(dir, "*.json")
                |> Array.sort
                |> Array.map parseCase
                |> Array.toList)
```

Register before `Program.fs`:

```xml
        <Compile Include="Worlds.fs" />
        <Compile Include="Dataset.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 2: Write the failing loader test (with a temp dataset)**

Create `tests/Nameless.TaskList.Core.Tests/EvalDatasetTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalDatasetTests

open System.IO
open System.Text.Json
open Nameless.TaskList.Eval
open Xunit

let private withTempDataset (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "ntl-eval-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "classify")) |> ignore
    try f root
    finally (try Directory.Delete(root, true) with _ -> ())

[<Fact>]
let ``load reads classify cases with defaults`` () =
    withTempDataset (fun root ->
        let json =
            """{"id":"c1","step":"classify","tags":["noise"],"world":"world-a",
                "input":{"message":"thanks!","referenceDate":"2026-06-24"},
                "expected":{"noise":true}}"""
        File.WriteAllText(Path.Combine(root, "classify", "c1.json"), json)
        let cases = Dataset.load root [ "classify" ]
        let c = Assert.Single(cases)
        Assert.Equal("c1", c.Id)
        Assert.Equal("classify", c.Step)
        Assert.Equal("world-a", c.World)
        Assert.Equal("thanks!", c.Input.GetProperty("message").GetString())
        Assert.True(c.Expected.GetProperty("noise").GetBoolean()))

[<Fact>]
let ``world defaults to _base when absent`` () =
    withTempDataset (fun root ->
        File.WriteAllText(Path.Combine(root, "classify", "c2.json"),
            """{"id":"c2","step":"classify","input":{"message":"x"},"expected":{"noise":false}}""")
        let c = Dataset.load root [ "classify" ] |> List.head
        Assert.Equal("_base", c.World))
```

Add to the test project file:

```xml
        <Compile Include="EvalDatasetTests.fs" />
```

- [ ] **Step 3: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalDatasetTests"`
Expected: PASS (2 tests).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Dataset.fs eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj \
        tests/Nameless.TaskList.Core.Tests/EvalDatasetTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval dataset model + JSON case loader"
```

---

## Task 6: Scoring (set-F1, classify scorer, topic scorer, aggregation)

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Scoring.fs`
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Scoring.fs` before `Program.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `Dataset.Case`; `Nameless.TaskList.Core.Prompts.Classification`; `Nameless.TaskList.Core.Steps.TopicDecision`.
- Produces:
  - `Scoring.FieldScore = { Field: string; Score: float; Detail: string }`
  - `Scoring.CaseResult = { Id: string; Step: string; Tags: string list; Score: float; Fields: FieldScore list; NoisePair: (bool * bool) option; ParseError: string option }`
  - `Scoring.setF1 : (expected: string list) -> (actual: string list) -> float`
  - `Scoring.scoreClassify : Dataset.Case -> Result<Prompts.Classification, string> -> CaseResult`
  - `Scoring.scoreTopic : Dataset.Case -> Result<Steps.TopicDecision, string> -> CaseResult`

- [ ] **Step 1: Create the scoring module**

Create `eval/Nameless.TaskList.Eval/Scoring.fs`:

```fsharp
namespace Nameless.TaskList.Eval

open System.Text.Json
open System.Text.RegularExpressions
open Nameless.TaskList.Core
open Nameless.TaskList.Eval

module Scoring =

    type FieldScore = { Field: string; Score: float; Detail: string }

    type CaseResult =
        { Id: string
          Step: string
          Tags: string list
          Score: float
          Fields: FieldScore list
          NoisePair: (bool * bool) option   // (expected, actual) for the noise-confusion summary
          ParseError: string option }

    let private norm (s: string) = (if isNull s then "" else s).Trim().ToLowerInvariant()

    /// An expected entry may be a glob (`*picnic*`); an actual entry matches it on exact
    /// normalised equality OR glob match. Globless expected entries also match as a substring,
    /// so benign wording variance ("pay school fees" vs "pay the school fees") does not false-fail.
    let private matches (expectedPat: string) (actual: string) : bool =
        let e, a = norm expectedPat, norm actual
        if e = a then true
        elif e.Contains "*" then
            let rx = "^" + Regex.Escape(e).Replace("\\*", ".*") + "$"
            Regex.IsMatch(a, rx)
        else a.Contains e || e.Contains a

    /// Set F1 of actual against expected patterns. Empty-expected + empty-actual = 1.0
    /// (correctly produced nothing); empty-expected + non-empty-actual = 0.0 (spurious output).
    let setF1 (expected: string list) (actual: string list) : float =
        match expected, actual with
        | [], [] -> 1.0
        | [], _ -> 0.0
        | _, [] -> 0.0
        | _ ->
            let tp = expected |> List.filter (fun e -> actual |> List.exists (matches e)) |> List.length |> float
            let precision = tp / float (List.length actual)
            let recall = tp / float (List.length expected)
            if precision + recall = 0.0 then 0.0 else 2.0 * precision * recall / (precision + recall)

    // ---- expected-field readers over the raw JSON ----
    let private expStrList (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
        | _ -> []
    let private expBool (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when (v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False) -> Some(v.GetBoolean())
        | _ -> None
    let private expStr (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private mean (xs: float list) = if List.isEmpty xs then 0.0 else List.average xs

    /// Score a classify result. Present expected fields are scored; absent ones are skipped.
    /// noise = exact; contexts/tasks/events/people = set-F1.
    let scoreClassify (case: Dataset.Case) (result: Result<Prompts.Classification, string>) : CaseResult =
        match result with
        | Error e ->
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0; Fields = []
              NoisePair = (expBool case.Expected "noise" |> Option.map (fun ex -> ex, false))
              ParseError = Some e }
        | Ok c ->
            let exp = case.Expected
            let fields = ResizeArray<FieldScore>()
            let arr (a: string array) = if isNull a then [] else List.ofArray a
            match expBool exp "noise" with
            | Some ex ->
                let s = if ex = c.Noise then 1.0 else 0.0
                fields.Add { Field = "noise"; Score = s; Detail = sprintf "exp=%b act=%b" ex c.Noise }
            | None -> ()
            let scoreArr name (expected: string list) (actual: string list) =
                if exp.TryGetProperty(name) |> fst then
                    let s = setF1 expected actual
                    fields.Add { Field = name; Score = s; Detail = sprintf "exp=%A act=%A" expected actual }
            scoreArr "contexts" (expStrList exp "contexts") (arr c.Contexts)
            scoreArr "tasks"    (expStrList exp "tasks")    (arr c.Entities.Tasks)
            scoreArr "events"   (expStrList exp "events")   (arr c.Entities.Events)
            scoreArr "people"   (expStrList exp "people")   (arr c.PeopleMentioned)
            let fieldList = List.ofSeq fields
            { Id = case.Id; Step = case.Step; Tags = case.Tags
              Score = mean (fieldList |> List.map (fun f -> f.Score))
              Fields = fieldList
              NoisePair = (expBool exp "noise" |> Option.map (fun ex -> ex, c.Noise))
              ParseError = None }

    /// Score a topic-match decision. expected.decision = "match" | "create".
    /// match => MatchExisting with the right slug; create => CreateTopic (+ optional titleContains).
    let scoreTopic (case: Dataset.Case) (result: Result<Steps.TopicDecision, string>) : CaseResult =
        let mk score detail parseErr =
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = score
              Fields = [ { Field = "decision"; Score = score; Detail = detail } ]
              NoisePair = None; ParseError = parseErr }
        match result with
        | Error e -> mk 0.0 (sprintf "error: %s" e) (Some e)
        | Ok decision ->
            let wantKind = expStr case.Expected "decision" |> Option.map norm |> Option.defaultValue ""
            match wantKind, decision with
            | "match", Steps.MatchExisting slug ->
                let wantSlug = expStr case.Expected "slug" |> Option.map norm |> Option.defaultValue ""
                if norm slug = wantSlug then mk 1.0 (sprintf "matched %s" slug) None
                else mk 0.0 (sprintf "matched %s, wanted %s" slug wantSlug) None
            | "create", Steps.CreateTopic title ->
                let needles = expStrList case.Expected "titleContains"
                let ok = needles |> List.forall (fun n -> norm(title).Contains(norm n))
                if ok then mk 1.0 (sprintf "created '%s'" title) None
                else mk 0.0 (sprintf "created '%s', missing %A" title needles) None
            | want, got -> mk 0.0 (sprintf "wanted %s, got %A" want got) None
```

Register before `Program.fs`:

```xml
        <Compile Include="Dataset.fs" />
        <Compile Include="Scoring.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 2: Write the failing scoring tests**

Create `tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalScoringTests

open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Prompts
open Nameless.TaskList.Eval
open Xunit

let private caseFrom (expectedJson: string) : Dataset.Case =
    let doc = JsonDocument.Parse(sprintf """{"id":"t","step":"classify","expected":%s}""" expectedJson)
    let root = doc.RootElement.Clone()
    { Id = "t"; Step = "classify"; Tags = []; World = "_base"
      Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }

let private classification noise contexts tasks events people : Classification =
    { Noise = noise; NoiseReason = ""; Contexts = contexts; Intent = ""; ActionRequired = false
      Urgency = "medium"; PeopleMentioned = people
      Entities = { Tasks = tasks; Events = events; Commitments = [||]; Notes = [||] } }

[<Fact>]
let ``setF1 rewards exact set, punishes spurious`` () =
    Assert.Equal(1.0, Scoring.setF1 [] [], 3)
    Assert.Equal(0.0, Scoring.setF1 [] [ "x" ], 3)
    Assert.Equal(1.0, Scoring.setF1 [ "school" ] [ "School" ], 3)   // normalised
    Assert.Equal(1.0, Scoring.setF1 [ "*picnic*" ] [ "Friday class picnic" ], 3)  // glob

[<Fact>]
let ``scoreClassify perfect case scores 1`` () =
    let case = caseFrom """{"noise":false,"contexts":["school"],"tasks":[],"events":["*picnic*"],"people":[]}"""
    let c = classification false [| "school" |] [||] [| "Friday class picnic" |] [||]
    let r = Scoring.scoreClassify case (Ok c)
    Assert.Equal(1.0, r.Score, 3)
    Assert.Equal(Some(false, false), r.NoisePair)

[<Fact>]
let ``scoreClassify penalises a missed noise call`` () =
    let case = caseFrom """{"noise":true}"""
    let c = classification false [||] [||] [||] [||]
    let r = Scoring.scoreClassify case (Ok c)
    Assert.Equal(0.0, r.Score, 3)
    Assert.Equal(Some(true, false), r.NoisePair)

[<Fact>]
let ``scoreClassify parse error scores 0 and records noise pair`` () =
    let case = caseFrom """{"noise":false}"""
    let r = Scoring.scoreClassify case (Error "bad json")
    Assert.Equal(0.0, r.Score, 3)
    Assert.True(r.ParseError.IsSome)

[<Fact>]
let ``scoreTopic match requires correct slug`` () =
    let mkCase j : Dataset.Case =
        let doc = JsonDocument.Parse(sprintf """{"id":"t","step":"topic-match","expected":%s}""" j)
        let root = doc.RootElement.Clone()
        { Id = "t"; Step = "topic-match"; Tags = []; World = "_base"
          Input = root; Expected = root.GetProperty("expected"); SourcePath = "" }
    let case = mkCase """{"decision":"match","slug":"gate-fault"}"""
    Assert.Equal(1.0, (Scoring.scoreTopic case (Ok (Steps.MatchExisting "gate-fault"))).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreTopic case (Ok (Steps.MatchExisting "other"))).Score, 3)
    Assert.Equal(0.0, (Scoring.scoreTopic case (Ok (Steps.CreateTopic "X"))).Score, 3)
```

Add to the test project file:

```xml
        <Compile Include="EvalScoringTests.fs" />
```

- [ ] **Step 3: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalScoringTests"`
Expected: PASS (5 tests).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Scoring.fs eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj \
        tests/Nameless.TaskList.Core.Tests/EvalScoringTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval scoring — set-F1 + classify/topic scorers"
```

---

## Task 7: Report (aggregation, markdown + JSON, baseline diff)

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Report.fs`
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Report.fs` before `Program.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/EvalReportTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `Scoring.CaseResult`.
- Produces:
  - `Report.Scorecard = { Model: string; Date: string; Overall: float; Steps: (string * float * int) list; Cases: (string * string * float) list }` (step = (name, score, count); case = (id, step, score))
  - `Report.aggregate : (model: string) -> CaseResult list -> Scorecard`
  - `Report.noiseSummary : CaseResult list -> (precision: float * recall: float * fp: string list * fn: string list)`
  - `Report.toJson : Scorecard -> string`
  - `Report.fromJson : string -> Scorecard`
  - `Report.toMarkdown : Scorecard -> CaseResult list -> (baseline: Scorecard option) -> string`

- [ ] **Step 1: Create the report module**

Create `eval/Nameless.TaskList.Eval/Report.fs`:

```fsharp
namespace Nameless.TaskList.Eval

open System
open System.Text
open System.Text.Json
open Nameless.TaskList.Eval

module Report =

    type Scorecard =
        { Model: string
          Date: string
          Overall: float
          Steps: (string * float * int) list      // name, mean score, case count
          Cases: (string * string * float) list }  // id, step, score

    let private meanBy f xs = if List.isEmpty xs then 0.0 else xs |> List.averageBy f

    let aggregate (model: string) (results: Scoring.CaseResult list) : Scorecard =
        let steps =
            results
            |> List.groupBy (fun r -> r.Step)
            |> List.map (fun (step, rs) -> step, meanBy (fun (r: Scoring.CaseResult) -> r.Score) rs, List.length rs)
            |> List.sortBy (fun (s, _, _) -> s)
        // Overall = mean over step means (each step weighted equally), per the spec.
        let overall = if List.isEmpty steps then 0.0 else steps |> List.averageBy (fun (_, s, _) -> s)
        { Model = model
          Date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
          Overall = overall
          Steps = steps
          Cases = results |> List.map (fun r -> r.Id, r.Step, r.Score) }

    /// noise precision/recall over classify cases that carry a NoisePair. "Positive" = noise=true.
    let noiseSummary (results: Scoring.CaseResult list) =
        let pairs = results |> List.choose (fun r -> r.NoisePair |> Option.map (fun p -> r.Id, p))
        let tp = pairs |> List.filter (fun (_, (e, a)) -> e && a)
        let fp = pairs |> List.filter (fun (_, (e, a)) -> not e && a) |> List.map fst
        let fn = pairs |> List.filter (fun (_, (e, a)) -> e && not a) |> List.map fst
        let precision = let d = List.length tp + List.length fp in if d = 0 then 1.0 else float (List.length tp) / float d
        let recall    = let d = List.length tp + List.length fn in if d = 0 then 1.0 else float (List.length tp) / float d
        precision, recall, fp, fn

    let toJson (sc: Scorecard) : string =
        let obj =
            {| model = sc.Model; date = sc.Date; overall = sc.Overall
               steps = [ for (n, s, c) in sc.Steps -> {| name = n; score = s; count = c |} ]
               cases = [ for (id, st, s) in sc.Cases -> {| id = id; step = st; score = s |} ] |}
        JsonSerializer.Serialize(obj, JsonSerializerOptions(JsonSerializerDefaults.Web, WriteIndented = true))

    let fromJson (json: string) : Scorecard =
        let d = JsonDocument.Parse(json)
        let r = d.RootElement
        { Model = r.GetProperty("model").GetString()
          Date = r.GetProperty("date").GetString()
          Overall = r.GetProperty("overall").GetDouble()
          Steps = [ for s in r.GetProperty("steps").EnumerateArray() ->
                        s.GetProperty("name").GetString(), s.GetProperty("score").GetDouble(), s.GetProperty("count").GetInt32() ]
          Cases = [ for c in r.GetProperty("cases").EnumerateArray() ->
                        c.GetProperty("id").GetString(), c.GetProperty("step").GetString(), c.GetProperty("score").GetDouble() ] }

    let private fmt (x: float) = x.ToString("0.000")
    let private delta (cur: float) (baseOpt: float option) =
        match baseOpt with
        | Some b -> let d = cur - b in sprintf " (%s%s)" (if d >= 0.0 then "+" else "") (d.ToString("0.000"))
        | None -> ""

    let toMarkdown (sc: Scorecard) (results: Scoring.CaseResult list) (baseline: Scorecard option) : string =
        let sb = StringBuilder()
        let baseOverall = baseline |> Option.map (fun b -> b.Overall)
        let baseStep name = baseline |> Option.bind (fun b -> b.Steps |> List.tryPick (fun (n, s, _) -> if n = name then Some s else None))
        sb.AppendLine(sprintf "# Eval scorecard — %s" sc.Model) |> ignore
        sb.AppendLine(sprintf "_%s · %d cases · overall **%s**%s_" sc.Date (List.length sc.Cases) (fmt sc.Overall) (delta sc.Overall baseOverall)) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("| Step | Cases | Score |") |> ignore
        sb.AppendLine("|------|------:|------:|") |> ignore
        for (name, score, count) in sc.Steps do
            sb.AppendLine(sprintf "| %s | %d | %s%s |" name count (fmt score) (delta score (baseStep name))) |> ignore
        sb.AppendLine() |> ignore
        let p, r, fp, fn = noiseSummary results
        if results |> List.exists (fun x -> x.NoisePair.IsSome) then
            sb.AppendLine(sprintf "**Noise** — precision %s · recall %s" (fmt p) (fmt r)) |> ignore
            if not (List.isEmpty fp) then sb.AppendLine(sprintf "- false positives: %s" (String.concat ", " fp)) |> ignore
            if not (List.isEmpty fn) then sb.AppendLine(sprintf "- false negatives: %s" (String.concat ", " fn)) |> ignore
            sb.AppendLine() |> ignore
        let failures = results |> List.filter (fun x -> x.Score < 1.0) |> List.sortBy (fun x -> x.Score)
        if not (List.isEmpty failures) then
            sb.AppendLine("## Below-par cases") |> ignore
            for f in failures do
                sb.AppendLine(sprintf "### %s (%s) — %s" f.Id f.Step (fmt f.Score)) |> ignore
                match f.ParseError with Some e -> sb.AppendLine(sprintf "- parse error: %s" e) |> ignore | None -> ()
                for fld in f.Fields do
                    if fld.Score < 1.0 then sb.AppendLine(sprintf "- %s: %s — %s" fld.Field (fmt fld.Score) fld.Detail) |> ignore
        sb.ToString()
```

Register before `Program.fs`:

```xml
        <Compile Include="Scoring.fs" />
        <Compile Include="Report.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 2: Write the failing report tests**

Create `tests/Nameless.TaskList.Core.Tests/EvalReportTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalReportTests

open Nameless.TaskList.Eval
open Xunit

let private result id step score noisePair : Scoring.CaseResult =
    { Id = id; Step = step; Tags = []; Score = score; Fields = []
      NoisePair = noisePair; ParseError = None }

[<Fact>]
let ``aggregate weights steps equally`` () =
    let results =
        [ result "a" "classify" 1.0 None
          result "b" "classify" 0.0 None
          result "c" "topic-match" 1.0 None ]
    let sc = Report.aggregate "m" results
    // classify mean = 0.5, topic-match mean = 1.0, overall = mean(0.5, 1.0) = 0.75
    Assert.Equal(0.75, sc.Overall, 3)

[<Fact>]
let ``noiseSummary computes precision and recall`` () =
    let results =
        [ result "tp" "classify" 1.0 (Some(true, true))
          result "fn" "classify" 0.0 (Some(true, false))
          result "fp" "classify" 0.0 (Some(false, true)) ]
    let p, r, fp, fn = Report.noiseSummary results
    Assert.Equal(0.5, p, 3)   // tp=1, fp=1
    Assert.Equal(0.5, r, 3)   // tp=1, fn=1
    Assert.Equal<string list>([ "fp" ], fp)
    Assert.Equal<string list>([ "fn" ], fn)

[<Fact>]
let ``scorecard JSON round-trips`` () =
    let sc = Report.aggregate "m" [ result "a" "classify" 1.0 None ]
    let back = Report.fromJson (Report.toJson sc)
    Assert.Equal(sc.Overall, back.Overall, 6)
    Assert.Equal(sc.Model, back.Model)

[<Fact>]
let ``markdown shows baseline delta`` () =
    let cur = Report.aggregate "m" [ result "a" "classify" 1.0 None ]
    let baseline = { cur with Overall = 0.8 }
    let md = Report.toMarkdown cur [ result "a" "classify" 1.0 None ] (Some baseline)
    Assert.Contains("+0.200", md)
```

Add to the test project file:

```xml
        <Compile Include="EvalReportTests.fs" />
```

- [ ] **Step 3: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalReportTests"`
Expected: PASS (4 tests).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Report.fs eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj \
        tests/Nameless.TaskList.Core.Tests/EvalReportTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval report — aggregation, markdown/JSON scorecard, baseline diff"
```

---

## Task 8: Runner (case → result via shared Steps)

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Runner.fs`
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Runner.fs` before `Program.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `Dataset.Case`, `Worlds.load`, `Scoring.*`, `Steps.classify`, `Steps.matchTopic`, `IChatClient`, `IEmbedder`.
- Produces:
  - `Runner.runCase : IChatClient -> IEmbedder -> (datasetRoot: string) -> (topK: int) -> (floor: float) -> Dataset.Case -> Scoring.CaseResult`
  - `Runner.runAll : IChatClient -> IEmbedder -> (datasetRoot: string) -> (topK: int) -> (floor: float) -> Dataset.Case list -> Scoring.CaseResult list`

- [ ] **Step 1: Create the runner**

Create `eval/Nameless.TaskList.Eval/Runner.fs`:

```fsharp
namespace Nameless.TaskList.Eval

open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval

module Runner =

    let private inputStr (case: Dataset.Case) (name: string) (d: string) =
        match case.Input.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> d

    /// Render an optional history array on the case input into the oldest->newest transcript the
    /// pipeline builds, reusing Prompts.renderHistory via lightweight ChatMessage stand-ins.
    let private historyText (case: Dataset.Case) : string =
        match case.Input.TryGetProperty "history" with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            // The case authors history oldest->newest; renderHistory expects newest-first, so reverse.
            let turns =
                [ for t in arr.EnumerateArray() ->
                    let s f = match t.TryGetProperty f with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> null
                    s "sender", s "content", s "mediaType" ]
            turns
            |> List.rev
            |> List.map (fun (sender, content, media) ->
                { Id = ""; ChatJid = ""; ChatName = ""; NormalizedChatName = ""; IsGroup = false
                  SenderId = ""; SenderName = (if isNull sender then "Unknown" else sender)
                  SenderPushName = null; SenderSavedName = null; SenderBusinessName = null
                  IsFromMe = false; Platform = "whatsapp-direct"; IsBroadcast = false
                  Content = content; MediaType = media
                  FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = System.DateTime.MinValue } : ChatMessage)
            |> Prompts.renderHistory
        | _ -> ""

    let runCase (chat: IChatClient) (embedder: IEmbedder) (datasetRoot: string)
                (topK: int) (floor: float) (case: Dataset.Case) : Scoring.CaseResult =
        let vault = Worlds.load datasetRoot case.World
        match case.Step with
        | "classify" ->
            let content = inputStr case "message" ""
            let result =
                Steps.classify chat vault (historyText case) content
                |> Result.mapError (fun (e: Steps.ClassifyError) -> e.Message)
            Scoring.scoreClassify case result
        | "topic-match" ->
            let intent = inputStr case "intent" (inputStr case "message" "")
            let result = Steps.matchTopic chat embedder vault topK floor intent
            Scoring.scoreTopic case result
        | other ->
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0
              Fields = [ { Field = "step"; Score = 0.0; Detail = sprintf "unknown step '%s'" other } ]
              NoisePair = None; ParseError = Some(sprintf "unknown step '%s'" other) }

    let runAll (chat: IChatClient) (embedder: IEmbedder) (datasetRoot: string)
               (topK: int) (floor: float) (cases: Dataset.Case list) : Scoring.CaseResult list =
        cases |> List.map (runCase chat embedder datasetRoot topK floor)
```

Register before `Program.fs`:

```xml
        <Compile Include="Report.fs" />
        <Compile Include="Runner.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 2: Write the failing runner test (offline, with fakes)**

This proves the runner wires `Steps` + scoring correctly without Ollama, using the existing `FakeChatClient`/`FakeEmbedder` and a temp dataset+world.

Create `tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalRunnerTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Nameless.TaskList.Eval
open Xunit

let private withDataset (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "ntl-runner-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, "classify")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "_worlds", "_base", "contexts")) |> ignore
    File.WriteAllText(Path.Combine(root, "_worlds", "_base", "contexts", "family.md"),
                      "---\ntype: Context\nname: family\n---\nFamily.")
    try f root
    finally (try Directory.Delete(root, true) with _ -> ())

[<Fact>]
let ``runCase scores a classify case end to end with a fake model`` () =
    withDataset (fun root ->
        File.WriteAllText(Path.Combine(root, "classify", "c1.json"),
            """{"id":"c1","step":"classify","world":"_base",
                "input":{"message":"thanks so much!"},
                "expected":{"noise":true}}""")
        let chat =
            FakeChatClient([ Responses.final
                """{"noise":true,"noise_reason":"gratitude","contexts":[],"intent":null,
                    "action_required":false,"urgency":"none","people_mentioned":[],
                    "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}""" ])
        let embedder = FakeEmbedder(fun _ -> [| 1.0 |])
        let case = Dataset.load root [ "classify" ] |> List.head
        let r = Runner.runCase (chat :> IChatClient) (embedder :> IEmbedder) root 5 0.5 case
        Assert.Equal(1.0, r.Score, 3)
        Assert.Equal(Some(true, true), r.NoisePair))
```

Add to the test project file:

```xml
        <Compile Include="EvalRunnerTests.fs" />
```

- [ ] **Step 3: Run tests, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalRunnerTests"`
Expected: PASS (1 test).

- [ ] **Step 4: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Runner.fs eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj \
        tests/Nameless.TaskList.Core.Tests/EvalRunnerTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval runner — case to scored result via shared Steps"
```

---

## Task 9: CLI Program (arg parsing, wiring, threshold exit)

**Files:**
- Create: `eval/Nameless.TaskList.Eval/Args.fs`
- Modify: `eval/Nameless.TaskList.Eval/Program.fs` (real entry point)
- Modify: `eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj` (add `Args.fs` before `Program.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/EvalArgsTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `Args.Options = { Model: string option; Steps: string list; Report: string option; Baseline: string option; Threshold: float; OllamaUrl: string option }`
  - `Args.parse : string array -> Result<Args.Options, string>`

- [ ] **Step 1: Create the arg parser**

Create `eval/Nameless.TaskList.Eval/Args.fs`:

```fsharp
namespace Nameless.TaskList.Eval

module Args =

    type Options =
        { Model: string option
          Steps: string list
          Report: string option
          Baseline: string option
          Threshold: float
          OllamaUrl: string option }

    let private defaults =
        { Model = None; Steps = []; Report = None; Baseline = None; Threshold = 0.85; OllamaUrl = None }

    /// Parse `--flag value` pairs. `--steps` takes a comma-separated list. Unknown flags error.
    let parse (argv: string array) : Result<Options, string> =
        let rec go (acc: Options) args =
            match args with
            | [] -> Ok acc
            | "--model" :: v :: rest -> go { acc with Model = Some v } rest
            | "--steps" :: v :: rest ->
                let steps = v.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "") |> Array.toList
                go { acc with Steps = steps } rest
            | "--report" :: v :: rest -> go { acc with Report = Some v } rest
            | "--baseline" :: v :: rest -> go { acc with Baseline = Some v } rest
            | "--ollama-url" :: v :: rest -> go { acc with OllamaUrl = Some v } rest
            | "--threshold" :: v :: rest ->
                match System.Double.TryParse v with
                | true, t -> go { acc with Threshold = t } rest
                | _ -> Error(sprintf "invalid --threshold '%s'" v)
            | flag :: _ -> Error(sprintf "unknown or incomplete argument '%s'" flag)
        go defaults (List.ofArray argv)
```

- [ ] **Step 2: Wire the real Program**

Replace `eval/Nameless.TaskList.Eval/Program.fs`:

```fsharp
module Nameless.TaskList.Eval.Program

open System.IO
open System.Net.Http
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Eval

let private datasetRoot = Path.Combine(Config.repoRoot, "eval", "dataset")

// TopK / SimilarityFloor for topic matching — matching the host appsettings TopicMatch defaults.
let private topK = 5
let private floor = 0.35

[<EntryPoint>]
let main argv =
    match Args.parse argv with
    | Error e ->
        eprintfn "arg error: %s" e
        eprintfn "usage: --model M --steps a,b --report out.md --baseline base.json --threshold 0.85 --ollama-url URL"
        2
    | Ok opts ->
        let cfg = Config.loadOllama ()
        let url = opts.OllamaUrl |> Option.defaultValue cfg.Url
        let model = opts.Model |> Option.defaultValue cfg.Model
        let cases = Dataset.load datasetRoot opts.Steps
        if List.isEmpty cases then
            eprintfn "no cases found under %s for steps %A" datasetRoot opts.Steps
            2
        else
            use http = new HttpClient()
            http.Timeout <- System.TimeSpan.FromMinutes 10.0
            let chat = OllamaChatClient(http, url, model, cfg.NumCtx, cfg.Temperature) :> IChatClient
            let embedder = OllamaEmbedder(http, url, cfg.EmbedModel) :> IEmbedder
            let results =
                try Ok (Runner.runAll chat embedder datasetRoot topK floor cases)
                with ex -> Error ex.Message
            match results with
            | Error e ->
                eprintfn "run failed (is Ollama reachable at %s?): %s" url e
                2
            | Ok rs ->
                let scorecard = Report.aggregate model rs
                let baseline = opts.Baseline |> Option.map (fun p -> Report.fromJson (File.ReadAllText p))
                let md = Report.toMarkdown scorecard rs baseline
                printfn "%s" md
                match opts.Report with
                | Some path ->
                    File.WriteAllText(path, md)
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), Report.toJson scorecard)
                | None -> ()
                if scorecard.Overall < opts.Threshold then
                    eprintfn "FAIL: overall %.3f < threshold %.3f" scorecard.Overall opts.Threshold
                    1
                else
                    0
```

Register `Args.fs` before `Program.fs`:

```xml
        <Compile Include="Runner.fs" />
        <Compile Include="Args.fs" />
        <Compile Include="Program.fs" />
```

- [ ] **Step 3: Write the failing args test**

Create `tests/Nameless.TaskList.Core.Tests/EvalArgsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalArgsTests

open Nameless.TaskList.Eval
open Xunit

[<Fact>]
let ``parse reads model, steps, threshold`` () =
    match Args.parse [| "--model"; "granite4.1:8b"; "--steps"; "classify, topic-match"; "--threshold"; "0.9" |] with
    | Ok o ->
        Assert.Equal(Some "granite4.1:8b", o.Model)
        Assert.Equal<string list>([ "classify"; "topic-match" ], o.Steps)
        Assert.Equal(0.9, o.Threshold, 3)
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``parse defaults threshold to 0.85 and steps to empty`` () =
    match Args.parse [||] with
    | Ok o -> Assert.Equal(0.85, o.Threshold, 3); Assert.Empty(o.Steps)
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``parse rejects unknown flag`` () =
    match Args.parse [| "--frobnicate" |] with
    | Ok _ -> failwith "expected Error"
    | Error e -> Assert.Contains("frobnicate", e)
```

Add to the test project file:

```xml
        <Compile Include="EvalArgsTests.fs" />
```

- [ ] **Step 4: Run tests + build, verify green**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalArgsTests"`
Expected: PASS (3 tests).
Run: `dotnet build`
Expected: Build succeeds.
Run: `dotnet run --project eval/Nameless.TaskList.Eval -- --frobnicate`
Expected: prints `arg error: unknown or incomplete argument '--frobnicate'` + usage, exits 2.

- [ ] **Step 5: Commit**

```bash
git add eval/Nameless.TaskList.Eval/Args.fs eval/Nameless.TaskList.Eval/Program.fs \
        eval/Nameless.TaskList.Eval/Nameless.TaskList.Eval.fsproj \
        tests/Nameless.TaskList.Core.Tests/EvalArgsTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: eval CLI — arg parsing, Ollama wiring, threshold exit code"
```

---

## Task 10: Seed the gold dataset + README

**Files:**
- Create: `eval/dataset/README.md`
- Create base world contexts: `eval/dataset/_worlds/_base/contexts/{family,medical,school,finance,professional,personal-kb}.md`
- Create world `ashford-family`: `eval/dataset/_worlds/ashford-family/people/family/sarah-ashford.md`, `.../people/school/teacher-nomsa.md`, `.../topics/active/13th-street-gate-fault.md`
- Create classify cases: `eval/dataset/classify/{noise-gratitude,owner-task-vs-organiser,event-not-incident,person-discipline}.json`
- Create topic-match cases: `eval/dataset/topic-match/{match-gate-fault,create-new-school-fees}.json`
- Create: `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: `Dataset.load`, `Worlds.load`. No new production code.

- [ ] **Step 1: Author the base world contexts**

Create the six files under `eval/dataset/_worlds/_base/contexts/`. Each is a minimal anonymised context definition (the real ones live in the user's private vault). `family.md` already exists from Task 4 — leave it. Create the other five; e.g. `medical.md`:

```markdown
---
type: Context
name: medical
priority_weight: 5
---
Health and medical matters: appointments, prescriptions, results, providers.
```

`school.md` (priority_weight 4): "School and education: classes, fees, events, teachers." 
`finance.md` (priority_weight 4): "Money matters: bills, payments, accounts, advisors." 
`professional.md` (priority_weight 3): "Work matters: colleagues, clients, deadlines." 
`personal-kb.md` (priority_weight 2): "Personal knowledge-base housekeeping and meta notes."

- [ ] **Step 2: Author the `ashford-family` world**

Create `eval/dataset/_worlds/ashford-family/people/family/sarah-ashford.md`:

```markdown
---
type: Person
title: Sarah Ashford
role: spouse
context: family
aliases: ["Sarah"]
---
The KB owner's spouse.
```

Create `eval/dataset/_worlds/ashford-family/people/school/teacher-nomsa.md`:

```markdown
---
type: Person
title: Teacher Nomsa
role: class teacher
context: school
aliases: ["Nomsa", "Ethan's class teacher"]
---
Ethan's class teacher.
```

Create `eval/dataset/_worlds/ashford-family/topics/active/13th-street-gate-fault.md`:

```markdown
---
type: Topic
title: 13th Street gate fault
status: active
context: ["family"]
---
## Current understanding
The estate's 13th Street vehicle gate motor is intermittently failing; the body corporate has been notified.

## Open questions
- When will the technician attend?

## Resolved
```

- [ ] **Step 3: Author the classify gold cases**

`eval/dataset/classify/noise-gratitude.json`:

```json
{
  "id": "noise-gratitude",
  "step": "classify",
  "tags": ["noise-taxonomy"],
  "world": "_base",
  "input": { "message": "Thanks so much for your help yesterday, you're a lifesaver!", "referenceDate": "2026-06-24" },
  "expected": { "noise": true, "tasks": [], "events": [], "people": [] }
}
```

`eval/dataset/classify/owner-task-vs-organiser.json`:

```json
{
  "id": "owner-task-vs-organiser",
  "step": "classify",
  "tags": ["owner-task-discipline", "event-not-task"],
  "world": "ashford-family",
  "input": { "message": "School picnic this Friday at 10am — remember to bring a named teddy for each child and label their water bottles.", "referenceDate": "2026-06-24" },
  "expected": { "noise": false, "contexts": ["school"], "tasks": [], "events": ["*picnic*"] }
}
```

`eval/dataset/classify/event-not-incident.json`:

```json
{
  "id": "event-not-incident",
  "step": "classify",
  "tags": ["event-not-incident"],
  "world": "ashford-family",
  "input": { "message": "Heads up: the 13th Street gate motor failed again last night and security had to let cars in manually.", "referenceDate": "2026-06-24" },
  "expected": { "noise": false, "events": [], "tasks": [] }
}
```

`eval/dataset/classify/person-discipline.json`:

```json
{
  "id": "person-discipline",
  "step": "classify",
  "tags": ["person-discipline"],
  "world": "ashford-family",
  "input": { "message": "Hey babe, can you ask Sarah to confirm the dentist time? The JOC said the road will be closed.", "referenceDate": "2026-06-24" },
  "expected": { "noise": false, "people": ["Sarah"] }
}
```

- [ ] **Step 4: Author the topic-match gold cases**

`eval/dataset/topic-match/match-gate-fault.json`:

```json
{
  "id": "match-gate-fault",
  "step": "topic-match",
  "tags": ["follow-up-same-incident"],
  "world": "ashford-family",
  "input": { "intent": "The 13th Street gate motor is slow and grinding again today." },
  "expected": { "decision": "match", "slug": "13th-street-gate-fault" }
}
```

`eval/dataset/topic-match/create-new-school-fees.json`:

```json
{
  "id": "create-new-school-fees",
  "step": "topic-match",
  "tags": ["distinct-subject"],
  "world": "ashford-family",
  "input": { "intent": "Reminder that term 3 school fees are due at the end of the month." },
  "expected": { "decision": "create", "titleContains": ["fees"] }
}
```

- [ ] **Step 5: Write the dataset README**

Create `eval/dataset/README.md`:

```markdown
# Eval gold dataset

Hand-labelled cases for the prompt-eval CLI (`eval/Nameless.TaskList.Eval`). Run with:

    dotnet run --project eval/Nameless.TaskList.Eval -- --model <model> --report out.md

## Layout

- `classify/*.json`, `topic-match/*.json` — one gold case per file. `step` selects the scorer;
  `world` names the vault fixture seeded for the case's tool calls.
- `_worlds/_base/` — generic, non-PII context definitions + standing identities, always seeded.
- `_worlds/<name>/` — a case's anonymised people / topics, overlaid on `_base` (named files win).

## Case schema

```json
{ "id": "...", "step": "classify|topic-match", "tags": ["..."], "world": "<world or _base>",
  "input": { "message": "...", "intent": "...", "referenceDate": "YYYY-MM-DD", "history": [] },
  "expected": { ... step-specific gold ... } }
```

- **classify** expected: `noise` (bool), `contexts`/`tasks`/`events`/`people` (arrays; entries may be
  `*globs*` or substrings). Only the fields present are scored.
- **topic-match** expected: `{ "decision": "match", "slug": "..." }` or
  `{ "decision": "create", "titleContains": ["..."] }`.

## Anonymisation rule (privacy-first)

Seed cases from real pipeline examples, but before committing apply ONE name/number/address map to
BOTH the message text AND the case's world files, so a person the message names exists as the same
anonymised `people/…` file the tools surface, and `expected` uses the same fictional slugs. Never
commit a real name, phone number, account number, or address.
```

- [ ] **Step 6: Write the dataset-integrity test (offline, no Ollama)**

This guards every committed case: it parses, names a real step, and its world directory exists.

Create `tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EvalDatasetIntegrityTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval
open Xunit

let private datasetRoot = Path.Combine(Config.repoRoot, "eval", "dataset")
let private knownSteps = set [ "classify"; "topic-match" ]

[<Fact>]
let ``every committed case parses and names a known step`` () =
    let cases = Dataset.load datasetRoot []
    Assert.NotEmpty(cases)
    for c in cases do
        Assert.True(knownSteps.Contains c.Step, sprintf "%s: unknown step '%s'" c.Id c.Step)

[<Fact>]
let ``every case world loads and is non-empty`` () =
    for c in Dataset.load datasetRoot [] do
        let v = Worlds.load datasetRoot c.World
        // _base always provides contexts; a named world must too.
        Assert.NotEmpty((v.ListFilesRecursive "contexts"))

[<Fact>]
let ``base world exposes all six contexts`` () =
    let v = Worlds.load datasetRoot "_base"
    for ctx in [ "family"; "medical"; "school"; "finance"; "professional"; "personal-kb" ] do
        Assert.True(v.Exists(sprintf "contexts/%s.md" ctx), sprintf "missing context %s" ctx)
```

Add to the test project file:

```xml
        <Compile Include="EvalDatasetIntegrityTests.fs" />
```

- [ ] **Step 7: Run the integrity tests + full suite**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EvalDatasetIntegrityTests"`
Expected: PASS (3 tests).
Run: `dotnet test`
Expected: PASS — entire default suite green.

- [ ] **Step 8: (Optional, needs Ollama) smoke-run the eval end to end**

If a local Ollama with the configured model + `nomic-embed-text` is available:
Run: `dotnet run --project eval/Nameless.TaskList.Eval -- --report /tmp/eval.md`
Expected: prints a scorecard with `classify` and `topic-match` rows and a noise summary; writes `/tmp/eval.md` + `/tmp/eval.json`; exit 0 or 1 depending on the threshold. (No Ollama → exits 2 with a clear message; that is acceptable and not a build failure.)

- [ ] **Step 9: Commit**

```bash
git add eval/dataset tests/Nameless.TaskList.Core.Tests/EvalDatasetIntegrityTests.fs \
        tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj
git commit -m "feat: seed eval gold dataset (classify + topic-match) + worlds + README"
```

---

## Final verification

- [ ] Run `dotnet build` — all projects compile (Core, host, three test projects, eval).
- [ ] Run `dotnet test` — full default suite green (existing tests + new Steps/eval unit tests; no Ollama needed).
- [ ] Confirm `git status` is clean and the branch contains the 10 task commits.
- [ ] (If Ollama available) one real `dotnet run --project eval/Nameless.TaskList.Eval` to eyeball the scorecard.
