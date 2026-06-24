# Voice-Note Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transcribe caption-less WhatsApp voice notes (Opus/Ogg audio) with a local Whisper CLI and feed the transcript through the existing classify → topic → entities pipeline.

**Architecture:** A new `ITranscriber` port + `WhisperTranscriber` adapter that shells out to the `whisper` CLI (temp `.ogg` → `Process.Start` → read transcript), with a pure `WhisperArgs.build` for the argument list. The pipeline's existing image (vision) step and the new audio (transcription) step are factored into one shared `enrich` helper inside `processMessage`. Best-effort and additive: on any failure the message falls back to today's noise behavior.

**Tech Stack:** F# / .NET 10, xUnit, the installed `whisper` CLI (openai-whisper) + `ffmpeg`, Ollama (unchanged — it has no audio endpoint, so transcription does NOT use it).

## Global Constraints

- **Serialized records must be public.** A `private` F# record serializes to `{}` with System.Text.Json/YamlDotNet. (Not directly relevant here — `WhisperTranscriber` sends no JSON body — but keep any new wire records public.)
- **Never extend `IMessageSource`.** `GetMediaBytes(id, chatJid) : byte array option` already exists and is media-type-agnostic; reuse it. (This avoids a ripple into the message-source fakes.)
- **Transcription is best-effort.** The pipeline call site wraps `Transcribe` in `try/with`; the adapter raises on any failure (timeout, non-zero exit, missing/empty output). No new abort path, no new `PipelineResult` case.
- **Transcription runs only after the idempotency guard** (`if deps.Vault.Exists messagePath then Skipped`) — never on a `Skipped` reprocess, at most once per message.
- **Config keys (exact):** `Whisper:Command` (default `whisper`), `Whisper:Model` (default `base`), `Whisper:Language` (default `""` ⇒ Whisper auto-detects per note), `Whisper:TimeoutSeconds` (default `300`).
- **Body header values (exact):** `## Image (vision-extracted)\n` (image), `## Voice note (transcribed)\n` (audio), `## Raw\n` (neither). Applied on both the signal path and the noise path (noise path preserves derived text).
- `dotnet build` must be clean and `dotnet test` green before any task is considered done.

---

## File Structure

- `src/Nameless.TaskList.Core/Ports.fs` — add `ITranscriber` (one new interface; additive).
- `src/Nameless.TaskList.Core/Adapters.fs` — add `WhisperArgs.build` (pure) + `WhisperTranscriber` (subprocess adapter), in the `Adapters` module.
- `src/Nameless.TaskList.Core/Pipeline.fs` — add `Transcriber` to `PipelineDeps`; replace the standalone vision block with a shared `enrich` helper + two calls; make the body header three-way on both write paths.
- `src/Nameless.TaskList/Program.fs` — register `ITranscriber` singleton; add the param + `Transcriber` field to both `/messages/process` and `/messages/process-since` handlers.
- `src/Nameless.TaskList/appsettings.json` — add the `Whisper` config block.
- `tests/Nameless.TaskList.Core.Tests/Fakes.fs` — add `FakeTranscriber`.
- `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs` — `WhisperArgs.build` unit tests.
- `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` — add `Transcriber` to all six `PipelineDeps` construction sites; add the audio pipeline tests.

**No ripple** into `tests/Nameless.TaskList.Tests/EndpointTests.fs`: it calls `toHttp`/`BulkJobs.tryStart` directly (never constructs `PipelineDeps`) and `FailingMessageSource` implements the unchanged `IMessageSource`.

---

## Task 1: `ITranscriber` port + `WhisperTranscriber` adapter

