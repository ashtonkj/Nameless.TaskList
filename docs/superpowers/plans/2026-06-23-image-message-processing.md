# Image-Only Message Processing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give caption-less image messages a vision step before classify — read the image bytes from the Postgres `media` column, get a description+transcription from a local multimodal model, and feed that through the existing pipeline (best-effort, with a fallback to today's behavior).

**Architecture:** A lazy `IMessageSource.GetMediaBytes`; a new `IVision` port + `OllamaVision` adapter (`/api/chat` with a base64 `images` array, `gemma3:latest`); and a single content-substitution point in `processMessage` — for a caption-less `image` message with stored bytes, shadow `msg` with the vision description so the rest of the pipeline runs on it unchanged.

**Tech Stack:** F# / .NET 10, Npgsql, System.Text.Json, ASP.NET Core minimal API, xUnit.

## Global Constraints

- Target framework `net10.0` for every project.
- Spec of record: `docs/superpowers/specs/2026-06-23-image-message-processing-design.md`.
- Best-effort & additive: vision is wrapped in `try/with`; on failure / blank result / no stored bytes, `msg` is unchanged (empty content) and the pipeline behaves exactly as today. Never aborts.
- Vision runs only **after** the idempotency guard (so a `Skipped` reprocess never calls it) and only for `media_type = "image"` with whitespace `content`.
- Any record serialized via `JsonContent.Create`/`JsonSerializer`/`Frontmatter.serialize` must be PUBLIC (a `private` F# record serializes to `{}` — this bit the codebase before).
- `PipelineResult`, the existing routes, the Indexer, the Digest engine, the entity writers, and topic matching are unchanged except the one content-substitution point and the message-body header.
- TDD: every code change starts with a failing test. Commit after each task.
- Out of scope: non-image media; caption images (already text); re-downloading media for rows without stored bytes.

Reference — current shapes:
- `Pipeline.processMessage` begins: `match deps.Messages.GetMessage(id, chatJid) with | None -> NotFound | Some msg -> … let messagePath = … if deps.Vault.Exists messagePath then Skipped else <classify…>`.
- The signal-path Message body is written at the end as `MarkdownFile.ToString (Frontmatter.serialize messageRecord) ("## Raw\n" + msg.Content)`.
- `ChatMessage` (in `Nameless.TaskList.Core`) has `.MediaType` (string, e.g. `"image"` or null) and `.Content` (string, may be empty/null).
- `PipelineDeps = { Messages: IMessageSource; Vault: IVault; Chat: IChatClient; Model: string; Embedder: IEmbedder; TopK: int; SimilarityFloor: float }`.
- `IMessageSource` has `GetMessage`, `GetRecent`, `GetMessagesSince`. Two test fakes implement it: `FakeMessages` (PipelineTests.fs) and `FakeSince` (BulkProcessorTests.fs).
- `OllamaChatClient` (Adapters.fs) is the adapter style; `Conversation.Response.parseResponse : string -> ChatResponse` parses `{message:{content,tool_calls}}`.
- `Program.fs` builds `PipelineDeps` in the `/messages/process` and `/messages/process-since` handlers; the test `deps`/`depsE` helpers build it in `PipelineTests.fs`.
- Core compile order: `… Similarity.fs, Pipeline.fs, BulkProcessor.fs, Indexer.fs, Adapters.fs, Weights.fs, Digest.fs`.

---

### Task 1: `GetMediaBytes` query + port + adapter + fakes

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add `Queries.GetMediaBytes`)
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IMessageSource.GetMediaBytes`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `PostgresMessageSource.GetMediaBytes`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (`FakeMessages` gains an optional media param + `GetMediaBytes`)
- Modify: `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs` (`FakeSince` gains `GetMediaBytes`)

**Interfaces:**
- Produces: `IMessageSource.GetMediaBytes : id: string * chatJid: string -> byte array option`.
- Produces: `FakeMessages(msg: ChatMessage option, ?media: byte array)` whose `GetMediaBytes` returns the supplied media (default `None`).

- [ ] **Step 1: Add the SQL query**

In `src/Nameless.TaskList.Core/Library.fs`, after `GetMessagesSince`, add:

```fsharp
    let GetMediaBytes =
        """
SELECT media
FROM messages
WHERE id = @Id AND chat_jid = @ChatJid AND octet_length(media) > 0;
        """
```

- [ ] **Step 2: Add the port member**

In `src/Nameless.TaskList.Core/Ports.fs`, inside `IMessageSource`, after `GetMessagesSince`:

```fsharp
    abstract member GetMediaBytes : id: string * chatJid: string -> byte array option
