# Digests (Increment C) — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning
> **Scope:** The third deferred increment — generate a daily briefing and a weekly digest from vault state, scored deterministically and written up as prose by the local LLM.
> **Builds on:** the spine, entity writers, and vault consistency increments. KB conventions authoritative in `docs/DESIGN.md` (§4.12 priority weights, §6.2 weekly flow, §7.6 daily briefing prompt).

---

## 1. Goal

Surface what matters: a **daily briefing** (DESIGN §7.6) and a **weekly digest** (DESIGN §6.2). Both read the vault's tasks/events/commitments/topics, score and select the important items **deterministically** per `_meta/priority-weights.md` (§4.12), and hand the prepared lists to the local LLM to write a concise human briefing. Each digest is written to a dated vault note and returned over HTTP, triggered by `POST /digest/daily` and `POST /digest/weekly` (a scheduler/n8n calls them).

The deterministic scoring/selection is the load-bearing, reproducible, unit-tested part; the LLM only turns prepared lists into prose, with a deterministic plain-text fallback if it fails.

---

## 2. Scope

### In scope
- `Weights` module: a `ScoringWeights` record, code `defaults` (§4.12), and a tolerant `parse` of `_meta/priority-weights.md` (fall back to defaults for any missing entry / malformed file).
- `Scoring`: a pure `scoreTask` applying context weight + derivable modifiers (due-within-2, due-within-7, blocked penalty).
- `Digest` module: deterministic gather + select (parameterized by window/top-N), list rendering, LLM prose with deterministic fallback, and writing a dated note.
- Two digest variants over one engine: **daily** (7-day window, top 5) and **weekly** (14-day window, top 10).
- Prompts: `dailyBriefingSystem` (verbatim §7.6) and a parallel `weeklyDigestSystem`.
- `Digest` frontmatter record + `Naming.digestPath`.
- `POST /digest/daily` and `POST /digest/weekly` endpoints.
- Unit tests with in-memory fakes for weights, scoring, selection, fallback, and endpoint mapping.

### Out of scope (later / unchanged)
- Actual delivery (WhatsApp self-message, email) — the endpoint returns the text; a caller/n8n delivers it.
- Scheduling itself (cron/n8n triggers the endpoints).
- The non-derivable §4.12 modifiers (`+2 blocks another task`, `+1 external person`) — current record fields don't express task dependencies or coordination cleanly; omitted.
- DESIGN §9 enhancements; live-service integration tests.
- `PipelineResult`, the pipeline, the Indexer, and existing routes are unchanged.

---

## 3. Weights & scoring (`Weights.fs`)

### 3.1 Record + defaults
```
ScoringWeights =
    { ContextWeights: Map<string,int>
      DueWithin7: int
      DueWithin2: int
      UnassignedCommitmentDueWithin7: int
      Blocked: int }
```
`defaults` (DESIGN §4.12): context weights `medical 10, finance 10, school 9, family 7, professional 5, personal-kb 2`; `DueWithin7 = 3`, `DueWithin2 = 5`, `UnassignedCommitmentDueWithin7 = 5`, `Blocked = -2`.

### 3.2 `parse : string -> ScoringWeights`
Reads the markdown body of `_meta/priority-weights.md`:
- "Context weights" lines of the form `name:<spaces>N` populate `ContextWeights` (any context absent keeps its default; contexts not in defaults are still accepted).
- Modifier lines are matched by their stable text fragments (`within 7 days`→`DueWithin7`, `within 2 days`→`DueWithin2`, `commitment with no task_assigned`→`UnassignedCommitmentDueWithin7`, `status == "blocked"`→`Blocked`) reading the leading signed integer.
- Any value not found falls back to the default. **Never throws** — a malformed or empty string yields `defaults`. The caller passes the file contents when `_meta/priority-weights.md` exists, else uses `defaults` directly.