**Files:**
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (after the `IVision` type, currently ends at line 32)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add after the `OllamaVision` block, currently ends ~line 103, before the `// ---- Message source over Postgres ----` comment at line 105)
- Test: `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `Nameless.TaskList.Core.Ports.ITranscriber` with `abstract member Transcribe : audioBytes: byte array -> string`.
  - `Nameless.TaskList.Core.Adapters.WhisperArgs.build : model:string -> language:string -> inputName:string -> outputDir:string -> string list`.
  - `Nameless.TaskList.Core.Adapters.WhisperTranscriber(command:string, model:string, language:string, timeoutSeconds:int)` implementing `ITranscriber`.

- [ ] **Step 1: Add the `ITranscriber` port**

In `src/Nameless.TaskList.Core/Ports.fs`, append after the `IVision` type (after line 32):

```fsharp

/// Transcribes spoken audio (e.g. a voice note) to plain text.
type ITranscriber =
    abstract member Transcribe : audioBytes: byte array -> string
```

- [ ] **Step 2: Write the failing `WhisperArgs.build` tests**

In `tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs`, append at the end of the file (the module already `open`s `Nameless.TaskList.Core.Adapters` and `Xunit`):

```fsharp
[<Fact>]
let ``WhisperArgs.build emits core flags, output dir and the input name`` () =
    let args = WhisperArgs.build "base" "" "audio.ogg" "/tmp/x"
    Assert.Equal<string list>(
        [ "audio.ogg"; "--model"; "base"; "--output_format"; "txt"
          "--output_dir"; "/tmp/x"; "--fp16"; "False" ],
        args)

[<Fact>]
let ``WhisperArgs.build omits --language when blank`` () =
    let args = WhisperArgs.build "base" "   " "audio.ogg" "/tmp/x"
    Assert.DoesNotContain("--language", args)

[<Fact>]
let ``WhisperArgs.build includes --language when set`` () =
    let args = WhisperArgs.build "base" "en" "audio.ogg" "/tmp/x"
    Assert.Contains("--language", args)
    Assert.Contains("en", args)
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: compile error / FAIL — `WhisperArgs` is not defined yet.

- [ ] **Step 4: Implement `WhisperArgs.build` + `WhisperTranscriber`**

In `src/Nameless.TaskList.Core/Adapters.fs`, insert immediately after the `OllamaVision` block (after line 103, before the `// ---- Message source over Postgres ----` comment). `System.IO` is already `open`ed at the top of the file; `System.Diagnostics` is fully qualified below so no new `open` is needed.

