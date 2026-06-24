# Live-Service Integration Harness — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning
> **Scope:** An opt-in test suite that exercises the real adapters (Postgres, Ollama chat/embed/vision, Whisper CLI, filesystem vault) and one end-to-end `processMessage`, gated so the default `dotnet test` is unaffected and each test skips when its service is absent.
> **Builds on:** all shipped adapters (`Adapters.fs`), the ports (`Ports.fs`), and `Pipeline.processMessage`.

---

## 1. Goal

The unit suite proves the **logic** behind the ports with in-memory fakes; nothing proves the **wiring** to the real services. Every manual smoke run so far has surfaced bugs precisely in the untested wire paths — the `private`-record `{}` serialization bugs (request bodies collapsing to `{}`) and the entity-field backfill bug (pipeline logic against real LLM output). This harness closes that gap permanently: a small, opt-in suite that drives the real `PostgresMessageSource`, `OllamaChatClient`/`OllamaEmbedder`/`OllamaVision`, `WhisperTranscriber`, and `FileSystemVault`, plus one end-to-end `processMessage`. It runs only when explicitly invoked, skips per-service when a dependency is down, and never touches production data destructively.

---

## 2. Scope

### In scope
- A new test project `tests/Nameless.TaskList.IntegrationTests` referencing `Nameless.TaskList.Core` only.
- In the solution (`.slnx`) for build/compile coverage, but excluded from the default `dotnet test` via a conditional `IsTestProject` flag; run on demand with `-p:Integration=true`.
- A `ServiceProbes` module (cheap, cached availability checks) + `Xunit.SkippableFact` gating.
- Config loaded from the host's `appsettings.json` + `appsettings.Development.json` + environment variables.
- Six adapter wire tests (Postgres, Ollama chat, Ollama embed, Ollama vision, Whisper, vault) + one end-to-end `processMessage` into a temp vault.
- Assertions are loose/structural (no exact LLM-output matches).

### Out of scope (later / unchanged)
- **Standing up CI (GitHub Actions).** None exists today; service-container orchestration is a separate subsystem. The harness is delivered CI-ready — a future CI job is just `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true`.
- No new production code, no new SQL queries, no new ports. The harness exercises existing adapters/ports only.
- The unit suites (`Nameless.TaskList.Core.Tests`, `Nameless.TaskList.Tests`) and `dotnet test` default behavior are **unchanged**.
- No load/perf testing, no multi-row data validation — one representative call per adapter.

---

## 3. Project & run model

### 3.1 Project
`tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj`:
- `TargetFramework` `net10.0`; `IsPackable` false.
- `ProjectReference` → `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (adapters + pipeline live in Core; the host is not referenced).
- Packages: `Microsoft.NET.Test.Sdk` 17.12.0, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2 (matching the existing test projects), and `Xunit.SkippableFact` 1.5.23 for per-test dynamic skipping. Config is read directly from the host's `appsettings*.json` with `System.Text.Json` (BCL) — no configuration packages. Npgsql comes transitively via the Core `ProjectReference` (the probe uses `Npgsql.NpgsqlConnectionStringBuilder`/`NpgsqlConnection`); `System.Net.Http` and `System.Text.Json` are in the shared framework.
- Added to `Nameless.TaskList.slnx` under the `/tests/` folder.

### 3.2 Exclusion from default `dotnet test`
In the `.fsproj`:
```xml
<!-- Excluded from the default solution test run (dotnet test). Build always compiles it
     (catches adapter-signature drift); run the live suite on demand with -p:Integration=true. -->
