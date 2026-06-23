# Embedding Topic Matching (Increment D-1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the pipeline's topic-match step embedding-assisted — `nomic-embed-text` ranks active topics by cosine similarity to the message intent and shortlists the top-K; the existing LLM prompt confirms among them (or "new"); a clearly-new case skips the LLM; an embedder failure falls back to today's behavior.

**Architecture:** New `IEmbedder` port + `OllamaEmbedder` adapter (Ollama `/api/embed`), a pure `Similarity.cosine`, and a rewritten topic-match step in `Pipeline.fs`. Embeddings are computed on the fly (no persistence). `PipelineDeps` gains `Embedder`/`TopK`/`SimilarityFloor`.

**Tech Stack:** F# / .NET 10, System.Text.Json (embed wire + parse), ASP.NET Core minimal API, xUnit.

## Global Constraints

- Target framework `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-23-embedding-topic-matching-design.md`. KB conventions in `docs/DESIGN.md` (§6.1 topic step, §7.2 prompt, §9 embedding matching).
- **Any record passed to `JsonContent.Create`/`JsonSerializer`/`Frontmatter.serialize` must be PUBLIC** — a `private` F# record serializes to `{}` (this already broke `OllamaRequest` and `IndexMeta`). The embed request envelope must be public, with a code comment.
- Embeddings computed on the fly; no persistence, no caching.
- The hybrid step must **fall back to today's tool-enabled topic match** when the embedder throws or there are zero active topics — never a regression.
- `PipelineResult`, the `/messages/process` contract, the entity writers, the Indexer, and the digest engine are **unchanged**.
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope: persistence/caching of embeddings, batching, the other §9 items.

Reference — current shapes:
- `Ports.IEmbedder` does not exist yet; `Ports` has `IMessageSource`/`IVault`/`IChatClient`.
- `Pipeline.PipelineDeps = { Messages: IMessageSource; Vault: IVault; Chat: IChatClient; Model: string }`.
- `Topic = { Type; Title; Status; Context: string[]; Channel; People: string[]; FirstSeen; LastUpdated; SpawnedTasks: string[]; SpawnedEvents: string[]; MessageRefs: string[] }`.
- `Prompts.topicMatchSystem` + `Prompts.parseTopicMatch : string -> Result<TopicMatch,string>` where `TopicMatch = { Match: bool; TopicSlug: string; Confidence: float; MatchReason: string; NewTopicTitle: string }`.
- `Tools.getTopics`/`Tools.getTopic`, `Agent.runConversation : IChatClient -> Tool list -> string -> string -> string`, `Naming.topicPath`, `Naming.slug`, `MarkdownFile.FromString`/`ToString`, `Frontmatter.serialize`/`deserialize`.
- `OllamaChatClient` (Adapters.fs) is the adapter style to mirror. Core compile order ends `… Prompts.fs, Pipeline.fs, Indexer.fs, Adapters.fs, Weights.fs, Digest.fs`.

---

### Task 1: `Similarity.cosine`

**Files:**
- Create: `src/Nameless.TaskList.Core/Similarity.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (compile before `Pipeline.fs`)
- Create: `tests/Nameless.TaskList.Core.Tests/SimilarityTests.fs`
- Modify: `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `Nameless.TaskList.Core.Similarity.cosine : float array -> float array -> float`.

- [ ] **Step 1: Register the test file**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add after the last `<Compile Include="...Tests.fs" />`:

```xml
        <Compile Include="SimilarityTests.fs" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/SimilarityTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.SimilarityTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``identical vectors have cosine 1`` () =
    Assert.Equal(1.0, Similarity.cosine [| 1.0; 2.0; 3.0 |] [| 1.0; 2.0; 3.0 |], 6)

[<Fact>]
let ``orthogonal vectors have cosine 0`` () =
    Assert.Equal(0.0, Similarity.cosine [| 1.0; 0.0 |] [| 0.0; 1.0 |], 6)

[<Fact>]
let ``opposite vectors have cosine -1`` () =
    Assert.Equal(-1.0, Similarity.cosine [| 1.0; 0.0 |] [| -1.0; 0.0 |], 6)

[<Fact>]
let ``zero norm or mismatched length yields 0`` () =
    Assert.Equal(0.0, Similarity.cosine [| 0.0; 0.0 |] [| 1.0; 1.0 |], 6)
    Assert.Equal(0.0, Similarity.cosine [| 1.0 |] [| 1.0; 2.0 |], 6)
    Assert.Equal(0.0, Similarity.cosine [||] [||], 6)
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SimilarityTests"`
Expected: FAIL — `Similarity` not defined.

