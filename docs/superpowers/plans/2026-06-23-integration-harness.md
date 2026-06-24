# Live-Service Integration Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An opt-in test project that drives the real adapters (Postgres read-only, Ollama chat/embed/vision, Whisper CLI, filesystem vault) plus one end-to-end `processMessage`, gated so the default `dotnet test` is unchanged and each test skips when its service is absent.

**Architecture:** A new `tests/Nameless.TaskList.IntegrationTests` project referencing only `Nameless.TaskList.Core`. It is in the solution (so `dotnet build` compiles it — catching adapter drift) but excluded from default `dotnet test` via a conditional `IsTestProject` flag; it runs on demand with `-p:Integration=true`. Per-service availability is probed at runtime and gated with xUnit 2.9.2's built-in `Assert.SkipUnless`/`Assert.Skip`. Config is read from the host's `appsettings*.json` with `System.Text.Json`. Vault writes go to temp dirs; Postgres is read-only.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2 (built-in dynamic skip — no third-party skip package), the existing Core adapters, Npgsql (transitive via Core), `System.Net.Http` + `System.Text.Json` (BCL).

## Global Constraints

- The default `dotnet test` MUST remain unchanged (the two unit projects only; live services NOT required). The integration project is excluded via `<IsTestProject Condition="'$(Integration)' != 'true'">false</IsTestProject>`.
- The live suite runs ONLY with `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true`.
- **Safety (hard):** Postgres access is read-only (existing `SELECT` query methods only — never write/delete the WhatsApp store). Any `FileSystemVault` is rooted at a fresh temp dir under `Path.GetTempPath()`, deleted in a `finally` — never the real `Vault:Root`.
- Assertions are loose/structural — no exact LLM-output matches. The only fixed structural value is the embedding dimension **768** (nomic-embed-text).
- Tests run sequentially (`DisableTestParallelization = true`) so Ollama isn't hit concurrently.
- No `src/` changes — the harness is purely additive test code (+ a `CLAUDE.md` doc line + the `.slnx` entry).
- Each test self-**skips** (never fails) when its prerequisite service(s) or a suitable real row are unavailable.

---

## File Structure

- `tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj` — project (conditional `IsTestProject`, Core reference, standard test packages).
- `tests/Nameless.TaskList.IntegrationTests/AssemblyInfo.fs` — disable test parallelization.
- `tests/Nameless.TaskList.IntegrationTests/Support.fs` — `Config` (read host appsettings), `ServiceProbes` (cached probes), `Helpers` (live message source, row discovery, temp-vault).
- `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs` — the vault round-trip test (Task 1) + the five live adapter wire tests (Task 2).
- `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` — the one end-to-end test (Task 3).
- `Nameless.TaskList.slnx` — add the project under `/tests/`.
- `CLAUDE.md` — document the opt-in command (Task 3).

Compile order in the `.fsproj`: `AssemblyInfo.fs`, `Support.fs`, `AdapterIntegrationTests.fs`, `PipelineIntegrationTests.fs`.

---

## Task 1: Project scaffold + Support module + vault round-trip test

**Files:**
- Create: `tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj`
- Create: `tests/Nameless.TaskList.IntegrationTests/AssemblyInfo.fs`
- Create: `tests/Nameless.TaskList.IntegrationTests/Support.fs`
- Create: `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs`
- Modify: `Nameless.TaskList.slnx`

**Interfaces:**
- Consumes: `Nameless.TaskList.Core.ChatMessage`; `Nameless.TaskList.Core.Adapters.{FileSystemVault, PostgresMessageSource}`; `Nameless.TaskList.Core.Ports.{IVault, IMessageSource}`.
- Produces: module `Nameless.TaskList.IntegrationTests.Support` exposing `Config.{connectionString, ollamaUrl, chatModel, embedModel, visionModel, whisperCommand, whisperModel, whisperLanguage, whisperTimeout}`, `ServiceProbes.{postgres, ollama, whisper}` (each `Lazy<bool>`), and `Helpers.{messages, firstMessageWith, firstWithMedia, withTempVault}`.

- [ ] **Step 1: Create the project file**

