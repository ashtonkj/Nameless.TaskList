# In-App Scheduler (replace n8n)

**Date:** 2026-06-28
**Status:** Approved design, pre-implementation

## 1. Goal

Run the recurring maintenance tasks — the daily digest, the weekly digest, and
the reindex/topic-archival sweep — on a schedule from inside the app, so the n8n
dependency can be removed. Today these are HTTP endpoints (`POST /digest/daily`,
`POST /digest/weekly`, `POST /reindex`) that an external n8n cron triggers. After
this change the app schedules them itself; the endpoints remain for manual runs.

## 2. Decisions (settled)

| Question | Decision |
|---|---|
| Schedule format | Simple fixed-time config: a local time-of-day per task, plus a day-of-week for the weekly digest. No cron engine, no new dependency. |
| Missed runs | Run-if-missed (catch-up): a per-task last-run timestamp; a task whose most-recent scheduled slot is newer than its last run is due, so a restart or downtime that straddled a slot runs it once on return. Safe — digests are date-stamped and reindex is idempotent. |
| Enabling tasks | A blank/missing time disables that task (no separate per-task flags). A single `Scheduler:Enabled` gates the whole service (default off). |
| Mechanism | A gated `BackgroundService` + pure scheduling logic in Core + a JSON state store — mirrors the IMAP poller / WhatsApp listener pattern already in the codebase. |

## 3. Architecture

```
SchedulerService (BackgroundService, gated on Scheduler:Enabled)
  every CheckIntervalSeconds:
    state = stateStore.Load()
    due   = Scheduler.dueTasks now config state      ── pure, testable
    for task in due:
        try run task (Digest.generate | Indexer.regenerate)   ── shared runner
        record last-run = now ; stateStore.Save
```

The pipeline/endpoints are untouched. Times are server-local (`DateTime.Now`),
consistent with `Indexer`/`Digest`, which already use `DateTime.Now`; the
deployment runs in SAST (+02:00).

### 3.1 Config

Under a new `Scheduler` section in `appsettings.json`:

```json
"Scheduler": {
  "Enabled": false,
  "DailyDigest": "07:00",
  "WeeklyDigest": "Mon 07:00",
  "Reindex": "03:00",
  "CheckIntervalSeconds": 60
}
```

- `Enabled = "true"` registers the hosted service; otherwise it never starts
  (off by default and in tests).
- Each task's time string: `HH:mm` for daily/reindex, `Ddd HH:mm` for the weekly
  digest (day-of-week is one of `Mon Tue Wed Thu Fri Sat Sun`, case-insensitive).
- A blank, missing, or unparseable time string disables that task (logged once at
  startup). Bad config never throws.
- `CheckIntervalSeconds` defaults to 60.

### 3.2 Pure scheduling logic (`Scheduler.fs`, new in Core)

```fsharp
/// A parsed schedule for one task. None of the constructors are produced for a
/// blank/unparseable config string (that task is simply absent from the task list).
type ScheduleSpec =
    | Daily of hour:int * minute:int
    | Weekly of day:System.DayOfWeek * hour:int * minute:int
```

- `parseSpec : string -> ScheduleSpec option` — tolerant. `"07:00"` → `Daily`;
  `"Mon 07:00"` → `Weekly`; anything else (blank, garbage, out-of-range) → `None`.