```

- [ ] **Step 3: Build to see it fail**

Run: `dotnet build src/Nameless.TaskList.Core`
Expected: FAIL — `PostgresMessageSource` does not implement `GetMediaBytes`.

- [ ] **Step 4: Implement in `PostgresMessageSource`**

In `src/Nameless.TaskList.Core/Adapters.fs`, in the `PostgresMessageSource` interface block, after `GetMessagesSince`:

```fsharp
            member _.GetMediaBytes(id, chatJid) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetMediaBytes, conn)
                cmd.Parameters.AddWithValue("Id", id) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                if reader.Read() && not (reader.IsDBNull 0) then Some(reader.GetFieldValue<byte array>(0)) else None
```

- [ ] **Step 5: Update `FakeMessages` (optional media + method)**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, change the `FakeMessages` type to accept an optional media argument and implement the method:

```fsharp
type FakeMessages(msg: ChatMessage option, ?media: byte array) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = []
        member _.GetMessagesSince(_chatJid, _since) = []
        member _.GetMediaBytes(_id, _jid) = media
```

(Existing call sites `FakeMessages(Some x)` still compile — the second arg is optional.)

- [ ] **Step 6: Update `FakeSince`**

In `tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs`, add to the `FakeSince` interface block:

```fsharp
        member _.GetMediaBytes(_id, _jid) = None
```

- [ ] **Step 7: Build + run all Core tests**

Run: `dotnet build` then `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: build clean; all existing Core tests pass (additive; exercised in Task 3).

- [ ] **Step 8: Commit**

```bash
git add src/Nameless.TaskList.Core/Library.fs src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs
git commit -m "feat: add IMessageSource.GetMediaBytes (lazy media fetch)"
```

---

### Task 2: `IVision` port + `OllamaVision` adapter + `FakeVision`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IVision`)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add `OllamaVision` + public request records)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (add `FakeVision`)
- Modify: `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs` (vision wire test)

**Interfaces:**
- Produces: `IVision.Describe : imageBytes: byte array -> string`; `OllamaVision(httpClient, url, model)`; `FakeVision(describe: byte array -> string)`.

- [ ] **Step 1: Write the failing wire test**

Append to `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs`:

```fsharp
[<Fact>]
let ``OllamaVision posts the image to /api/chat and returns the description`` () =
    let listener = new HttpListener()
    listener.Prefixes.Add("http://localhost:11694/")
    listener.Start()
    let captured = ref ""
    let capturedPath = ref ""
    let worker =
        Thread(fun () ->
            let ctx = listener.GetContext()
            capturedPath.Value <- ctx.Request.Url.AbsolutePath
            use reader = new StreamReader(ctx.Request.InputStream)
            captured.Value <- reader.ReadToEnd()
            let body = Text.Encoding.UTF8.GetBytes("""{"model":"m","message":{"role":"assistant","content":"a birthday invite"},"done":true}""")
            ctx.Response.StatusCode <- 200
            ctx.Response.OutputStream.Write(body, 0, body.Length)
            ctx.Response.OutputStream.Close())
    worker.IsBackground <- true
    worker.Start()
    try
        use http = new HttpClient()
        let vision = OllamaVision(http, "http://localhost:11694", "gemma3:latest") :> Ports.IVision
        let text = vision.Describe([| 1uy; 2uy; 3uy |])
        worker.Join(TimeSpan.FromSeconds 5.0) |> ignore
        Assert.Equal("/api/chat", capturedPath.Value)
        Assert.NotEqual<string>("{}", captured.Value.Trim())      // public-envelope regression
        Assert.Contains("\"images\"", captured.Value)
        Assert.Contains("gemma3:latest", captured.Value)
        Assert.Contains("AQID", captured.Value)                    // base64 of [1;2;3]
        Assert.Equal("a birthday invite", text)
    finally
        listener.Stop()
```