- [ ] **Step 4: Create Similarity.fs**

Create `src/Nameless.TaskList.Core/Similarity.fs`:

```fsharp
namespace Nameless.TaskList.Core

module Similarity =

    /// Cosine similarity of two equal-length vectors. Returns 0.0 for null,
    /// length mismatch, empty, or a zero-norm input.
    let cosine (a: float array) (b: float array) : float =
        if isNull a || isNull b || a.Length <> b.Length || a.Length = 0 then 0.0
        else
            let mutable dot = 0.0
            let mutable na = 0.0
            let mutable nb = 0.0
            for i in 0 .. a.Length - 1 do
                dot <- dot + a.[i] * b.[i]
                na <- na + a.[i] * a.[i]
                nb <- nb + b.[i] * b.[i]
            if na = 0.0 || nb = 0.0 then 0.0 else dot / (sqrt na * sqrt nb)
```

- [ ] **Step 5: Register Similarity.fs in the Core project**

In `Nameless.TaskList.Core.fsproj`, add `Similarity.fs` immediately **before** `Pipeline.fs`:

```xml
        <Compile Include="Similarity.fs" />
        <Compile Include="Pipeline.fs" />
```

(Replace the existing standalone `<Compile Include="Pipeline.fs" />` line with these two.)

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~SimilarityTests"`
Expected: PASS, 4 tests.

- [ ] **Step 7: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Similarity.fs src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add Similarity.cosine"
```

---

### Task 2: `IEmbedder` port + `OllamaEmbedder` adapter + `FakeEmbedder`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IEmbedder`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `OllamaEmbedder` + public envelope)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (add `FakeEmbedder`)
- Modify: `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs` (embedder wire test)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `Ports.IEmbedder` with `Embed : text: string -> float array`.
  - `Adapters.OllamaEmbedder(httpClient: HttpClient, url: string, model: string)` implementing `IEmbedder`.
  - `Tests.Fakes.FakeEmbedder(embed: string -> float array)` implementing `IEmbedder`.

- [ ] **Step 1: Write the failing embedder wire test**

Append to `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs`:

```fsharp
[<Fact>]
let ``OllamaEmbedder posts model+input to /api/embed and parses embeddings[0]`` () =
    let listener = new HttpListener()
    listener.Prefixes.Add("http://localhost:11692/")
    listener.Start()
    let captured = ref ""
    let capturedPath = ref ""
    let worker =
        Thread(fun () ->
            let ctx = listener.GetContext()
            capturedPath.Value <- ctx.Request.Url.AbsolutePath
            use reader = new StreamReader(ctx.Request.InputStream)
            captured.Value <- reader.ReadToEnd()
            let body = Text.Encoding.UTF8.GetBytes("""{"model":"m","embeddings":[[0.1,0.2,0.3]]}""")
            ctx.Response.StatusCode <- 200
            ctx.Response.OutputStream.Write(body, 0, body.Length)
            ctx.Response.OutputStream.Close())
    worker.IsBackground <- true
    worker.Start()
    try
        use http = new HttpClient()
        let embedder = OllamaEmbedder(http, "http://localhost:11692", "nomic-embed-text") :> Ports.IEmbedder
        let vec = embedder.Embed("hello")
        worker.Join(TimeSpan.FromSeconds 5.0) |> ignore
        Assert.Equal("/api/embed", capturedPath.Value)
        Assert.NotEqual<string>("{}", captured.Value.Trim())     // public-envelope regression
        Assert.Contains("\"model\"", captured.Value)
        Assert.Contains("\"input\"", captured.Value)
        Assert.Contains("nomic-embed-text", captured.Value)
        Assert.Equal<float array>([| 0.1; 0.2; 0.3 |], vec)
    finally
        listener.Stop()
```

