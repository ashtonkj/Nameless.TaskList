# Bulk Reprocess From Date — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning
> **Scope:** Re-run the existing per-message pipeline over all messages since a given date, as a background job, so the knowledge base can be rebuilt/backfilled. The other new roadmap item (image-only message processing) is separate.
> **Builds on:** the per-message pipeline (`processMessage`) and the existing HTTP host.

---

## 1. Goal

Provide a way to (re)build the knowledge base by feeding every message since a chosen start date back through the existing `Pipeline.processMessage`, in chronological order so topics accumulate correctly. Because each message can take tens of seconds (multiple local-LLM calls), a full rebuild may run for hours — so it runs as a **background job** triggered by an HTTP endpoint, with a status endpoint for progress. The per-message idempotency guard (one file per message) makes the run safe to re-run and resume.

---

## 2. Scope

### In scope
- A `GetMessagesSince` SQL query + `IMessageSource` method (ascending by timestamp; optional single chat; excludes WhatsApp `status%` broadcasts).
- A Core `BulkProcessor.runSince` that iterates the since-list in order, calls an injected per-message processor, tallies outcomes, and reports progress.
- A web-host background-job layer: `POST /messages/process-since` (start, returns a job id) and `GET /messages/process-since/{jobId}` (progress). One job at a time; in-memory job state.
- "Every message" is fed through the pipeline (no text/media filter) — the classifier decides noise; media-only messages are processed as-is.
- Unit tests with in-memory fakes for the runner, the job registry's one-at-a-time rule, and the endpoint mapping.