(`AdapterWireTests.fs` already opens `System`, `System.IO`, `System.Net`, `System.Net.Http`, `System.Threading`, `Nameless.TaskList.Core`, `Nameless.TaskList.Core.Adapters`, `Xunit`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: FAIL — `OllamaVision` not defined.

- [ ] **Step 3: Add `IVision` to Ports.fs**

In `src/Nameless.TaskList.Core/Ports.fs`, after `IEmbedder`:

```fsharp
/// Describes an image (and transcribes any text in it) as plain text.
type IVision =
    abstract member Describe : imageBytes: byte array -> string
```

- [ ] **Step 4: Add `OllamaVision` to Adapters.fs**

In `src/Nameless.TaskList.Core/Adapters.fs`, after `OllamaEmbedder`, add:

```fsharp
    // ---- Vision over Ollama ----
    // Public (serialized) — a private record would serialize to `{}`.
    type VisionMessage = { role: string; content: string; images: string array }
    type OllamaVisionRequest = { model: string; messages: VisionMessage array; stream: bool }

    type OllamaVision(httpClient: HttpClient, url: string, model: string) =
        let prompt =
            "Describe this image. If it contains text (an invitation, flyer, schedule, notice, or screenshot), transcribe the text verbatim."
        interface IVision with
            member _.Describe(imageBytes) =
                let b64 = System.Convert.ToBase64String(imageBytes)
                let body =
                    { model = model; stream = false
                      messages = [| { role = "user"; content = prompt; images = [| b64 |] } |] }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                use content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let response = httpClient.PostAsync(Uri(url.TrimEnd('/') + "/api/chat"), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                (Response.parseResponse json).Message.Content
```

- [ ] **Step 5: Add `FakeVision` to Fakes.fs**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (it opens `Nameless.TaskList.Core.Ports`), add:

```fsharp
/// Test vision adapter: maps image bytes -> text via the supplied function (which may throw).
type FakeVision(describe: byte array -> string) =
    interface IVision with
        member _.Describe(imageBytes) = describe imageBytes
```

- [ ] **Step 6: Run to verify the wire test passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: PASS (existing adapter wire tests + the new vision test).

- [ ] **Step 7: Run the full Core suite and commit**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS.

```bash
git add src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests/Fakes.fs tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs
git commit -m "feat: add IVision port and OllamaVision adapter"
```

---

### Task 3: Pipeline vision step + deps + host wiring

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`PipelineDeps.Vision` + the vision step + body header)
- Modify: `src/Nameless.TaskList/Program.fs` (register `IVision`; add `Vision` in both deps-building handlers)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (the `deps`/`depsE` helpers + two image tests)

**Interfaces:**
- Consumes: `IVision`, `IMessageSource.GetMediaBytes`, `Adapters.OllamaVision`.
- Produces: `PipelineDeps` gains `Vision: IVision`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (uses `FakeVault`, `FakeChatClient`, `Responses.final`, `FakeVision`, `sampleMessage`):

```fsharp
// A caption-less image message (empty content, media_type=image).
let private imageMessage () : ChatMessage =
    { sampleMessage () with Content = ""; MediaType = "image" }

[<Fact>]
let ``image-only message is described by vision and processed as that text`` () =
    let vault = FakeVault()
    // classify the vision text as a signal task, new topic, one task, topic update
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"birthday party invite","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["RSVP to the party"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Birthday party"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: RSVP to the party\nstatus: pending\npriority: medium\ncontext:\n  - family\n---\nrsvp"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let messages = FakeMessages(Some(imageMessage ()), [| 1uy; 2uy; 3uy |]) :> IMessageSource
    let vision = FakeVision(fun _ -> "INVITE: Ethan's party Saturday 2pm, please RSVP") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = vision }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains("vision-extracted", vault.Files.[msgKey])           // body header
        Assert.Contains("INVITE: Ethan's party", vault.Files.[msgKey])      // the description is the content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``image-only message falls back to noise when vision fails`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(imageMessage ()), [| 1uy; 2uy |]) :> IMessageSource
    let vision = FakeVision(fun _ -> failwith "vision down") :> IVision
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = vision }
    // vision throws -> content stays empty -> classify (scripted noise) -> ProcessedNoise, no crash
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — `PipelineDeps` has no `Vision` field (compile error), so the whole file fails to build.

- [ ] **Step 3: Add `Vision` to `PipelineDeps`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, add the field to the record:

```fsharp
    type PipelineDeps =
        { Messages: IMessageSource
          Vault: IVault
          Chat: IChatClient
          Model: string
          Embedder: IEmbedder
          TopK: int
          SimilarityFloor: float
          Vision: IVision }
```

- [ ] **Step 4: Insert the vision step + body header**

In `processMessage`, immediately after the idempotency `else` and **before** the `// --- Step: classify` comment, add:

```fsharp
            // --- Step: image-only messages get a vision description before classify ---
            let imageDerived, msg =
                let isImageOnly =
                    (not (isNull msg.MediaType)) && msg.MediaType = "image"
                    && System.String.IsNullOrWhiteSpace msg.Content
                if not isImageOnly then false, msg
                else
                    let described =
                        match deps.Messages.GetMediaBytes(id, chatJid) with
                        | Some bytes -> (try Some(deps.Vision.Describe bytes) with _ -> None)
                        | None -> None
                    match described with
                    | Some t when not (System.String.IsNullOrWhiteSpace t) -> true, { msg with Content = t }
                    | _ -> false, msg
```

Then change the signal-path message-body write (currently `("## Raw\n" + msg.Content)`) to use the header:

```fsharp
            let rawBody = (if imageDerived then "## Image (vision-extracted)\n" else "## Raw\n") + msg.Content
            deps.Vault.Write(messagePath, MarkdownFile.ToString (Frontmatter.serialize messageRecord) rawBody)
```

(`imageDerived` is bound in the `else` block and is in scope at the signal-path write further down — F#'s nested binding scope. The noise-branch write is left unchanged.)

- [ ] **Step 5: Run the pipeline tests (Core)**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: the two new image tests still FAIL to **compile** because the test `deps`/`depsE` helpers don't yet set `Vision`. Fix them in the next step. (Existing tests will compile once the helpers are fixed.)

- [ ] **Step 6: Update the test `deps`/`depsE` helpers**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, add `Vision` to both helpers (a default that throws — existing tests use non-image messages, so it's never called):

```fsharp
      Embedder = FakeEmbedder(fun _ -> failwith "no embedder configured") :> IEmbedder
      TopK = 5; SimilarityFloor = 0.5
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision }
```

Apply the same `Vision = …` line to the `depsE` helper's record literal.

- [ ] **Step 7: Run all Core tests**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS — the two new image tests pass, all existing tests stay green (non-image messages never call vision; the default fake throws if they did).

- [ ] **Step 8: Wire `IVision` into the web host**

In `src/Nameless.TaskList/Program.fs`, after the `IEmbedder` registration, add:

```fsharp
        builder.Services.AddSingleton<IVision>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let visionModel = if isNull cfg.["Ollama:VisionModel"] then "gemma3:latest" else cfg.["Ollama:VisionModel"]
            OllamaVision(http, cfg.["Ollama:Url"], visionModel) :> IVision) |> ignore
```

Then add an `IVision` parameter to **both** handler delegates that build `PipelineDeps` and set the field. For `/messages/process`, change the delegate to `System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, Microsoft.AspNetCore.Http.IResult>` and the lambda to take `(req) (messages) (vault) (chat) (embedder) (vision)`, with the deps record gaining `Vision = vision`. For `/messages/process-since`, change its delegate to `System.Func<ProcessSinceRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, Microsoft.AspNetCore.Http.IResult>` similarly, adding `Vision = vision` to its deps record.

- [ ] **Step 9: Build + run the whole solution**

Run: `dotnet build` then `dotnet test`
Expected: build clean; all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: vision step for caption-less image messages in the pipeline"
```

---

### Task 4: Config + whole-solution verification

**Files:**
- Modify: `src/Nameless.TaskList/appsettings.json`

- [ ] **Step 1: Add the config key**

In `src/Nameless.TaskList/appsettings.json`, add `VisionModel` to the `Ollama` object:

```json
  "Ollama": { "Url": "http://localhost:11434", "Model": "gemma4:e4b", "EmbedModel": "nomic-embed-text", "VisionModel": "gemma3:latest" },
```

(Keep the existing `TopicMatch` and `Vault` keys.)

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Run all tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 4: Confirm scope**

Run: `git diff --stat main..HEAD`
Expected: changes only under `src/Nameless.TaskList.Core/`, `src/Nameless.TaskList/` (DI + config), `tests/`, and `docs/`. `PipelineResult`, the Indexer, and the Digest engine are unchanged; `Pipeline.fs` changes are limited to the deps field, the vision step, and the body header.

---

## Self-Review Notes (for the implementer)

- **Spec coverage:** Task 1 → §3.1 (GetMediaBytes). Task 2 → §3.2 (IVision + OllamaVision + FakeVision + wire test). Task 3 → §4 (the vision step, shadow-substitution, body header, fallback) and §5 (deps + DI) and §7 pipeline tests. Task 4 → §5 config + §2 scope guard.
- **Backward compatibility:** `sampleMessage` has `MediaType = null` and non-empty `Content`, so `isImageOnly` is false → vision never called; the default test `FakeVision`/`FakeEmbedder` throw if mis-invoked. No existing test changes behavior.
- **Type consistency:** `IVision.Describe : byte array -> string` (Task 2) is used by `PipelineDeps.Vision` (Task 3) and the host. `GetMediaBytes : string * string -> byte array option` matches across Library/port/adapter/both fakes. The `PipelineDeps` field set is identical across Pipeline, the two host handlers, and the two test helpers.
- **Vision runs post-idempotency:** the step is inside the `else` of the message-existence guard, so a `Skipped` reprocess never calls the slow model. The bulk reprocessor picks up image handling automatically (it calls `processMessage`).
- **Deferred (do NOT build here):** non-image media, media re-download for rows without bytes, vision-output caching; any change to `PipelineResult`/routes/Indexer/Digest beyond the noted points.
```