### 3.3 `scoreTask : ScoringWeights -> today:DateTime -> Task -> int`
- **Base** = the maximum `ContextWeights` value over the task's `Context` array (a context absent from the map contributes 0; empty/null context → base 0).
- **Due bracket** (mutually exclusive): if `Due` parses (`DateTimeOffset.TryParse`) and `due.Date` is within 2 days of `today` → `+ DueWithin2`; else if within 7 days → `+ DueWithin7`; else nothing. Past-due dates count as within-2 (≤ 2). Blank/unparseable `Due` → no due modifier.
- **Blocked**: if `Status` (case-insensitive) is `blocked` → `+ Blocked` (negative).
- Pure; `today` is injected. `UnassignedCommitmentDueWithin7` is not applied to tasks (it's a commitment concept surfaced in selection §4.3, not in `scoreTask`).

---

## 4. Digest engine (`Digest.fs`)

Depends on `IVault`, `IChatClient`, `Weights`/`Scoring`, the domain records, `Agent`, `MarkdownFile`/`Frontmatter`/`Naming`. No new ports.

### 4.1 Params & deps
```
DigestKind = Daily | Weekly
DigestParams = { Kind: DigestKind; WindowDays: int; TopN: int }
  daily  = { Kind = Daily;  WindowDays = 7;  TopN = 5 }
  weekly = { Kind = Weekly; WindowDays = 14; TopN = 10 }

DigestDeps = { Vault: IVault; Chat: IChatClient; Model: string; Today: System.DateTime }

DigestResult =
  { Path: string; Text: string
    TaskCount: int; EventCount: int; CommitmentCount: int; StaleTopicCount: int }
```
`Today` is injected (endpoint passes `DateTime.Now`) so the engine is deterministic.

### 4.2 Gather (deterministic)
Load + parse all `tasks`/`events`/`commitments`/`topics` via `IVault.ListFilesRecursive` (excluding `index.md`), reusing the Indexer's skip-malformed resilience (parse failures skipped, never fatal).

### 4.3 Select (deterministic)
Load weights: if `_meta/priority-weights.md` exists, `Weights.parse` its body; else `Weights.defaults`.
```
DigestSelection =
  { TopTasks       : (Task * int) list   // status pending|in-progress, scored desc (tie: due, title), take TopN
    UpcomingEvents : Event list          // `when` within [Today, Today + WindowDays], chronological
    OpenCommitments: Commitment list     // status = unresolved AND task_assigned blank
    StaleTopics    : Topic list }        // status = active AND last_updated older than 14 days
```
Daily vs weekly differ **only** by `WindowDays`/`TopN`.

### 4.4 Render lists
Each selection list → compact text lines for the prompt (`{{tasks_list}}` etc.), e.g. `- {Title} · {context} · due {Due} · score {n}`; `(none)` when a list is empty.

### 4.5 LLM prose + fallback
`Agent.runConversation deps.Chat [] systemPrompt userPayload`, where the system prompt is `dailyBriefingSystem` (§7.6) or `weeklyDigestSystem`, and the payload fills the date + four rendered lists. If the call throws, produce a **deterministic plain-text fallback**: a fixed-format rendering of the four lists under plain headings. Either way a body is produced.

### 4.6 Write + return
Write `Naming.digestPath Today Kind` → `digests/{yyyy-MM-dd}-{daily|weekly}.md` with `type: Digest` frontmatter (`title`, `generated` timestamp, `kind`) + the briefing body. Re-running on the same day overwrites that day's note (idempotent by date). Never deletes. Return the `DigestResult` (path, text, and the four counts from the selection).

`Digest.generate : DigestDeps -> DigestParams -> DigestResult` is the entry point.

---

## 5. Naming & records (`KnowledgeBase.fs`)
- `Naming.digestPath (day: System.DateTime) (kind: string) -> "digests/{yyyy-MM-dd}-{kind}.md"` where `kind` is `"daily"`/`"weekly"`.
- `[<CLIMutable>] Digest = { Type: string; Title: string; Kind: string; Generated: string }` (frontmatter for the note).

---

## 6. Endpoints (web host)
Follow the `/reindex` pattern (resolve `IVault` + `IChatClient` from DI; build `DigestDeps` with `Today = DateTime.Now` and `Model` from `Ollama:Model`; call `Digest.generate`):
- `POST /digest/daily` → `Digest.generate deps DigestParams.daily`
- `POST /digest/weekly` → `Digest.generate deps DigestParams.weekly`

Both return `200` with `{ path, text, taskCount, eventCount, commitmentCount, staleTopicCount }`; unexpected exception → `500` (try/catch wrapper). `DigestHandler.toHttp : DigestResult -> IResult`. No config keys added. `PipelineResult` and existing routes unchanged.

---

## 7. Error handling & idempotency
- `Weights.parse` never throws → `defaults`.
- Gather skips unparseable entity files (not fatal).
- LLM failure → deterministic plain-text fallback; a note is always produced.
- Endpoint unexpected exception → `500`.
- Writes create/overwrite the dated note only; never delete. Same-day re-run overwrites (idempotent by date).

---

## 8. Testing (unit, in-memory fakes — no live services)
- `Weights.parse`: full table → all values; partial table → parsed + defaults; missing/garbage → `defaults`.
- `Scoring.scoreTask`: base = max over contexts (unknown → 0; empty → 0); due 2-day vs 7-day bracket vs neither; past-due counts as within-2; blocked penalty; deterministic under injected `today`.
- `Digest` selection (seeded `FakeVault`, injected `Today`): top-N order by score; event window boundary (inside vs just outside `WindowDays`); open-commitment filter (unresolved + blank task_assigned); stale-topic threshold (13 days not stale, 15 days stale); malformed file skipped; daily vs weekly differ only by window/N; the dated note is written and the returned counts match.
- LLM path: `FakeChatClient` yielding prose → asserted in the note; scripted-failure → fallback plain-text body used.
- Endpoint: `DigestResult` → 200 with the fields (existing `statusOf`-style harness; no live services).

---

## 9. Files touched
- `src/Nameless.TaskList.Core/Weights.fs` — new (ScoringWeights, defaults, parse, scoreTask).
- `src/Nameless.TaskList.Core/Digest.fs` — new (params/deps/result, gather, select, render, prose+fallback, write).
- `src/Nameless.TaskList.Core/Prompts.fs` — `dailyBriefingSystem`, `weeklyDigestSystem`.
- `src/Nameless.TaskList.Core/KnowledgeBase.fs` — `Digest` record + `Naming.digestPath`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — register `Weights.fs` then `Digest.fs` after `Indexer.fs`.
- `src/Nameless.TaskList/ProcessMessage.fs` + `Program.fs` — `DigestHandler` + two routes.
- `tests/Nameless.TaskList.Core.Tests/` and `tests/Nameless.TaskList.Tests/` — tests per §8.

---

## 10. Open follow-ups (later)
1. Delivery adapters (WhatsApp self-message / email) consuming the returned text.
2. Scheduling the endpoints (cron/n8n).
3. The non-derivable §4.12 modifiers once task-dependency / coordination data exists.
4. Align index/digest `generated` timestamps with the vault's `+02:00` convention.
