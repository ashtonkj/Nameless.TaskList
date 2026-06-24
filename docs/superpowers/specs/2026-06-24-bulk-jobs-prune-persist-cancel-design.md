# BulkJobs: prune + persist + cancel — Design

Date: 2026-06-24

## Problem

The bulk reprocess registry (`POST /messages/process-since`, `GET /messages/process-since/{jobId}`) tracks jobs in an in-memory `ConcurrentDictionary<string, BulkProgress>` in `src/Nameless.TaskList/ProcessMessage.fs`. Three gaps:

1. **No pruning** — completed jobs are never removed; the dictionary grows unbounded.
2. **No persistence** — all job state is lost on host restart, including a job that was mid-run.
3. **No cancellation** — a running job cannot be stopped; the only exit is completion or a host kill.

This increment addresses all three.

## Decisions

- **Scope:** prune + persist + cancel, all in this increment.
- **Storage:** a single JSON file on disk (rewrite-on-update), consistent with the "files are the database" ethos. The Postgres DB belongs to the WhatsApp bridge and is read-only.
- **Restart behaviour:** auto-resume an interrupted (still-`running`) job on startup. Safe and cheap because `processMessage` is idempotent — already-written messages are skipped by the message-file-existence guard.
- **Status model:** a single `Status` string field replacing `Done: bool`.
- **Retention:** keep all running jobs plus the latest N (default 20) non-running, evicting the oldest by `StartedAt`.

## Data model (Core, `BulkProcessor.fs`)

Collapse `BulkProgress` into one durable record that serves as both the persisted unit and the HTTP response body:

```fsharp
type BulkJob =
  { JobId: string
    Since: System.DateTime
    ChatJid: string          // "" = all chats (mirrors the existing ProcessSinceRequest convention)
    StartedAt: System.DateTime
    Status: string           // "running" | "done" | "cancelled" | "interrupted" | "error"
    Total: int; Processed: int; Noise: int; Skipped: int; Errors: int
    Error: string }
```

- `Done: bool` is removed; status is now carried by `Status`.
- The record is **public** (not `private`) — a `private` record serializes to `{}` under YamlDotNet/STJ (see `fsharp-private-record-serialization` memory).
- `ChatJid` is a plain `string` (not `string option`) so it round-trips through System.Text.Json without special-casing; `""` means "all chats", matching the existing request convention where a blank `ChatJid` becomes `None`.

`isRunning` becomes:

```fsharp
let isRunning (jobs: BulkJob seq) : bool =
  jobs |> Seq.exists (fun j -> j.Status = "running")
```

## Pruning (pure, Core)

```fsharp
/// Retain every running job plus the most-recent `keep` non-running jobs (by StartedAt).
let prune (keep: int) (jobs: BulkJob list) : BulkJob list
```

Called after starting a job and after each job reaches a terminal status. Running jobs are never evicted. Retain count comes from config `BulkJobs:Retain` (default 20).

## Cancellation

- `runSince` gains a `CancellationToken` parameter. It checks `token.IsCancellationRequested` between messages; on a trip it sets `Status = "cancelled"` and stops cleanly, returning the partial counts accumulated so far. (A token already cancelled before the first message yields zero processed and status `cancelled`.)
- The web registry owns one `CancellationTokenSource` per running job.
- New endpoint `POST /messages/process-since/{jobId}/cancel`:
  - 200 if the job was running and cancellation was signalled,
  - 404 if the job id is unknown,
  - 409 if the job already reached a terminal status.

## Persistence (port + adapter)

- New port in `Core/Ports.fs`:

  ```fsharp
  type IJobStore =
    abstract member Save: BulkJob list -> unit
    abstract member Load: unit -> BulkJob list
  ```

- Adapter `FileSystemJobStore` in `Core/Adapters.fs`:
  - Serializes the full job list to a single JSON file with System.Text.Json (rewrite-on-update).
  - Path from config `BulkJobs:StatePath`, defaulting to `<Vault:Root>/.taskmeister/bulk-jobs.json`.
  - Creates the parent directory if missing.
  - `Load` is best-effort: a missing or corrupt file returns an empty list and never throws.

Keeping file I/O in an adapter preserves the convention that `Core` logic is pure and adapters perform I/O.

## Registry rework (web, `ProcessMessage.fs`)

Replace the static `BulkJobs` module with an injectable `BulkJobRegistry` singleton holding:

- a `ConcurrentDictionary<string, BulkJob>`,
- a `ConcurrentDictionary<string, CancellationTokenSource>`,
- an `IJobStore`,
- the retain count.

Operations:

- **`TryStart processOne since chatJid`** — under the existing one-at-a-time gate: if any job is running, return `Error "a bulk job is already running"`; otherwise create a `running` `BulkJob`, prune, `Save`, then `Task.Run` the `runSince` loop with a fresh `CancellationToken`. `onProgress` updates the dictionary and persists; reaching a terminal status persists and prunes.
- **`Get jobId`** — returns the `BulkJob option`.
- **`Cancel jobId`** — cancels the job's token if running; returns a `Result`/status the handler maps to 200/404/409.
- **`Resume processOne`** — on startup, any persisted job with `Status = "running"` is by definition interrupted (nothing is running yet). Re-run `runSince` from its `Since`/`ChatJid` under the **same** `JobId`; already-written messages tally as `Skipped` via the idempotency guard. If more than one persisted job is `running` (should not happen given the gate), resume the most recent and mark the rest `interrupted`.

## Startup wiring (`Program.fs`)

- Register `IJobStore` (→ `FileSystemJobStore`) and `BulkJobRegistry` as singletons.
- After `app.Build()`, resolve the adapter singletons (all adapters are already singletons), build the pipeline `deps` and a `processOne` closure (the same construction the `/messages/process-since` endpoint performs), and call `registry.Resume processOne` on a background task before `app.Run()`.
- Update the start/progress endpoints to delegate to the injected `BulkJobRegistry`; add the cancel endpoint.

## Testing

**Core unit tests (`tests/Nameless.TaskList.Core.Tests/BulkProcessorTests.fs`):**

- `runSince` with a token cancelled before the run → status `cancelled`, zero processed.
- `runSince` with a token cancelled after the first message → stops early, status `cancelled`, partial counts.
- `isRunning` keys off `Status`.
- `prune` keeps all running jobs plus the latest N non-running and evicts the oldest by `StartedAt`.
- Registry tests with an in-memory `FakeJobStore`: `TryStart` rejects a second concurrent job; `Get` returns progress; `Cancel` transitions a running job to `cancelled`; a resume cycle (seed the store with a `running` job → `Resume` re-runs and reaches a terminal status; `Save` was called).

**Endpoint tests (`tests/Nameless.TaskList.Tests/EndpointTests.fs`):**

- Update existing `BulkProgress` → `BulkJob` references.
- Cancel-endpoint status mapping: 200 / 404 / 409.

## Out of scope

- Multiple concurrent jobs — the gate stays one-at-a-time.
- A job-history listing endpoint.
- Any persistence backend other than the JSON file (no SQLite, no DB).
- Auto-resume of `done`/`cancelled`/`error` jobs (only `running`/interrupted jobs resume).