Create `tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <IsPackable>false</IsPackable>
        <!-- Excluded from the default solution test run (dotnet test). dotnet build still
             compiles it (catches adapter-signature drift); run the live suite on demand with
             dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true -->
        <IsTestProject Condition="'$(Integration)' != 'true'">false</IsTestProject>
    </PropertyGroup>

    <ItemGroup>
        <Compile Include="AssemblyInfo.fs" />
        <Compile Include="Support.fs" />
        <Compile Include="AdapterIntegrationTests.fs" />
        <Compile Include="PipelineIntegrationTests.fs" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
        <PackageReference Include="xunit" Version="2.9.2" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\src\Nameless.TaskList.Core\Nameless.TaskList.Core.fsproj" />
    </ItemGroup>

</Project>
```

Note: `PipelineIntegrationTests.fs` is listed in compile order now but is created in Task 3. To keep Task 1 building, create a placeholder in the next step.

- [ ] **Step 2: Create the parallelization-off assembly attribute + a placeholder pipeline file**

Create `tests/Nameless.TaskList.IntegrationTests/AssemblyInfo.fs`:

```fsharp
module Nameless.TaskList.IntegrationTests.AssemblyInfo

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()
```

Create `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` as a placeholder (Task 3 fills it):

```fsharp
module Nameless.TaskList.IntegrationTests.PipelineIntegrationTests
```

- [ ] **Step 3: Create the Support module**

Create `tests/Nameless.TaskList.IntegrationTests/Support.fs`:

```fsharp
module Nameless.TaskList.IntegrationTests.Support

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters

module Config =
    // Walk up from the test assembly to the directory holding the solution file, then into the host.
    let rec private findRoot (dir: string) =
        if isNull dir then failwith "could not locate Nameless.TaskList.slnx above the test assembly"
        elif File.Exists(Path.Combine(dir, "Nameless.TaskList.slnx")) then dir
        else findRoot (Path.GetDirectoryName dir)

    let repoRoot = findRoot AppContext.BaseDirectory
    let private hostDir = Path.Combine(repoRoot, "src", "Nameless.TaskList")

    let private load (file: string) =
        let p = Path.Combine(hostDir, file)
        if File.Exists p then Some(JsonDocument.Parse(File.ReadAllText p)) else None

    let private baseDoc = load "appsettings.json"
    let private devDoc = load "appsettings.Development.json"

    let private get (doc: JsonDocument option) (section: string) (key: string) =
        match doc with
        | None -> null
        | Some d ->
            match d.RootElement.TryGetProperty section with
            | true, s ->
                match s.TryGetProperty key with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> null
            | _ -> null

    let private orDefault (d: string) (v: string) = if String.IsNullOrWhiteSpace v then d else v

    let connectionString =
        [ get devDoc "ConnectionStrings" "WhatsApp"
          get baseDoc "ConnectionStrings" "WhatsApp"
          Environment.GetEnvironmentVariable "WHATSAPP_CONNSTRING" ]
        |> List.tryFind (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Option.defaultValue null

    let ollamaUrl    = get baseDoc "Ollama" "Url"         |> orDefault "http://localhost:11434"
    let chatModel    = get baseDoc "Ollama" "Model"       |> orDefault "gemma4:e4b"
    let embedModel   = get baseDoc "Ollama" "EmbedModel"  |> orDefault "nomic-embed-text"
    let visionModel  = get baseDoc "Ollama" "VisionModel" |> orDefault "gemma3:latest"
    let whisperCommand  = get baseDoc "Whisper" "Command"  |> orDefault "whisper"
    let whisperModel    = get baseDoc "Whisper" "Model"    |> orDefault "base"
    let whisperLanguage = (get baseDoc "Whisper" "Language" : string)   // "" allowed (auto-detect)
    let whisperTimeout  =
        match Int32.TryParse(get baseDoc "Whisper" "TimeoutSeconds") with
        | true, v -> v
        | _ -> 300

module ServiceProbes =
    let postgres : Lazy<bool> =
        lazy (
            match Config.connectionString with
            | cs when String.IsNullOrWhiteSpace cs -> false
            | cs ->
                try
                    let b = Npgsql.NpgsqlConnectionStringBuilder(cs)
                    b.Timeout <- 3
                    use c = new Npgsql.NpgsqlConnection(b.ConnectionString)
                    c.Open()
                    true
                with _ -> false)

    let ollama : Lazy<bool> =
        lazy (
            try
                use http = new HttpClient()
                http.Timeout <- TimeSpan.FromSeconds 3.0
                let r = http.GetAsync(Config.ollamaUrl.TrimEnd('/') + "/api/tags").Result
                r.IsSuccessStatusCode
            with _ -> false)

    let private canStart (cmd: string) (arg: string) =
        try
            let psi = Diagnostics.ProcessStartInfo(cmd, arg)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            use p = Diagnostics.Process.Start(psi)
            p.WaitForExit(15000) |> ignore
            true
        with _ -> false

    let whisper : Lazy<bool> =
        lazy (canStart Config.whisperCommand "--help" && canStart "ffmpeg" "-version")

module Helpers =
    let messages () : IMessageSource =
        PostgresMessageSource(Config.connectionString) :> IMessageSource

    let firstMessageWith (pred: ChatMessage -> bool) : ChatMessage option =
        (messages ()).GetMessagesSince(None, DateTime.MinValue) |> List.tryFind pred

    let firstWithMedia (mediaType: string) : (ChatMessage * byte array) option =
        let src = messages ()
        src.GetMessagesSince(None, DateTime.MinValue)
        |> List.filter (fun m -> m.MediaType = mediaType)
        |> List.tryPick (fun m ->
            match src.GetMediaBytes(m.Id, m.ChatJid) with
            | Some b -> Some(m, b)
            | None -> None)

    let withTempVault (f: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "ntl-it-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try f root
        finally (try Directory.Delete(root, true) with _ -> ())
```

