# Person context re-filing — design

**Date:** 2026-06-30
**Status:** Approved (ready for plan)
**Roadmap item:** Medium — "Person context re-filing" (first consumer of the vault relocate primitive).

## Problem

People are filed at `people/{context}/{slug}.md`, where the context folder is derived at stub-creation
time from the LLM's `Context[0]` (falling back to the *message's* context, default `family`). So a person
first mentioned in a family chat is filed under `people/family/` even when their `Role` (e.g. "doctor")
implies a different life-context (`medical`). The pipeline never revisits this, so the misfiling persists.

## Goal

A maintenance pass that re-files each person into the context their `Role` implies, moving the markdown file
between context folders. Tidiness only — links are slug-based so functionality is unaffected; this improves
how the vault browses (e.g. `people/medical/` actually lists medical contacts).

## Approach

An LLM-based maintenance pass, structured like `Indexer`/`Digest`: a Core `Refiler` module holds the logic;
the host exposes it as `POST /people/refile` and an optional scheduler slot, both routed through the existing
`MaintenanceTasks` runner so a manual and a scheduled run execute identical code. It scans every person,
classifies their `Role` into one of the five `knownContexts`, and moves any whose folder disagrees — using
`IVault.Relocate(old, new)` **directly** (active→active; the `.trash/`/`Vault.retire` machinery is only for
de-dup). Per-message processing is untouched.

`knownContexts` (already in `Steps`): `family | medical | school | finance | professional`.

## Components

### 1. `Steps.classifyPersonContext` (shared, eval-coverable)

`classifyPersonContext : IChatClient -> name:string -> role:string -> aliases:string array -> string option`

A small prompted step (new `Prompts.personContextSystem` + a parser) that returns exactly one of the five
known contexts, or `None` when the model replies off-list/garbage. Lives in `Steps.fs` with the other prompted
steps, so the prompt-eval harness can cover it (new gold cases). Best-effort: a parse failure → `None`.

The parser lowercases/trims the reply and accepts it only if it is exactly one of `Steps.knownContexts`;
anything else → `None`.

### 2. `Refiler` (Core module, LLM-driven maintenance)

- **Pure decision helper (unit-testable without a vault):**
  ```fsharp
  type RefileAction = NoChange | Refile of target: string | SkipCollision | SkipUnknown
  let decideRefile (currentContext: string) (target: string option) (targetExists: bool) : RefileAction
  ```
  `None` target → `SkipUnknown`; `Some t` with `t = currentContext` → `NoChange`; `Some t` with the target
  path already occupied → `SkipCollision`; otherwise → `Refile t`.

- **`RefileSummary = { Scanned: int; Refiled: int; Skipped: int }`** (defined in `Refiler`, like
  `Indexer.IndexSummary`).

- **`run : IVault -> IChatClient -> RefileSummary`** — for each file under `people/**` (recursive):
  1. Parse the `Person`; derive `currentContext` = the path segment after `people/` and `slug` = the filename
     without extension. Skip files whose path isn't the expected `people/<ctx>/<slug>.md` shape.
  2. `target = Steps.classifyPersonContext chat p.Title p.Role p.Aliases`.
  3. `newPath = Naming.personPath target slug`; `targetExists = vault.Exists newPath`.
  4. `decideRefile currentContext target targetExists`:
     - `NoChange` / `SkipUnknown` / `SkipCollision` → tally (a `SkipCollision` is logged — that's a same-slug
       duplicate, which is the *de-dup* feature's job, not re-filing's).
     - `Refile t` → execute the move (below) and count as refiled.
  5. **Move + frontmatter fix:** `vault.Relocate(oldPath, newPath)`; then — guarded by the primitive's
     no-success-signal caveat — if `vault.Exists newPath && not (vault.Exists oldPath)`, read the moved file,
     set `Context = [| t |] ++ (existing contexts except t)` (so `Context[0] = t`, folder and frontmatter
     agree, other contexts preserved, deduped), and `Write` it back.
  - Best-effort per person: any exception (parse/read/classify/move) skips that one and the pass continues.