- `mostRecentOccurrence : now:System.DateTime -> ScheduleSpec -> System.DateTime`
  — the latest scheduled datetime ≤ `now`.
  - `Daily(h,m)`: today at `h:m` if that is ≤ `now`, else yesterday at `h:m`.
  - `Weekly(d,h,m)`: the most recent `d` at `h:m` that is ≤ `now` (this week if its
    occurrence has passed, else last week's).
- A task is **due** when `lastRun < mostRecentOccurrence now spec`. `lastRun`
  defaults to `DateTime.MinValue` (never run) so a task is due on first start once
  its first slot has passed.

### 3.3 Task model + selection

```fsharp
/// The three schedulable operations. Name is the state key + log label. (Scheduler.fs)
type ScheduledTask = { Name: string; Spec: ScheduleSpec }
```

`SchedulerState` is serialized and is referenced by `ISchedulerStateStore` in
`Ports.fs` (compiled early), so — like `EmailCursor`/`ListenCursor` — it lives in
`Library.fs` (compiled first), not in `Scheduler.fs`:

```fsharp
/// Per-task last-run timestamps. Serialized — public record, in Library.fs.
type SchedulerState = { LastRuns: System.Collections.Generic.Dictionary<string, System.DateTime> }
```

- `dueTasks : now:System.DateTime -> ScheduledTask list -> SchedulerState -> ScheduledTask list`
  — returns the tasks whose slot is newer than their recorded last run, in list
  order (deterministic).
- `tick : now -> ScheduledTask list -> SchedulerState -> run:(ScheduledTask -> unit) -> SchedulerState`
  — runs each due task via `run`, returns the state with those tasks' last-run set
  to `now`. Pure except for the injected `run`; this is the unit-tested seam. (The
  task names are the fixed constants `"daily-digest"`, `"weekly-digest"`,
  `"reindex"`.)

### 3.4 Ports + adapter

- `ISchedulerStateStore` (Ports.fs): `Load : unit -> SchedulerState` /
  `Save : SchedulerState -> unit`.
- `FileSystemSchedulerStateStore(path)` (Adapters.fs): JSON at
  `<Vault:Root>/.taskmeister/scheduler-state.json`, mirroring the cursor stores.
  Missing/unparseable file → empty state (all tasks treated as never-run). The
  `Dictionary<string,DateTime>` serializes cleanly with System.Text.Json.

### 3.5 Host service + shared runner

- `SchedulerService : BackgroundService` (host project, registered before
  `Program.fs`): a timer loop at `CheckIntervalSeconds` that loads state, computes
  `dueTasks`, runs each, and persists state after each task. **Each task runs in
  its own try/catch** so one failure (e.g. a slow or failing LLM digest) is logged
  and does not block the others or the loop. Clean cancellation on shutdown, like
  the IMAP/WhatsApp services.
- **Shared runner.** `Program.fs` currently builds the digest deps in a local
  `runDigest` helper and the reindex `TopicSweepConfig` inline in the endpoint.
  Extract these into a small reusable runner (a module or functions) so the
  endpoint handlers and the scheduler call the *same* code — no drift between a
  manual run and a scheduled run. The runner executes `Digest.generate` (daily /
  weekly params) and `Indexer.regenerate` against the DI-provided `IVault` /
  `IChatClient` and the same `Topics:*` / `Ollama:Model` config the endpoints read.

## 4. Components / files

- `src/Nameless.TaskList.Core/Library.fs` — add `SchedulerState` (serialized; referenced by the port).
- `src/Nameless.TaskList.Core/Scheduler.fs` — **new** (compiled after `Digest.fs`; pure logic, depends only on `SchedulerState` from Library + the std lib): `ScheduleSpec`, `ScheduledTask`, `parseSpec`, `mostRecentOccurrence`, `dueTasks`, `tick`.
- `src/Nameless.TaskList.Core/Ports.fs` — add `ISchedulerStateStore` (references `SchedulerState`).
- `src/Nameless.TaskList.Core/Adapters.fs` — add `FileSystemSchedulerStateStore`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — register `Scheduler.fs` (after `Digest.fs`).
- `src/Nameless.TaskList/Scheduler.fs` — **new**: `SchedulerService : BackgroundService` + the shared digest/reindex runner.
- `src/Nameless.TaskList/Program.fs` — extract the digest/reindex run into the shared runner; have the endpoints call it; register `SchedulerService` when `Scheduler:Enabled = "true"`.
- `src/Nameless.TaskList/Nameless.TaskList.fsproj` — register the host `Scheduler.fs` before `Program.fs`.
- `src/Nameless.TaskList/appsettings.json` — `Scheduler` section.
- Tests — `SchedulerTests.fs` (Core.Tests).

## 5. Testing

All offline; default `dotnet test` stays green and starts no scheduler.

- **`parseSpec`**: `"07:00"` → `Daily(7,0)`; `"Mon 07:00"` → `Weekly(Monday,7,0)`;
  blank / `"garbage"` / `"25:00"` / `"Xyz 07:00"` → `None`; case-insensitive day.
- **`mostRecentOccurrence`**: daily before vs after today's time (today vs
  yesterday); weekly on the configured day before/after the time, and on a
  different weekday (→ earlier this week or last week). Use fixed `now` values.
- **`dueTasks` / `tick`**: a task with `lastRun` before its slot is due and runs; a
  task already run this slot is not re-run; multi-day downtime → due once (catch-up)
  and not repeatedly; a disabled task (absent from the list) never runs; `tick`
  advances only the run tasks' last-run and a recording `run` proves which fired;
  one task's `run` throwing does not stop the others (the service-level guard is
  exercised by making `run` partial in the tick test or asserting the host wrapper
  — keep the throw-isolation at the service shell).
- **State store** round-trip; missing file → empty state.

## 6. Out of scope

- Cron expressions / sub-minute precision.
- Distributed locking or multi-instance coordination (single instance).
- Per-task retry/backoff beyond "try again at the next check interval / next slot".
- New scheduled task types beyond the existing three.

## 7. Notes / risks

- **Idempotency makes catch-up safe.** A daily digest re-run on the same day
  overwrites `digests/YYYY-MM-DD-daily.md`; reindex regenerates index files. So
  even if `dueTasks` mis-fires by a slot, output is not duplicated.
- **Shared-runner extraction** is the one change touching existing endpoint code.
  Keep it behavior-preserving: the endpoints must produce the same HTTP results as
  today; only the digest/reindex *invocation* is factored out for reuse.
- **Time zone.** All times are server-local via `DateTime.Now`. If the host is not
  in SAST the slots shift; this matches the existing `Indexer`/`Digest` behavior
  and the deployment assumption, and is out of scope to change here.