### Out of scope (later / unchanged)
- Image-only / media message understanding (separate roadmap item).
- Job persistence across host restarts (state is in-memory).
- Parallelism (sequential by design — topic accumulation depends on order).
- Cancellation of a running job.
- Live-DB integration tests for the new query (consistent with the project's no-live-service test stance).
- `Pipeline.processMessage`, `PipelineResult`, `/messages/process`, `/reindex`, the digests, and all Core pipeline logic are **unchanged**.

---

## 3. Query + port (`Library.fs`, `Ports.fs`, `Adapters.fs`)

### 3.1 SQL
`Queries.GetMessagesSince` selects the same columns as the existing message queries, with:
```
WHERE chat_jid NOT LIKE 'status%'
  AND m.timestamp >= @Since
  AND (@ChatJid IS NULL OR m.chat_jid = @ChatJid)
ORDER BY m.timestamp ASC
```
(No `LIMIT` — a rebuild needs all of them; ascending so topics accrue in order.)

### 3.2 Port
```
IMessageSource.GetMessagesSince : chatJid: string option * since: System.DateTime -> ChatMessage list
```
`PostgresMessageSource` maps `None` to a `DBNull` `@ChatJid` parameter and `Some jid` to the value; materializes the rows into a `ChatMessage list` via the existing reader mapper. (Personal-scale volumes; a list is acceptable.)

---

## 4. Batch runner (`BulkProcessor.fs`, Core)

Depends only on `IMessageSource`, `Pipeline.PipelineResult`, and an injected per-message processor. Compiled after `Pipeline.fs`.

```
type BulkProgress =
    { Total: int
      Processed: int
      Noise: int
      Skipped: int
      Errors: int
      Done: bool
      Error: string }          // job-level failure message; "" when none

runSince :
    messages: IMessageSource ->
    processOne: (string -> string -> Pipeline.PipelineResult) ->
    since: System.DateTime ->
    chatJid: string option ->
    onProgress: (BulkProgress -> unit) ->
    BulkProgress
```

Behavior:
1. Fetch `messages.GetMessagesSince(chatJid, since)`; `Total` = its length.
2. Iterate in the returned (ascending) order. For each, call `processOne msg.Id msg.ChatJid` inside `try/with`:
   - exception → `Errors+1` (continue);
   - `Processed _` → `Processed+1`; `ProcessedNoise` → `Noise+1`; `Skipped` → `Skipped+1`; `LlmError _` or `NotFound` → `Errors+1`.
3. After each message, call `onProgress current`.
4. Return the final `BulkProgress` with `Done = true`.

`processOne` is **injected** so the runner is unit-testable with a stub (no real LLM); the host wires it to `fun id jid -> Pipeline.processMessage deps id jid`.

---

## 5. Background job + endpoints (web host)

### 5.1 `BulkJobs` module (registry)
A `System.Collections.Concurrent.ConcurrentDictionary<string, BulkProgress>` keyed by job id.
- `tryStart : IMessageSource -> (string -> string -> PipelineResult) -> System.DateTime -> string option -> Result<string, string>`
  - If any entry has `Done = false`, return `Error "a bulk job is already running"`.
  - Else mint a GUID job id, seed an initial `BulkProgress` (`Done = false`), spawn a background `Task` that runs `BulkProcessor.runSince` with `onProgress` replacing the dict entry after each message — wrapped in `try/with` so a job-level exception sets `Done = true` and `Error = ex.Message`. Return `Ok jobId`.
- `get : string -> BulkProgress option`.

Whole-record replacement on each update keeps reads (GET) and the task's writes race-free.

### 5.2 Endpoints (`Program.fs`)
- `POST /messages/process-since` — body `{ since: string; chatJid: string }` (`chatJid` null/empty → all chats). Parse `since` as a date; invalid/missing → `400`. Build `PipelineDeps` once from the root services + config (identical to the `/messages/process` handler: `Embedder`, `TopK`, `SimilarityFloor` included), set `processOne = fun id jid -> Pipeline.processMessage deps id jid`, call `BulkJobs.tryStart`. `Ok jobId` → `202 { jobId }`; `Error msg` → `409 { error = msg }`.
- `GET /messages/process-since/{jobId}` — `BulkJobs.get jobId` → `200` with the `BulkProgress`; `None` → `404`.

The background task resolves singletons from the root `app.Services` at start (shared `HttpClient`, `IMessageSource`, `IVault`, `IChatClient`, `IEmbedder`) and processes sequentially.

`DigestHandler`-style `toHttp` helpers map `BulkProgress`/the start result to `IResult`.

---

## 6. Error handling & idempotency
- `400` invalid `since`; `409` job already running; `404` unknown job id.
- Per-message exception / `LlmError` / `NotFound` → counted as `Errors`, run continues.
- Job-level exception → job ends `Done = true` with an `Error` message.
- `Skipped` (already-processed) makes the run resumable; no duplicate writes.
- No new vault writes beyond `processMessage`'s own; never deletes.

---

## 7. Testing (unit, in-memory fakes — no live services)
- `BulkProcessor.runSince`:
  - processes messages in the source's (ascending) order — a `processOne` stub records the ids it received; assert order;
  - tallies correct counts for a mix of `Processed`/`ProcessedNoise`/`Skipped`/`LlmError` results;
  - a `processOne` that throws on one message → `Errors+1` and the rest still process;
  - `onProgress` is invoked once per message; final `Done = true` and `Total` matches.
- `BulkJobs`: pre-seed the dict with a `Done = false` entry → `tryStart` returns `Error` (one-at-a-time rule); `get` returns the seeded progress and `None` for an unknown id. (Deterministic — no reliance on background-task timing.)
- Endpoint mapping: `BulkProgress` → `200` JSON; unknown job id → `404`; the start-result mapping (202 vs 409). Uses the existing `statusOf`-style harness.
- The new `GetMessagesSince` is exercised only through `FakeMessages` in tests; the Npgsql adapter is not unit-tested (needs a live DB).

---

## 8. Files touched
- `src/Nameless.TaskList.Core/Library.fs` — `GetMessagesSince` query.
- `src/Nameless.TaskList.Core/Ports.fs` — `IMessageSource.GetMessagesSince`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `PostgresMessageSource.GetMessagesSince`.
- `src/Nameless.TaskList.Core/BulkProcessor.fs` — new (`BulkProgress`, `runSince`); register after `Pipeline.fs`.
- `src/Nameless.TaskList/ProcessMessage.fs` — `BulkJobs` module + request DTO + `toHttp` mapping.
- `src/Nameless.TaskList/Program.fs` — the two routes.
- `tests/Nameless.TaskList.Core.Tests/` — `BulkProcessorTests.fs`; add `GetMessagesSince` to every `FakeMessages`.
- `tests/Nameless.TaskList.Tests/` — `BulkJobs`/endpoint mapping tests.

> **Port-extension ripple:** adding `GetMessagesSince` to `IMessageSource` breaks every implementer until updated — `PostgresMessageSource` and each test `FakeMessages` (same pattern as the `IVault.ListFilesRecursive` addition). The build stays green only once all are updated together.

---

## 9. Open follow-ups (later)
1. Image-only message processing (the other roadmap item) — then a bulk re-run backfills media messages.
2. Persist job state across restarts; add cancellation.
3. Throughput: today each message is fully sequential; revisit if rebuilds are too slow.