The slug (filename) is unchanged by a re-file, only the context directory changes. Entity→person links
(`People` arrays, relationship `from`/`to`) are slug-based, so **no link rewriting is needed** — Obsidian
resolves across folders. The pass does **not** regenerate indexes; `/reindex` (scheduled) does that.

### 3. Host wiring

- **`MaintenanceTasks.refilePeople cfg vault chat : Refiler.RefileSummary`** (host `Scheduler.fs`) — calls
  `Refiler.run vault chat`.
- **`RefileHandler.toHttp : Refiler.RefileSummary -> IResult`** (host `ProcessMessage.fs`) — `Results.Ok(box
  {| scanned; refiled; skipped |})`, mirroring `ReindexHandler`.
- **`POST /people/refile`** (Program.fs) → `MaintenanceTasks.refilePeople cfg vault chat |> RefileHandler.toHttp`,
  with the same `try … with ex -> Results.Json({| error = ex.Message |}, statusCode = 500)` wrapper as `/reindex`.
- **Scheduler slot `Scheduler:RefilePeople`** — a new `"refile-people"` scheduled task (blank spec = disabled;
  default disabled), wired into the Program scheduler `tasks` list and the `runTask` dispatch
  (`"refile-people" -> MaintenanceTasks.refilePeople cfg vault chat |> ignore`).

## Error handling

Best-effort and idempotent throughout. A classify/parse failure, read failure, or target collision skips that
one person; the pass continues and tallies it as skipped. A correctly-filed person classifies to its current
context → `NoChange`, so repeated runs are stable (no move loops — re-filing keys off `Role`, which the pass
never changes). A `Relocate` IO failure is caught per-person.

## Testing

- **`Steps.classifyPersonContext`** (Core.Tests, `FakeChatClient`): on-list reply → that context; off-list /
  garbage / whitespace → `None`. Plus optional gold eval cases for the new step.
- **`Refiler.decideRefile`** (pure, Core.Tests): already-correct → `NoChange`; mismatch + free target →
  `Refile`; mismatch + occupied target → `SkipCollision`; `None` target → `SkipUnknown`.
- **`Refiler.run`** over a `FakeVault` (Core.Tests, scripted `FakeChatClient`): a `family/`-filed "doctor"
  moves to `people/medical/<slug>.md` with `Context[0] = medical` and the old path vacated; a person whose
  target folder already holds that slug is left in place (collision); an already-correct person is untouched;
  the returned `RefileSummary` counts match.
- **`RefileHandler.toHttp`** (endpoint tests): a `RefileSummary` maps to 200 with the expected JSON shape.

## File / compile-order summary

- `Prompts.fs` — add `personContextSystem` + the context parser.
- `Steps.fs` — add `classifyPersonContext` (uses `knownContexts`).
- `Refiler.fs` — **new** Core module (after `Indexer.fs`, near the other maintenance modules): `RefileAction`,
  `decideRefile`, `RefileSummary`, `run`.
- `Nameless.TaskList.Core.fsproj` — register `Refiler.fs`.
- Host `Scheduler.fs` — `MaintenanceTasks.refilePeople`.
- Host `ProcessMessage.fs` — `RefileHandler.toHttp`.
- Host `Program.fs` — the `POST /people/refile` endpoint + the `refile-people` scheduler task + `runTask` arm.
- `appsettings.json` — add `Scheduler:RefilePeople` (default `""` = disabled).
- Tests: `StepsTests.fs`, a new `RefilerTests.fs`, `EndpointTests.fs`.

## Out of scope

- **Retroactive de-dup** (the same-slug collision case, and merging existing duplicates) — its own later cycle;
  re-filing deliberately *skips* collisions rather than guessing.
- Re-classifying or editing a person's `Role` — re-filing reads `Role`, never writes it.
- Index regeneration — `/reindex` already owns that.
- Re-filing non-person entities.