```fsharp
    // ---- Transcription over the local Whisper CLI ----
    // `--language` is included only when set, so an empty value lets Whisper
    // auto-detect each note's language. Pure so it can be unit-tested without the binary.
    module WhisperArgs =
        let build (model: string) (language: string) (inputName: string) (outputDir: string) : string list =
            [ inputName
              "--model"; model
              "--output_format"; "txt"
              "--output_dir"; outputDir
              "--fp16"; "False" ]
            @ (if System.String.IsNullOrWhiteSpace language then [] else [ "--language"; language ])

    type WhisperTranscriber(command: string, model: string, language: string, timeoutSeconds: int) =
        interface ITranscriber with
            member _.Transcribe(audioBytes) =
                let workDir =
                    Path.Combine(Path.GetTempPath(), "whisper-" + System.Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(workDir) |> ignore
                try
                    let inputName = "audio.ogg"
                    File.WriteAllBytes(Path.Combine(workDir, inputName), audioBytes)
                    let psi = System.Diagnostics.ProcessStartInfo(command)
                    WhisperArgs.build model language inputName workDir |> List.iter psi.ArgumentList.Add
                    psi.WorkingDirectory <- workDir
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    use proc = System.Diagnostics.Process.Start(psi)
                    // Read both streams async to avoid a full-pipe deadlock while we wait.
                    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                    let stderrTask = proc.StandardError.ReadToEndAsync()
                    if not (proc.WaitForExit(timeoutSeconds * 1000)) then
                        (try proc.Kill(true) with _ -> ())
                        failwithf "whisper timed out after %d s" timeoutSeconds
                    stdoutTask.Result |> ignore
                    let stderr = stderrTask.Result
                    if proc.ExitCode <> 0 then
                        failwithf "whisper exited %d: %s" proc.ExitCode stderr
                    File.ReadAllText(Path.Combine(workDir, "audio.txt")).Trim()
                finally
                    (try Directory.Delete(workDir, true) with _ -> ())
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~AdapterWireTests"`
Expected: PASS (the three new `WhisperArgs.build` tests plus the existing wire tests).

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build`
Expected: 0 errors. (`ITranscriber`/`WhisperTranscriber` are additive; nothing constructs `PipelineDeps` with the new field yet.)

- [ ] **Step 7: Commit**

```bash
git add src/Nameless.TaskList.Core/Ports.fs src/Nameless.TaskList.Core/Adapters.fs tests/Nameless.TaskList.Core.Tests/AdapterWireTests.fs
git commit -m "feat: add ITranscriber port and WhisperTranscriber adapter"
```

---

## Task 2: Wire the transcriber through the pipeline + host

This is the atomic record-field ripple: adding `Transcriber` to `PipelineDeps` breaks every construction site until all are updated, so the pipeline change, the host DI/handlers, the config, the `FakeTranscriber`, and all existing `PipelineDeps` test sites land together. The refactor's behavior is validated by the **existing** image/vision tests, which now flow through the shared `enrich` helper.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (PipelineDeps lines 8-16; vision block lines 138-151; noise body line 167; signal body line 415)
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (add `FakeTranscriber` after `FakeVision`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (deps sites: lines 33, 260, 277, 290, 309, 319)
- Modify: `src/Nameless.TaskList/Program.fs` (DI after line 38; handlers at lines 42-50 and 74-85)
- Modify: `src/Nameless.TaskList/appsettings.json` (add `Whisper` block)

**Interfaces:**
- Consumes: `ITranscriber` and `WhisperTranscriber` from Task 1.
- Produces: `PipelineDeps` with field `Transcriber: ITranscriber` (added after `Vision: IVision`); `Fakes.FakeTranscriber(transcribe: byte array -> string)` implementing `ITranscriber`.

- [ ] **Step 1: Add `FakeTranscriber`**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs`, append after the `FakeVision` type (it already `open`s `Nameless.TaskList.Core.Ports`):

```fsharp

/// Test transcriber: maps audio bytes -> text via the supplied function (which may throw).
type FakeTranscriber(transcribe: byte array -> string) =
    interface ITranscriber with
        member _.Transcribe(audioBytes) = transcribe audioBytes
```

- [ ] **Step 2: Add the `Transcriber` field to `PipelineDeps`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, change the `PipelineDeps` record (lines 8-16) so the last field reads:

```fsharp
          Vision: IVision
          Transcriber: ITranscriber }
```

- [ ] **Step 3: Replace the vision block with the shared `enrich` helper**

In `src/Nameless.TaskList.Core/Pipeline.fs`, replace the entire current vision block (lines 138-151, beginning `// --- Step: image-only messages...` and ending with the `match described with ...` expression) with:

```fsharp
            // --- Step: image/audio messages get vision/transcription before classify ---
            let enrich (mediaType: string) (extract: byte array -> string) (m: ChatMessage) =
                let isTarget =
                    (not (isNull m.MediaType)) && m.MediaType = mediaType
                    && System.String.IsNullOrWhiteSpace m.Content
                if not isTarget then false, m
                else
                    match deps.Messages.GetMediaBytes(id, chatJid) with
                    | Some bytes ->
                        (try
                            match extract bytes with
                            | t when not (System.String.IsNullOrWhiteSpace t) -> true, { m with Content = t }
                            | _ -> false, m
                         with _ -> false, m)
                    | None -> false, m

            let imageDerived, msg = enrich "image" deps.Vision.Describe msg
            let audioDerived, msg = enrich "audio" deps.Transcriber.Transcribe msg
            let mediaHeader =
                if imageDerived then "## Image (vision-extracted)\n"
                elif audioDerived then "## Voice note (transcribed)\n"
                else "## Raw\n"
```