(`AdapterWireTests.fs` already opens `System`, `System.IO`, `System.Net`, `System.Net.Http`, `System.Threading`, `Nameless.TaskList.Core`, `Nameless.TaskList.Core.Adapters`, and `Xunit`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: FAIL — `OllamaEmbedder` not defined.

- [ ] **Step 3: Add `IEmbedder` to Ports.fs**

In `src/Nameless.TaskList.Core/Ports.fs`, after the `IChatClient` type, add:

```fsharp
/// Produces an embedding vector for a piece of text.
type IEmbedder =
    abstract member Embed : text: string -> float array
```

- [ ] **Step 4: Add `OllamaEmbedder` to Adapters.fs**

In `src/Nameless.TaskList.Core/Adapters.fs`, after the `OllamaChatClient` type, add:

```fsharp
    // ---- Embedder over Ollama ----
    // NOTE: must NOT be `private` — a private record serializes to `{}` (System.Text.Json
    // only serializes public types' members), which would send an empty body.
    type OllamaEmbedRequest = { model: string; input: string }

    type OllamaEmbedder(httpClient: HttpClient, url: string, model: string) =
        interface IEmbedder with
            member _.Embed(text) =
                let body = { model = model; input = text }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                let content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let endpoint = url.TrimEnd('/') + "/api/embed"
                let response = httpClient.PostAsync(Uri(endpoint), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                use doc = JsonDocument.Parse(json)
                let first = doc.RootElement.GetProperty("embeddings").[0]
                [| for el in first.EnumerateArray() -> el.GetDouble() |]
```

(`Adapters.fs` already `open`s `System`, `System.Net.Http`, `System.Net.Http.Headers`, `System.Net.Http.Json`, `System.Text.Json`, and `Nameless.TaskList.Core.Ports`. If `JsonDocument` is unqualified-unresolved, use `System.Text.Json.JsonDocument.Parse` instead.)

- [ ] **Step 5: Add `FakeEmbedder` to Fakes.fs**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs`, add (the file already opens `Nameless.TaskList.Core.Ports`):

```fsharp
/// Test embedder: maps text -> vector via the supplied function (which may throw).
type FakeEmbedder(embed: string -> float array) =
    interface IEmbedder with
        member _.Embed(text) = embed text
```

- [ ] **Step 6: Run to verify the wire test passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: PASS (existing adapter wire test + the new embedder test).

- [ ] **Step 7: Run full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: add IEmbedder port, OllamaEmbedder adapter, FakeEmbedder"
```

---

### Task 3: `PipelineDeps` plumbing (fields + DI + test helper) — no behavior change

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`PipelineDeps` record)
- Modify: `src/Nameless.TaskList/Program.fs` (register `IEmbedder`, build deps with the new fields)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (the `deps` helper)

**Interfaces:**
- Consumes: `Ports.IEmbedder`, `Adapters.OllamaEmbedder`.
- Produces: `PipelineDeps = { Messages; Vault; Chat; Model; Embedder: IEmbedder; TopK: int; SimilarityFloor: float }`.

This task only adds and threads the fields; the topic-match step is unchanged, so behavior and all existing tests are identical. (F# records require every field at construction, so the three call sites — Pipeline's type, the host, the test helper — must change together for the build to stay green.)

- [ ] **Step 1: Add the fields to `PipelineDeps`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, change the `PipelineDeps` record to:

```fsharp
    type PipelineDeps =
        { Messages: IMessageSource
          Vault: IVault
          Chat: IChatClient
          Model: string
          Embedder: IEmbedder
          TopK: int
          SimilarityFloor: float }
```

- [ ] **Step 2: Update the test `deps` helper**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, replace the `deps` helper with:

```fsharp
// Default embedder throws — existing tests seed no active topics, so the topic-match
// step never calls it (and if it ever did, the pipeline falls back gracefully).
let deps (messages: IMessageSource) (vault: FakeVault) (chat: IChatClient) : PipelineDeps =
    { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = FakeEmbedder(fun _ -> failwith "no embedder configured") :> IEmbedder
      TopK = 5; SimilarityFloor = 0.5 }
```

- [ ] **Step 3: Build + run all Core tests (no behavior change expected)**

Run: `dotnet build src/Nameless.TaskList.Core` then `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS — all existing tests still green (topic-match step unchanged).

- [ ] **Step 4: Wire `IEmbedder` into the web host**

In `src/Nameless.TaskList/Program.fs`, after the `IChatClient` singleton registration, add an `IEmbedder` registration:

```fsharp
        builder.Services.AddSingleton<IEmbedder>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let embedModel = if isNull cfg.["Ollama:EmbedModel"] then "nomic-embed-text" else cfg.["Ollama:EmbedModel"]
            OllamaEmbedder(http, cfg.["Ollama:Url"], embedModel) :> IEmbedder) |> ignore