- [ ] **Step 4: Create the vault round-trip test**

Create `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs`:

```fsharp
module Nameless.TaskList.IntegrationTests.AdapterIntegrationTests

open System
open Xunit
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.IntegrationTests.Support

// No live service needed — proves the harness executes under -p:Integration=true.
[<Fact>]
let ``FileSystemVault round-trips write, read, exists and list`` () =
    Helpers.withTempVault (fun root ->
        let vault = FileSystemVault(root) :> IVault
        vault.Write("topics/active/sample.md", "hello body")
        Assert.True(vault.Exists "topics/active/sample.md")
        Assert.Equal("hello body", vault.Read "topics/active/sample.md")
        Assert.Contains("topics/active/sample.md", vault.ListFilesRecursive "topics"))
```

- [ ] **Step 5: Register the project in the solution**

In `Nameless.TaskList.slnx`, add the project inside the existing `/tests/` folder block, after the `Nameless.TaskList.Tests` entry:

```xml
    <Project Path="tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj" />
```

- [ ] **Step 6: Verify the default `dotnet test` is unchanged (integration NOT run)**

Run: `dotnet test`
Expected: builds the whole solution (including the integration project) and runs ONLY the two unit projects — `Passed! ... Total: 108` (Core) and `Total: 10` (endpoint). The integration project's `FileSystemVault` test is NOT executed (no extra test project line in the output).

- [ ] **Step 7: Verify the opt-in suite runs and the vault test passes**

Run: `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true`
Expected: 1 test runs and passes (`FileSystemVault round-trips...`). PASS.

- [ ] **Step 8: Commit**

```bash
git add tests/Nameless.TaskList.IntegrationTests Nameless.TaskList.slnx
git commit -m "test: scaffold opt-in integration harness (project, probes, vault test)"
```

---

## Task 2: Live adapter wire tests (Postgres, Ollama chat/embed/vision, Whisper)

**Files:**
- Modify: `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs` (append five tests after the vault test)

**Interfaces:**
- Consumes: `Support.{Config, ServiceProbes, Helpers}` from Task 1; `Nameless.TaskList.Core.Agent.runConversation : IChatClient -> Tool list -> string -> string -> string`; `Adapters.{OllamaChatClient, OllamaEmbedder, OllamaVision, WhisperTranscriber}`; `Ports.{IChatClient, IEmbedder, IVision, ITranscriber}`.

- [ ] **Step 1: Add the five live adapter tests**

Append to `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs`. (Add `open Nameless.TaskList.Core` and `open System.Net.Http` at the top of the file, after the existing `open`s.)