- [ ] **Step 4: Update the noise-path body write**

In the same file, the noise branch currently (line 167) reads:

```fsharp
                let noiseBody = if imageDerived then "## Image (vision-extracted)\n" + msg.Content else ""
```

Replace it with (preserves the transcript/description for either media type when classified noise; stays empty otherwise):

```fsharp
                let noiseBody = if imageDerived || audioDerived then mediaHeader + msg.Content else ""
```

- [ ] **Step 5: Update the signal-path body write**

In the same file, the signal branch currently (line 415) reads:

```fsharp
            let rawBody = (if imageDerived then "## Image (vision-extracted)\n" else "## Raw\n") + msg.Content
```

Replace it with:

```fsharp
            let rawBody = mediaHeader + msg.Content
```

- [ ] **Step 6: Update the two `PipelineDeps` test helpers**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, the `deps` helper (currently lines 29-33) and the `depsE` helper (currently lines 315-319) each end with a `Vision = FakeVision(...) :> IVision }` line. In **both**, change that closing line to add a `Transcriber` field:

```fsharp
      Vision = FakeVision(fun _ -> failwith "no vision configured") :> IVision
      Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
```

- [ ] **Step 7: Update the four inline image-test deps**

In the same file, the image tests build `d` inline (currently at lines ~258-260, 275-277, 288-290, 307-309), each ending with `Vision = vision }`. In **all four**, change the closing line to:

```fsharp
              Vision = vision
              Transcriber = FakeTranscriber(fun _ -> failwith "no transcriber configured") :> ITranscriber }
```

(The image messages have `MediaType = "image"`, so the `enrich "audio"` step never calls the transcriber — the throwing fake also proves that.)

- [ ] **Step 8: Register `ITranscriber` in host DI**

In `src/Nameless.TaskList/Program.fs`, insert after the `IVision` registration (after line 38, before `let app = builder.Build()`):

```fsharp
        builder.Services.AddSingleton<ITranscriber>(fun _ ->
            let command = if isNull cfg.["Whisper:Command"] then "whisper" else cfg.["Whisper:Command"]
            let model = if isNull cfg.["Whisper:Model"] then "base" else cfg.["Whisper:Model"]
            let language = if isNull cfg.["Whisper:Language"] then "" else cfg.["Whisper:Language"]
            let timeout = match System.Int32.TryParse(cfg.["Whisper:TimeoutSeconds"]) with | true, v -> v | _ -> 300
            WhisperTranscriber(command, model, language, timeout) :> ITranscriber) |> ignore
```

- [ ] **Step 9: Thread the transcriber through `/messages/process`**

In `src/Nameless.TaskList/Program.fs`, change the `/messages/process` handler. The `System.Func<...>` type list (line 42) and the lambda params (line 43) gain `ITranscriber`, and the deps record (line 50) gains the field. After the edit:

```fsharp
        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, ITranscriber, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) ->
                try
                    let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
                    let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
                    let deps =
                        { Messages = messages; Vault = vault; Chat = chat
                          Model = cfg.["Ollama:Model"]; Embedder = embedder; TopK = topK; SimilarityFloor = floor
                          Vision = vision; Transcriber = transcriber }
                    processMessage deps req.Id req.ChatJid
                    |> ProcessMessageHandler.toHttp
                with ex ->
                    Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore
```

- [ ] **Step 10: Thread the transcriber through `/messages/process-since`**

In `src/Nameless.TaskList/Program.fs`, change the `/messages/process-since` handler the same way (type list line 74, lambda params line 75, deps record line 85). After the edit:

```fsharp
        app.MapPost("/messages/process-since", System.Func<ProcessSinceRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, ITranscriber, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessSinceRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) ->
                match System.DateTime.TryParse(req.Since) with
                | false, _ -> Results.Json({| error = "invalid or missing 'since' date" |}, statusCode = 400)
                | true, since ->
                    let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
                    let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
                    let deps =
                        { Messages = messages; Vault = vault; Chat = chat
                          Model = cfg.["Ollama:Model"]; Embedder = embedder; TopK = topK; SimilarityFloor = floor
                          Vision = vision; Transcriber = transcriber }
                    let processOne id jid = processMessage deps id jid
                    let chatJid = if System.String.IsNullOrWhiteSpace req.ChatJid then None else Some req.ChatJid
                    BulkJobs.tryStart messages processOne since chatJid |> BulkHandler.startToHttp)) |> ignore
```

- [ ] **Step 11: Add the `Whisper` config block**

In `src/Nameless.TaskList/appsettings.json`, add a `Whisper` line after the `Ollama` line:

```json
  "Ollama": { "Url": "http://localhost:11434", "Model": "gemma4:e4b", "EmbedModel": "nomic-embed-text", "VisionModel": "gemma3:latest" },
  "Whisper": { "Command": "whisper", "Model": "base", "Language": "", "TimeoutSeconds": 300 },
  "TopicMatch": { "TopK": 5, "SimilarityFloor": 0.5 },
```

- [ ] **Step 12: Build and run the full suite**

Run: `dotnet build`
Expected: 0 errors (all six `PipelineDeps` sites + both host handlers updated).

Run: `dotnet test`
Expected: PASS — the previous count (111) still green. The existing image/vision tests now exercise the shared `enrich` helper, validating the refactor; nothing else changed behaviour.

- [ ] **Step 13: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs src/Nameless.TaskList/Program.fs src/Nameless.TaskList/appsettings.json tests/Nameless.TaskList.Core.Tests/Fakes.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: wire voice-note transcription through the pipeline and host"
```

---

## Task 3: Audio pipeline tests

**Files:**
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (add an `audioMessage` helper + four tests, after the existing image tests — i.e. after the `GetMediaBytes=None falls back to noise` test, before the `// ── Hybrid embedding topic-match tests` comment)

**Interfaces:**
- Consumes: `Pipeline.processMessage`, `PipelineDeps` (with `Transcriber`), `FakeTranscriber`, `FakeVision`, `FakeEmbedder`, `FakeMessages`, `Responses.final`, `sampleMessage` — all already in scope in this test module.

- [ ] **Step 1: Write the failing audio tests**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, insert after the `GetMediaBytes=None falls back to noise` test (currently ending at line 311) and before the `// ── Hybrid embedding topic-match tests` comment (line 313):

```fsharp
let private audioMessage () : ChatMessage =
    { sampleMessage () with Content = ""; MediaType = "audio" }

[<Fact>]
let ``voice-note is transcribed and processed as that text`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"party rsvp request","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":["RSVP to the party"],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Birthday party"}"""
    let taskFile = Responses.final "---\ntype: Task\ntitle: RSVP to the party\nstatus: pending\npriority: medium\ncontext:\n  - family\n---\nrsvp"
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; taskFile; topicBody ])
    let messages = FakeMessages(Some(audioMessage ()), [| 9uy; 8uy; 7uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> "Please RSVP to Ethan's party on Saturday at 2pm") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    match Pipeline.processMessage d "M1" "jid" with
    | Processed(_, _) ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        Assert.Contains("Voice note (transcribed)", vault.Files.[msgKey])     // body header
        Assert.Contains("Please RSVP to Ethan's party", vault.Files.[msgKey]) // transcript is the content
    | other -> failwithf "expected Processed, got %A" other

[<Fact>]
let ``voice-note falls back to noise when transcription fails`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(audioMessage ()), [| 1uy; 2uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> failwith "whisper down") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    // transcription throws -> content stays empty -> classify (scripted noise) -> ProcessedNoise, no crash
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")

[<Fact>]
let ``transcribed noise preserves the text`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"chitchat","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    let messages = FakeMessages(Some(audioMessage ()), [| 3uy; 2uy; 1uy |]) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> "just saying hi, talk later") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    match Pipeline.processMessage d "M1" "jid" with
    | ProcessedNoise ->
        let msgKey = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith("messages/"))
        let msgContent = vault.Files.[msgKey]
        Assert.Contains("Voice note (transcribed)", msgContent)  // header preserved
        Assert.Contains("just saying hi, talk later", msgContent) // transcript preserved
    | other -> failwithf "expected ProcessedNoise, got %A" other