```

Then change the `/messages/process` handler to take `IEmbedder` and build the new deps fields. Replace the existing `app.MapPost("/messages/process", …)` block with:

```fsharp
        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, IEmbedder, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) ->
                try
                    let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
                    let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
                    let deps =
                        { Messages = messages; Vault = vault; Chat = chat
                          Model = cfg.["Ollama:Model"]; Embedder = embedder; TopK = topK; SimilarityFloor = floor }
                    processMessage deps req.Id req.ChatJid
                    |> ProcessMessageHandler.toHttp
                with ex ->
                    Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

(`Program.fs` already `open`s `Nameless.TaskList.Core.Ports`, `Nameless.TaskList.Core.Adapters`, `Nameless.TaskList.Core.Pipeline`, `Nameless.TaskList.Core`.)

- [ ] **Step 5: Build + run the whole solution**

Run: `dotnet build` then `dotnet test`
Expected: build clean; all tests pass (Core + endpoint).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: thread Embedder/TopK/SimilarityFloor through PipelineDeps and host"
```

---

### Task 4: Hybrid topic-match flow

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (the topic-match step + an `understandingOf` helper)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (branch tests)

**Interfaces:**
- Consumes: `Similarity.cosine`, `deps.Embedder`, `deps.TopK`, `deps.SimilarityFloor`, `Tools.getTopics`/`getTopic`, `Prompts.topicMatchSystem`/`parseTopicMatch`, `Naming.topicPath`/`slug`.
- Produces: same downstream `topicPath` binding the rest of `processMessage` already uses.

- [ ] **Step 1: Write the failing branch tests**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`. These seed active topics and pass a configured `FakeEmbedder`. Helper to build a deps with a specific embedder:

```fsharp
let private depsE (vault: FakeVault) (chat: IChatClient) (embedder: IEmbedder) (topK: int) (floor: float) : PipelineDeps =
    { Messages = FakeMessages(Some(sampleMessage ())) :> IMessageSource
      Vault = vault :> IVault; Chat = chat; Model = "test-model"
      Embedder = embedder; TopK = topK; SimilarityFloor = floor }

// Seed one active topic with a known slug + understanding.
let private seedTopic (v: FakeVault) (slug: string) (title: string) (understanding: string) =
    v.Seed(sprintf "topics/active/%s.md" slug,
           sprintf "---\ntype: Topic\ntitle: %s\nstatus: active\ncontext:\n  - family\n---\n## Current understanding\n%s\n\n## Open questions\n\n## Resolved\n" title understanding)

let private signalClassify =
    Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"the birthday party plan","action_required":true,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""

let private topicBody = Responses.final "## Current understanding\nx\n\n## Open questions\n\n## Resolved\n"

[<Fact>]
let ``embedding shortlists a similar topic and the LLM confirms the match`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    // intent vector close to the birthday topic's vector
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") || t.Contains("Birthday") || t.Contains("party") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    // classify, then the topic-match LLM confirm (matches the candidate), then topic update
    let confirm = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let chat = FakeChatClient([ signalClassify; confirm; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/birthday-party.md", topic)
    | other -> failwithf "expected Processed match, got %A" other

[<Fact>]
let ``no topic above the floor creates a new topic without an LLM topic-match call`` () =
    let vault = FakeVault()
    seedTopic vault "unrelated" "Unrelated" "something else entirely"
    // every embedding orthogonal -> cosine 0 < floor -> empty shortlist -> fast path
    let embedder = FakeEmbedder(fun t -> if t.Contains("birthday") then [| 1.0; 0.0 |] else [| 0.0; 1.0 |]) :> IEmbedder
    // Only classify + topic update are scripted; a topic-match LLM call would underflow the queue.
    let chat = FakeChatClient([ signalClassify; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) ->
        Assert.StartsWith("topics/active/", topic)
        Assert.DoesNotContain("unrelated", topic)        // a NEW topic, not the seeded one
    | other -> failwithf "expected Processed new, got %A" other

[<Fact>]
let ``LLM returning a slug outside the shortlist creates a new topic`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    let embedder = FakeEmbedder(fun _ -> [| 1.0; 0.0 |]) :> IEmbedder   // everything similar
    let halluc = Responses.final """{"match":true,"topic_slug":"not-a-candidate","confidence":0.9,"match_reason":"x","new_topic_title":"Fresh topic"}"""
    let chat = FakeChatClient([ signalClassify; halluc; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/fresh-topic.md", topic)   // from NewTopicTitle, not the hallucinated slug
    | other -> failwithf "expected Processed new, got %A" other

[<Fact>]
let ``embedder failure falls back to the tool-enabled topic match`` () =
    let vault = FakeVault()
    seedTopic vault "birthday-party" "Birthday party" "planning the party"
    let embedder = FakeEmbedder(fun _ -> failwith "embed down") :> IEmbedder
    // fallback path runs the tool-enabled LLM topic match (1 call), here returning a match
    let fallbackMatch = Responses.final """{"match":true,"topic_slug":"birthday-party","confidence":0.9,"match_reason":"same","new_topic_title":null}"""
    let chat = FakeChatClient([ signalClassify; fallbackMatch; topicBody ])
    let d = depsE vault chat embedder 5 0.5
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(topic, _) -> Assert.Equal("topics/active/birthday-party.md", topic)
    | other -> failwithf "expected Processed match via fallback, got %A" other
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — the new tests don't pass yet (topic-match step is still the old tool-only path).

- [ ] **Step 3: Add the `understandingOf` module-level helper**

In `src/Nameless.TaskList.Core/Pipeline.fs`, among the other module-level `private` helpers (e.g. after `freePath`), add:

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
```

- [ ] **Step 4: Replace the topic-match step with the hybrid flow**

In `processMessage`, replace the entire current topic-match block — from the `// --- Step: topic match (tool-enabled: get_topics / get_topic) ---` comment through the end of the `let _, topicPath = … slug, Naming.topicPath slug` binding — with:

```fsharp
            // --- Step: topic match (embedding shortlist + LLM confirm; fallback to tool-enabled) ---
            let createNewTopic (title: string) =
                let slug = Naming.slug title
                let topicRecord : Topic =
                    { Type = "Topic"; Title = title; Status = "active"
                      Context = classification.Contexts; Channel = channelSlug
                      People = classification.PeopleMentioned
                      FirstSeen = isoTimestamp msg.Timestamp; LastUpdated = isoTimestamp msg.Timestamp
                      SpawnedTasks = [||]; SpawnedEvents = [||]; MessageRefs = [||] }
                let body = "## Current understanding\n\n## Open questions\n\n## Resolved\n"
                deps.Vault.Write(Naming.topicPath slug, MarkdownFile.ToString (Frontmatter.serialize topicRecord) body)
                slug, Naming.topicPath slug

            // Active topics: (slug, title, understanding)
            let activeTopics =
                deps.Vault.ListFiles "topics/active"
                |> List.choose (fun path ->
                    try
                        let mf = MarkdownFile.FromString (deps.Vault.Read path)
                        match mf.FrontMatter with
                        | Some fm ->
                            let t = Frontmatter.deserialize<Topic> fm
                            Some (System.IO.Path.GetFileNameWithoutExtension(path), t.Title, understandingOf mf.Content)
                        | None -> None
                    with _ -> None)

            // Embedding shortlist: Some candidates (possibly empty) when embedding works; None to fall back.
            let shortlist =
                if List.isEmpty activeTopics then None
                else
                    try
                        let intentVec = deps.Embedder.Embed classification.Intent
                        activeTopics
                        |> List.map (fun (slug, title, und) ->
                            let score = Similarity.cosine intentVec (deps.Embedder.Embed (title + "\n" + und))
                            (slug, title, und, score))
                        |> List.filter (fun (_, _, _, s) -> s >= deps.SimilarityFloor)
                        |> List.sortByDescending (fun (_, _, _, s) -> s)
                        |> List.truncate deps.TopK
                        |> Some
                    with _ -> None

            let topicOutcome : Result<string * string, string> =
                match shortlist with
                | Some [] ->
                    // clearly new — skip the LLM topic-match call
                    Ok (createNewTopic (titleFromIntent classification.Intent))
                | Some candidates ->
                    let candidateText =
                        candidates
                        |> List.map (fun (slug, title, und, _) -> sprintf "slug: %s\ntitle: %s\nunderstanding: %s" slug title und)
                        |> String.concat "\n\n"
                    let payload = sprintf "New message intent: %s\n\nCandidate topics:\n%s" classification.Intent candidateText
                    match Prompts.parseTopicMatch (Agent.runConversation deps.Chat [] Prompts.topicMatchSystem payload) with
                    | Error e -> Error e
                    | Ok m ->
                        let slugs = candidates |> List.map (fun (s, _, _, _) -> s) |> Set.ofList
                        if m.Match && Set.contains m.TopicSlug slugs then Ok (m.TopicSlug, Naming.topicPath m.TopicSlug)
                        else
                            let title = if System.String.IsNullOrWhiteSpace m.NewTopicTitle then titleFromIntent classification.Intent else m.NewTopicTitle
                            Ok (createNewTopic title)
                | None ->
                    // fallback: today's tool-enabled match over all active topics
                    let topicTools = [ Tools.getTopics deps.Vault; Tools.getTopic deps.Vault ]
                    let reply = Agent.runConversation deps.Chat topicTools Prompts.topicMatchSystem (sprintf "New message intent: %s" classification.Intent)
                    match Prompts.parseTopicMatch reply with
                    | Error e -> Error e
                    | Ok m ->
                        if m.Match && not (System.String.IsNullOrWhiteSpace m.TopicSlug) then Ok (m.TopicSlug, Naming.topicPath m.TopicSlug)
                        else Ok (createNewTopic m.NewTopicTitle)

            match topicOutcome with
            | Error e -> LlmError e
            | Ok (_, topicPath) ->
```

> The trailing `| Ok (_, topicPath) ->` keeps the same "early-return on error, otherwise fall through to the rest of `processMessage`" idiom the old code used — everything after it (task creation, message write, topic update, channel update, `Processed(...)`) is unchanged and now sees the `topicPath` binding exactly as before.

- [ ] **Step 5: Run the pipeline tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS — the 4 new branch tests pass and all existing pipeline tests stay green (they seed no active topics → `shortlist = None` → fallback → unchanged behavior).

> If an existing test happened to seed an active topic, it would now go through the embedding path with the default throwing embedder → `shortlist = None` → fallback (same as before). No existing test should need editing.

- [ ] **Step 6: Run the full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests
git commit -m "feat: embedding-shortlist + LLM-confirm topic match with fallback"
```

---

### Task 5: Config keys + whole-solution verification

**Files:**
- Modify: `src/Nameless.TaskList/appsettings.json`

- [ ] **Step 1: Add the config keys**

In `src/Nameless.TaskList/appsettings.json`, add to the `Ollama` object and a new `TopicMatch` object:

```json
  "Ollama": { "Url": "http://localhost:11434", "Model": "gemma4:e4b", "EmbedModel": "nomic-embed-text" },
  "TopicMatch": { "TopK": 5, "SimilarityFloor": 0.5 },
```

(Keep the existing `Vault` key. The host already defaults these when absent, so this just makes them explicit/overridable.)

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 4: Confirm scope**

Run: `git diff --stat main..HEAD`
Expected: changes only under `src/Nameless.TaskList.Core/`, `src/Nameless.TaskList/` (DI + config), `tests/`, and `docs/`. `PipelineResult`, the `/messages/process` result mapping, the Indexer, and the digest engine are unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Nameless.TaskList/appsettings.json
git commit -m "chore: add Ollama:EmbedModel and TopicMatch config keys"
```

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Task 1 → §3.3 (cosine). Task 2 → §3.1/§3.2/§3.4 (port, adapter, fake). Task 3 → §5 (deps fields + DI). Task 4 → §4 (the hybrid flow: gather, shortlist, clearly-new fast path, LLM-confirm, hallucination guard, embedder-failure fallback) and §6 (error handling). Task 5 → §5 config + §2 scope guard.
- **Backward compatibility:** existing pipeline tests seed no active topics → `shortlist = None` → fallback → identical behavior; the default test embedder throws (never silently wrong). No existing test should change except the `deps` helper (Task 3).
- **Type consistency:** `IEmbedder.Embed : string -> float array` (Task 2) is used by `PipelineDeps.Embedder` (Task 3) and the hybrid flow (Task 4); `Similarity.cosine : float[] -> float[] -> float` (Task 1) used in Task 4. `PipelineDeps`' new fields match across Pipeline, host, and test helper.
- **Known trade-off:** the clearly-new fast path titles a topic from a truncated intent (no LLM), so its slug is rougher than an LLM-named topic. Documented in the spec's follow-ups; acceptable for this increment.
- **Deferred (do NOT build here):** embedding persistence/caching, batching, and the other §9 items; any change to `PipelineResult`/the endpoint contract/Indexer/Digest.