```fsharp
[<Fact>]
let ``Postgres returns at least one well-formed message`` () =
    Assert.SkipUnless(ServiceProbes.postgres.Value, "Postgres not reachable")
    let rows = (Helpers.messages ()).GetMessagesSince(None, DateTime.MinValue)
    Assert.NotEmpty(rows)
    let first = List.head rows
    Assert.False(String.IsNullOrWhiteSpace first.Id)
    Assert.False(String.IsNullOrWhiteSpace first.ChatJid)

[<Fact>]
let ``Ollama chat returns a non-empty reply`` () =
    Assert.SkipUnless(ServiceProbes.ollama.Value, "Ollama not reachable")
    use http = new HttpClient()
    let chat = OllamaChatClient(http, Config.ollamaUrl, Config.chatModel) :> IChatClient
    let reply = Agent.runConversation chat [] "You are a test." "Reply with the single word OK."
    Assert.False(String.IsNullOrWhiteSpace reply)

[<Fact>]
let ``Ollama embed returns a 768-dim finite vector`` () =
    Assert.SkipUnless(ServiceProbes.ollama.Value, "Ollama not reachable")
    use http = new HttpClient()
    let embedder = OllamaEmbedder(http, Config.ollamaUrl, Config.embedModel) :> IEmbedder
    let v = embedder.Embed "integration test sentence"
    Assert.Equal(768, v.Length)
    Assert.All(v, fun x -> Assert.True(Double.IsFinite x))

[<Fact>]
let ``Ollama vision describes a real image message`` () =
    Assert.SkipUnless(ServiceProbes.postgres.Value, "Postgres not reachable")
    Assert.SkipUnless(ServiceProbes.ollama.Value, "Ollama not reachable")
    match Helpers.firstWithMedia "image" with
    | None -> Assert.Skip("no image message with stored bytes")
    | Some(_, bytes) ->
        use http = new HttpClient()
        let vision = OllamaVision(http, Config.ollamaUrl, Config.visionModel) :> IVision
        let text = vision.Describe bytes
        Assert.False(String.IsNullOrWhiteSpace text)

[<Fact>]
let ``Whisper transcribes a real audio message`` () =
    Assert.SkipUnless(ServiceProbes.postgres.Value, "Postgres not reachable")
    Assert.SkipUnless(ServiceProbes.whisper.Value, "whisper/ffmpeg not available")
    match Helpers.firstWithMedia "audio" with
    | None -> Assert.Skip("no audio message with stored bytes")
    | Some(_, bytes) ->
        let t =
            WhisperTranscriber(Config.whisperCommand, Config.whisperModel, Config.whisperLanguage, Config.whisperTimeout)
            :> ITranscriber
        let text = t.Transcribe bytes
        Assert.False(String.IsNullOrWhiteSpace text)
```

- [ ] **Step 2: Run the opt-in suite**

Run: `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true`
Expected (on a dev box with Postgres + Ollama + whisper up): all six tests run and PASS (1 vault + 5 live). On a machine missing a service, the corresponding tests report as **skipped**, not failed. The vision/whisper tests may take up to ~60s each — this is expected.

- [ ] **Step 3: Verify the default `dotnet test` is still unchanged**

Run: `dotnet test`
Expected: still only the two unit projects (108 Core + 10 endpoint); the integration tests are NOT executed.

- [ ] **Step 4: Commit**

```bash
git add tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs
git commit -m "test: live adapter wire tests (Postgres, Ollama chat/embed/vision, Whisper)"
```

---

## Task 3: End-to-end pipeline test + docs

**Files:**
- Modify: `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` (replace the placeholder)
- Modify: `CLAUDE.md` (document the opt-in command)

**Interfaces:**
- Consumes: `Support.{Config, ServiceProbes, Helpers}`; `Nameless.TaskList.Core.Pipeline.{PipelineDeps, PipelineResult, processMessage}`; all five live adapters; `Ports.*`.
- `PipelineDeps` fields (exact, in order): `Messages: IMessageSource; Vault: IVault; Chat: IChatClient; Model: string; Embedder: IEmbedder; TopK: int; SimilarityFloor: float; Vision: IVision; Transcriber: ITranscriber`.

- [ ] **Step 1: Replace the placeholder with the end-to-end test**

Replace the entire contents of `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` with:

```fsharp
module Nameless.TaskList.IntegrationTests.PipelineIntegrationTests

open System
open System.IO
open System.Net.Http
open Xunit
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.IntegrationTests.Support

[<Fact>]
let ``end-to-end processMessage against live services writes a message file`` () =
    Assert.SkipUnless(ServiceProbes.postgres.Value, "Postgres not reachable")
    Assert.SkipUnless(ServiceProbes.ollama.Value, "Ollama not reachable")
    match Helpers.firstMessageWith (fun _ -> true) with
    | None -> Assert.Skip("no messages in the store")
    | Some msg ->
        Helpers.withTempVault (fun root ->
            use http = new HttpClient()
            let deps =
                { Messages = Helpers.messages ()
                  Vault = FileSystemVault(root) :> IVault
                  Chat = OllamaChatClient(http, Config.ollamaUrl, Config.chatModel) :> IChatClient
                  Model = Config.chatModel
                  Embedder = OllamaEmbedder(http, Config.ollamaUrl, Config.embedModel) :> IEmbedder
                  TopK = 5
                  SimilarityFloor = 0.5
                  Vision = OllamaVision(http, Config.ollamaUrl, Config.visionModel) :> IVision
                  Transcriber =
                    WhisperTranscriber(Config.whisperCommand, Config.whisperModel, Config.whisperLanguage, Config.whisperTimeout)
                    :> ITranscriber }
            match Pipeline.processMessage deps msg.Id msg.ChatJid with
            | LlmError e -> failwithf "pipeline returned LlmError: %s" e
            | NotFound -> failwith "pipeline returned NotFound for a real message id"
            | _ -> ()
            let wroteMessageFile =
                Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
                |> Array.exists (fun p -> p.Replace('\\', '/').Contains("/messages/"))
            Assert.True(wroteMessageFile, "expected a messages/ file to be written"))
```

- [ ] **Step 2: Run the opt-in suite (all seven tests)**

Run: `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true`
Expected (services up): 7 tests run and PASS (1 vault + 5 adapter + 1 end-to-end). The end-to-end may take tens of seconds (multiple live LLM calls). On a machine missing a service, the live tests skip.

- [ ] **Step 3: Document the opt-in command in CLAUDE.md**

In `CLAUDE.md`, under the `## Commands` section, after the `dotnet test` lines, add:

````markdown
# Run the opt-in live-service integration suite (real Postgres/Ollama/Whisper; each test skips when its service is absent).
# Excluded from the default `dotnet test`; requires the -p:Integration=true flag.
dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true
````

- [ ] **Step 4: Final verification — default run unchanged, build clean**

Run: `dotnet build`
Expected: 0 errors (the integration project compiles as part of the solution).

Run: `dotnet test`
Expected: unchanged — 108 Core + 10 endpoint, integration not executed.

- [ ] **Step 5: Commit**

```bash
git add tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs CLAUDE.md
git commit -m "test: end-to-end processMessage integration test + document opt-in suite"
```

---

## Self-Review

**1. Spec coverage:**
- §3.1 project (Core ref, packages, slnx) → Task 1 Steps 1, 5. ✅
- §3.2 conditional `IsTestProject` exclusion → Task 1 Step 1 + verified in Steps 6-7. ✅
- §3.3 parallelism off → Task 1 Step 2. ✅
- §4 `ServiceProbes` (postgres/ollama/whisper, cached via `lazy`) + `Assert.SkipUnless` gating → Task 1 Step 3, used in Tasks 2-3. ✅
- §5.1 config from host appsettings via System.Text.Json → Task 1 Step 3 (`Config`). ✅
- §5.2 safety (Postgres read-only; temp-dir vault; cleanup) → `Helpers.withTempVault` (Task 1) + read-only query usage (Tasks 2-3). ✅
- §6 tests 1-6 (Postgres, chat, embed-768, vision, whisper, vault) → Task 1 Step 4 (vault) + Task 2. ✅
- §6 test 7 (end-to-end) → Task 3. ✅
- §7 determinism/skip/cleanup → `Assert.Skip`/`SkipUnless` + `finally` cleanup throughout. ✅
- §8 files + CLAUDE.md doc → Task 3 Step 3. ✅

**2. Placeholder scan:** The `PipelineIntegrationTests.fs` "placeholder" in Task 1 Step 2 is an intentional, valid empty F# module (so the `.fsproj` compile list resolves before Task 3 fills it), not a content placeholder. Every code step contains complete code; no TBD/"handle errors"/"similar to" left.

**3. Type consistency:** `ServiceProbes.<svc>` are `Lazy<bool>` → accessed as `.Value` at every call site. `Helpers.firstWithMedia` returns `(ChatMessage * byte array) option` → matched as `Some(_, bytes)`. `firstMessageWith` returns `ChatMessage option` → matched as `Some msg`. `PipelineDeps` field set and order match `Pipeline.fs` exactly (incl. `Transcriber`). `Agent.runConversation chat [] system user` — `[]` infers `Tool list`. `Config.*` names match between `Support.fs` (definition) and the test call sites. Adapter constructor arities match `Adapters.fs`: `OllamaChatClient/Embedder/Vision(http, url, model)`, `WhisperTranscriber(command, model, language, timeout)`, `PostgresMessageSource(connStr)`, `FileSystemVault(root)`. ✅