[<Fact>]
let ``audio GetMediaBytes=None falls back to noise`` () =
    let vault = FakeVault()
    let noise = Responses.final """{"noise":true,"noise_reason":"empty","contexts":[],"intent":null,"action_required":false,"urgency":"none","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ noise ])
    // audio message but NO stored media bytes (None) -> transcriber must not be called
    let messages = FakeMessages(Some(audioMessage ())) :> IMessageSource
    let transcriber = FakeTranscriber(fun _ -> failwith "should not be called") :> ITranscriber
    let d = { Messages = messages; Vault = vault :> IVault; Chat = chat; Model = "test-model"
              Embedder = FakeEmbedder(fun _ -> failwith "no embedder") :> IEmbedder; TopK = 5; SimilarityFloor = 0.5
              Vision = FakeVision(fun _ -> failwith "no vision") :> IVision
              Transcriber = transcriber }
    Assert.Equal(ProcessedNoise, Pipeline.processMessage d "M1" "jid")
```

- [ ] **Step 2: Run the audio tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS — all `PipelineTests` including the four new audio tests.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: PASS — 115 tests (the prior 111 + 4 audio). `dotnet build` clean.

- [ ] **Step 4: Commit**

```bash
git add tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "test: voice-note pipeline path (transcribe, fallbacks, noise preservation)"
```

---

## Self-Review

**1. Spec coverage:**
- §3.1 `ITranscriber` → Task 1 Step 1. ✅
- §3.2 `WhisperTranscriber` (temp `.ogg`, `Process.Start`, timeout/kill, non-zero-exit raise, read `.txt`, `finally` cleanup) → Task 1 Step 4. ✅
- §3.3 pure `WhisperArgs.build` (+ `--language` only when set) → Task 1 Steps 2-4. ✅
- §4 `PipelineDeps.Transcriber`, shared `enrich`, two calls, three-way header on both paths → Task 2 Steps 2-5. ✅
- §5 config + DI + both handlers → Task 2 Steps 8-11. ✅
- §6 best-effort/idempotency (after the existing guard; `try/with`; temp cleanup) → adapter `finally` (Task 1) + `enrich` `try/with` (Task 2 Step 3). ✅
- §7 arg-builder tests + audio path (success, throw-fallback, none-fallback, noise-preservation) + non-audio untouched (throwing default fakes) → Task 1 Steps 2-3, Task 3. ✅
- §8 ripple note (every deps site; `IMessageSource` not extended) → Task 2 Steps 6-7, File Structure note. ✅

**2. Placeholder scan:** none — every code step has complete code and exact commands.

**3. Type consistency:** `Transcribe : byte array -> string`, `WhisperArgs.build : string -> string -> string -> string -> string list`, `WhisperTranscriber(command, model, language, timeoutSeconds)`, `PipelineDeps.Transcriber: ITranscriber`, header strings, and config keys are identical across the spec, the adapter, the DI, and the tests. The four-arg Whisper ctor matches between Task 1 (definition), Task 2 Step 8 (DI), and is not constructed in tests (fakes used). `enrich` returns `bool * ChatMessage` and is consumed as `imageDerived, msg` / `audioDerived, msg`. ✅