<IsTestProject Condition="'$(Integration)' != 'true'">false</IsTestProject>
```
- `dotnet test` (resolves `Nameless.TaskList.slnx`) → `Integration` unset → `IsTestProject=false` → VSTest skips discovery → default run unchanged (only the two unit projects run).
- `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true` → `IsTestProject=true` → the live suite runs.
- `dotnet build` (solution) compiles the project regardless of the flag → compile coverage stands.

### 3.3 Parallelism
A file (e.g. `AssemblyInfo.fs`) sets:
```fsharp
[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()
```
so live calls run sequentially (Ollama serves one model call at a time; vision is ~60s).

---

## 4. Service probes + skip gating

A `ServiceProbes` module exposes cached booleans (computed once via `lazy`):
- `postgres : bool` — build the connection string (§5); if absent → false. Else open an `NpgsqlConnection` with a short timeout (`Timeout=3`); true on success, false on any exception.
- `ollama : bool` — `GET {ollamaUrl}/api/tags` with a ~3s timeout; true on 2xx, false otherwise.
- `whisper : bool` — resolve the configured whisper command and `ffmpeg` on PATH (probe by attempting to start `whisper --help` / `ffmpeg -version` and catching `Win32Exception`/file-not-found); true if both resolve.
- `vault : bool` — always true.

Each live-service test is marked `[<SkippableFact>]` (from `Xunit.SkippableFact`) and begins with `Skip.IfNot(ServiceProbes.<svc>.Value, "<svc> not reachable")`; when no suitable real row exists, `Skip.If(true, "<reason>")`. Media tests (vision, whisper) require both their compute service **and** Postgres (they discover real rows), so they guard on both. The vault round-trip test (no live service needed) uses a plain `[<Fact>]`.

---

## 5. Config & safety

### 5.1 Config
A `Config` module builds an `IConfigurationRoot` once:
```
ConfigurationBuilder()
  .AddJsonFile(<hostDir>/appsettings.json, optional=true)
  .AddJsonFile(<hostDir>/appsettings.Development.json, optional=true)
  .AddEnvironmentVariables()
  .Build()
```
`<hostDir>` is resolved from the test assembly location up to the repo and into `src/Nameless.TaskList` (a helper walks up from `AppContext.BaseDirectory` to the directory containing `Nameless.TaskList.slnx`, then into `src/Nameless.TaskList`). Exposes: `connectionString` (`ConnectionStrings:WhatsApp`, may be null → Postgres skips), `ollamaUrl` (`Ollama:Url`, default `http://localhost:11434`), `chatModel` (`Ollama:Model`), `embedModel` (`Ollama:EmbedModel`, default `nomic-embed-text`), `visionModel` (`Ollama:VisionModel`, default `gemma3:latest`), `whisperCommand`/`whisperModel`/`whisperLanguage`/`whisperTimeout` (`Whisper:*`, same defaults as the host).

### 5.2 Safety (hard rules)
- **Postgres: read-only.** The harness only calls the existing `SELECT`-based query methods. It never writes/deletes in the WhatsApp DB.
- **Vault: temp dir only.** Any `FileSystemVault` is rooted at a fresh `Path.Combine(Path.GetTempPath(), "ntl-it-" + Guid)`, created per test and deleted in a `try/finally`. The real Obsidian vault (`Vault:Root`) is never used.
- Ollama/Whisper are compute-only (no persistence). Whisper writes only into its own temp working dir (the adapter already cleans up).

---

## 6. Tests

All assertions are structural/loose — they must not depend on specific model output. A shared helper discovers a real message of a given media type:
`firstMessageWith (predicate: ChatMessage -> bool)` = `(PostgresMessageSource cs :> IMessageSource).GetMessagesSince(None, DateTime.MinValue) |> List.tryFind predicate`.

1. **Postgres returns rows** — `GetMessagesSince(None, MinValue)` returns a non-empty list; the first item has a non-null `Id` and `ChatJid`.
2. **Ollama chat round-trips** — `OllamaChatClient(http, ollamaUrl, chatModel)`; call `Agent.runConversation chat [] "You are a test." "Reply with the single word OK."` (no tools — the real path the pipeline uses). Assert the returned reply string is non-null/non-empty. Getting a 2xx + parseable reply proves the request body wasn't the `{}` regression.
3. **Ollama embed returns a 768-vector** — `OllamaEmbedder(http, ollamaUrl, embedModel).Embed("integration test sentence")` returns an array of length 768 with all finite values.
4. **Ollama vision describes an image** — discover an `image` message with bytes (`firstMessageWith (fun m -> m.MediaType = "image")` then `GetMediaBytes` `Some`); `OllamaVision(http, ollamaUrl, visionModel).Describe bytes` returns a non-empty trimmed string. Skip if Postgres down or no such row.
5. **Whisper transcribes audio** — discover an `audio` message with bytes the same way; `WhisperTranscriber(whisperCommand, whisperModel, whisperLanguage, whisperTimeout).Transcribe bytes` returns a non-empty trimmed string. Skip if Postgres/whisper down or no such row.
6. **Vault round-trips** — `FileSystemVault(tempRoot)`: `Write("a/b.md", content)`, then `Exists` true, `Read` equals content, `ListFilesRecursive("a")` contains `a/b.md`. Temp root deleted in `finally`.
7. **End-to-end `processMessage`** — pick a real message id (`firstMessageWith (fun _ -> true)`), build `PipelineDeps` with the live `PostgresMessageSource`, `OllamaChatClient`, `OllamaEmbedder`, `OllamaVision`, `WhisperTranscriber`, and a `FileSystemVault` over a temp dir; call `Pipeline.processMessage deps id chatJid`. Assert the result is not `LlmError` and not `NotFound`, and that a `messages/…` file exists in the temp vault. (A first-time process of an unseen message into an empty temp vault yields `Processed`/`ProcessedNoise`, never `Skipped`.) Temp vault deleted in `finally`. Requires all services up.

---

## 7. Error handling & determinism

- Every test self-skips (not fails) when its prerequisite service(s) are unavailable, via `Skip.IfNot`.
- A test for which no suitable real row exists (e.g. no image/audio with bytes) skips with a clear reason rather than failing.
- Assertions are loose: presence/shape/length/non-emptiness, never exact text. The embed dimension (768) is the one fixed structural value (nomic-embed-text).
- Temp directories (vault roots) are always removed in `finally`; Postgres access is read-only; no production side effects.
- Sequential execution (parallelism disabled) keeps Ollama load and runtime predictable.

---

## 8. Files touched

- Create: `tests/Nameless.TaskList.IntegrationTests/Nameless.TaskList.IntegrationTests.fsproj`
- Create: `tests/Nameless.TaskList.IntegrationTests/AssemblyInfo.fs` (disable parallelization)
- Create: `tests/Nameless.TaskList.IntegrationTests/Support.fs` (`Config`, `ServiceProbes`, temp-vault + row-discovery helpers)
- Create: `tests/Nameless.TaskList.IntegrationTests/AdapterIntegrationTests.fs` (the 6 wire tests)
- Create: `tests/Nameless.TaskList.IntegrationTests/PipelineIntegrationTests.fs` (the 1 end-to-end test)
- Modify: `Nameless.TaskList.slnx` (add the project under `/tests/`)
- Modify: `CLAUDE.md` (document the opt-in command + that the default `dotnet test` is unchanged)

> No `src/` changes. The harness is purely additive test code.

---

## 9. Open follow-ups (later)

1. Stand up CI (GitHub Actions) with the opt-in command, once service availability in CI is decided (self-hosted runner with the services, or service containers + a CI Ollama/whisper).
2. Optionally commit a tiny speech `.ogg` fixture so the whisper test can run without Postgres (decouples media tests from the DB).
3. Broaden coverage if a future bug pattern warrants (e.g. a topic-match embedding round-trip, a digest against real LLM output).
