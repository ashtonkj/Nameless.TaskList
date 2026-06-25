# Fuzzy Match-and-Merge for Tasks & People — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorm), pending implementation plan
**Motivation:** Two live-run improvement loops surfaced entity proliferation that the existing
guards don't catch: near-duplicate **tasks** with different slugs ("Buy mattress" / "Research
mattresses"; an earring-supplies cluster) and duplicate **people** stubs for one person
("Nancy" vs "Teacher Nancy"). Notes and topics already solve this with embedding-shortlist +
LLM-confirm match-and-merge; tasks and people need the same, with type-appropriate resolution.

## Summary

Add fuzzy match-and-merge to **tasks** and **people**, reusing the proven note/topic mechanism
(`IEmbedder` shortlist → `Similarity.cosine` filter → LLM-confirm by slug → resolve). Tasks
**update in place** on a strict same-action match; people **add an alias** on a same-person
match. Both are best-effort: any failure falls back to the current create-new behavior, so the
pipeline never regresses.

## Goals

- Stop near-duplicate task files for the same action.
- Stop duplicate person stubs for the same individual.
- Reuse existing infrastructure and patterns; no new ports.
- Preserve idempotency and the "never throw on bad model output" discipline.

## Non-goals (this increment)

- Consolidating genuinely-distinct-but-related tasks ("buy" vs "research" the same item) — matching is **strict** (same action only); under-merging is acceptable and recoverable, over-merging loses a task.
- Person context re-filing (e.g. river/wyatt under `medical`) — separate concern.
- Retroactive cleanup of already-written duplicates (vault is append-only).
- Events/commitments fuzzy dedup (exact-slug overwrite already shipped; fuzzy not yet motivated by data).

## Architecture

Two independent additions, sharing the matching machinery, each integrated into the relevant
existing pipeline step.

### 1. Tasks — match-aware creation

In the task-creation step, before writing a new task for an intent:

1. **Shortlist** existing **pending** tasks (`tasks/pending`) by embedding similarity of the
   task intent vs each existing task's `title + body`: cosine ≥ `TaskMatch:SimilarityFloor`,
   top `TaskMatch:TopK`. Skip entirely when `tasks/pending` is empty (no embedder call).
2. **Confirm** with a **strict** `taskMatchSystem` LLM call: match only when the intent is the
   *same action restated* (e.g. "Sign up for X" = "Register for X"), NOT merely the same goal.
   Returns the matched slug. Reuses `parseTopicMatch` (the `Match`/`TopicSlug`/`Confidence`/
   `NewTopicTitle` DTO), exactly as notes do.
3. **Resolve:**
   - **Match → update in place:** rewrite the existing task file via a `taskUpdateSystem` merge
     that refreshes `due`/`priority` from the new mention and may enrich the body; the file path
     is unchanged. Append the source-message reference.
   - **No match / parse Error / unreadable matched file → create new** (the current
     `writeEntities`/`freePath` path).
4. **Re-scan per intent** (like notes) so a task written by an earlier intent in the same
   message is visible to the next — prevents in-message duplicates.

### 2. People — fuzzy fallback before stub creation

In the people-resolution loop, the existing **exact** alias-resolution runs first (cheap). Only
when it **fails** for a mention (a genuinely-new candidate):

1. **Shortlist** existing people by embedding similarity of the mention (name + message
   context) vs each person's `title + aliases + role`: cosine ≥ `PeopleMatch:SimilarityFloor`,
   top `PeopleMatch:TopK`. Skip when there are no people (no embedder call).
2. **Confirm** with `personMatchSystem`: same person? Returns the slug. Reuses `parseTopicMatch`.
3. **Resolve:**
   - **Match → add alias:** append the surface form as an alias to the matched person file (no
     new stub), reusing the existing alias-append logic.
   - **No match → create stub** (the current path).

## Components

**Reused, unchanged:** `IEmbedder`, `Similarity.cosine`, the per-intent re-scan pattern,
`parseTopicMatch` and its DTO, the alias-append logic, `EntitySpec`/`writeEntities` (the
no-match task path still goes through it).

**New prompts (`Prompts.fs`):**
- `taskMatchSystem` — strict "same action restated?" confirm → `parseTopicMatch`.
- `taskUpdateSystem` — merge a new mention into an existing task (refresh `due`/`priority`,
  enrich body), returns the updated body. Mirrors `noteUpdateSystem`. A `taskUpdateUser` builder
  mirrors `noteUpdateUser`.
- `personMatchSystem` — "same person?" confirm → `parseTopicMatch`.

**New config** (`appsettings.json`, bound like the existing `NoteMatch`):
- `TaskMatch:{TopK,SimilarityFloor}` and `PeopleMatch:{TopK,SimilarityFloor}`.
- Defaults: `TopK = 5`, `SimilarityFloor = 0.35` (matching `NoteMatch`).
- Added to `PipelineDeps`: `TaskTopK`, `TaskSimilarityFloor`, `PeopleTopK`, `PeopleSimilarityFloor`.

**Pipeline changes (`Pipeline.fs`):**
- The task branch becomes match-aware (a `processTask` analogous to `processNote`), wrapping the
  existing create path.
- The people stub-creation `else` branch gains the fuzzy-fallback step before creating a stub.

## Data flow

```
task intent ─► shortlist pending tasks (embedder + cosine, floor/topK)
            ─► taskMatchSystem (strict) ─► parseTopicMatch
                 match  ─► taskUpdateSystem merge ─► overwrite existing task file
                 no/err ─► create new task (writeEntities)

new-person mention (exact resolve failed)
            ─► shortlist people (embedder + cosine, floor/topK)
            ─► personMatchSystem ─► parseTopicMatch
                 match  ─► append alias to matched person file
                 no/err ─► create person stub
```

## Error handling

Identical to notes: embedder throws → no shortlist → create/stub; `parseTopicMatch` Error →
create/stub; matched file unreadable → create/stub. The whole match attempt is best-effort and
never throws out of the pipeline; behavior with matching disabled (empty vault, embedder
unavailable) is byte-for-byte the current behavior.

## Testing (`Core.Tests`; FakeVault / FakeEmbedder / FakeChatClient)

**Tasks:**
- Same action across two messages (FakeEmbedder returns a constant vector so the shortlist
  surfaces the existing task; `taskMatchSystem` scripted to match) → exactly one task file,
  `due`/`priority` updated, no `-2`.
- Two distinct actions (`taskMatchSystem` scripted no-match) → two task files.
- Empty `tasks/pending` → no embedder/match call, straight create (assert the embedder is not
  invoked).

**People:**
- "Teacher Nancy" then "Nancy" (exact resolution fails; `personMatchSystem` scripted match) →
  one person file with the other surface form as an alias; no second stub.
- A genuinely different person (`personMatchSystem` no-match) → two stubs.
- A mention that exactly resolves to an existing alias → no embedder/match call (fuzzy layer
  only fires after exact resolution fails).

## Files touched (anticipated)

- `Prompts.fs` — `taskMatchSystem`, `taskUpdateSystem` (+ `taskUpdateUser`), `personMatchSystem`.
- `Pipeline.fs` — `processTask` match-aware path; people fuzzy-fallback step; `PipelineDeps`
  fields.
- `Program.fs` / `appsettings.json` — bind `TaskMatch` / `PeopleMatch` config into `PipelineDeps`.
- Tests in `Core.Tests` (PipelineTests) as above.

## Open follow-ups (out of scope)

- Person context re-filing.
- Retroactive de-duplication of existing duplicates.
