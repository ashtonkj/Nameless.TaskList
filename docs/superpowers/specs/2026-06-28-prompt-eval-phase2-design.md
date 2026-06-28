# Prompt Evaluation System — Phase 2 Design (generative creators)

**Date:** 2026-06-28
**Status:** Approved (brainstorm) → ready for implementation planning
**Builds on:** `2026-06-28-prompt-eval-system-design.md` (the framework) and its Phase 1 deliverable
(merged to `main` as `bf3207b`). This doc captures only the Phase-2-specific decisions.

## 1. Goal

Extend the eval to the **generative** LLM steps — the markdown-file creators — so we can measure
generation quality (and drift, and a new model's generation) the same way Phase 1 measures the
discriminative steps. Phase 2 covers **four** creators: task, event, commitment, note. **Person-stub is
deferred to Phase 3** (its generation prompt is clean, but it is wrapped in entangled resolve / alias /
role→context-filing / fuzzy-dedup logic that belongs with Phase 3's person-match work; Phase 2 leaves
the Pipeline person block untouched).

## 2. Extraction boundary (the keystone decision)

Same principle as Phase 1: the eval must exercise the **real** prompts + interpret + fallback, not a
copy. So the per-type generation core is extracted from `Pipeline.fs` into `Steps.fs`:

- New functions `Steps.createTask`, `Steps.createEvent`, `Steps.createCommitment`, `Steps.createNote`.
- Each takes the chat client + the generation inputs the prompt already consumes (intent, raw message,
  referenceDate, contexts, urgency — per type) **plus the linkage values the pipeline injects**
  (topicPath, messagePath, peopleSlugs; for events also the linked taskPaths) **as parameters**.
- Each runs the real `Agent.runConversation` with that type's create system prompt, then applies the
  **same** interpret + parse-and-fallback logic currently in the `EntitySpec.Interpret` closures /
  `interpretNote` (including the event `ensureDated` path and its "date inferred from message" body
  flag), returning the existing `EntityOutcome<'T>` ({ Record; Body }).

**What stays in Pipeline (behaviour-preserving):** the `writeEntities` / `EntitySpec` write +
slug-collision machinery, the task/note **match-and-merge** wrappers (`processTask`/`processNote`,
`shortlistAndConfirm`, the update prompts), and **all file writes**. Pipeline's specs are rewired to
obtain their `EntityOutcome` from `Steps.create*` (passing the real linkage); nothing about the written
files changes. The existing `PipelineTests` guard this.

**Why "full record, score only generated fields"** (the chosen option over splitting generation from
linkage): it is the least-churn change to the entangled creators and is fully faithful. The linkage
fields (Topic, SourceMessage, TasksLinked, the pipeline's people-slugging) are forced by the pipeline and
never produced by the model, so the eval simply does not assert them. `Steps.create*` therefore returns
the full record (linkage included, set from its parameters); **the eval passes neutral stub linkage**
(empty topicPath/messagePath, empty people/taskPaths) and the **scorer asserts only the model-generated
fields**.

## 3. Scoring

Four small per-type scorers — `scoreTask`, `scoreEvent`, `scoreCommitment`, `scoreNote` — reusing the
Phase 1 helpers (`setF1`, `mean`, `FieldScore`, `CaseResult`). Each scores **only the fields a case
declares** in `expected`:

- **scalars** (`status`, `priority`, `all_day`, `location`, `reminder_days_before`, `escalate_after_days`,
  `task_assigned`) → exact (normalised) equality;
- **arrays** (`context`, `tags`) → `setF1` (with the Phase 1 glob/substring matching);
- **`due` / `when`** → date-equality against the case's expected **resolved** value. The model resolves
  the relative date (the prompt is given the reference date); the scorer compares the model's emitted
  value to the gold resolved value — `due` as a `YYYY-MM-DD` date, `when` as an ISO datetime compared on
  its resolved instant + `+02:00` offset. The scorer does **not** re-resolve the date itself.
- **`titleMatches`** → regex over the title; **`bodyContains`** → every listed substring present in the
  body (case-insensitive).

Case score = mean of the asserted checks. A generation that fails to parse falls back to the pipeline's
trivial record (exactly as production), which the assertions then score low — correctly flagging a poor
generation rather than throwing. Linkage fields are never asserted.

The case's `step` (`task-create` / `event-create` / `commitment-create` / `note-create`) selects the
scorer, exactly as `classify` / `topic-match` do in Phase 1.

## 4. Dataset

Four new step directories under `eval/dataset/`: `task-create/`, `event-create/`, `commitment-create/`,
`note-create/`. A generative case:

```json
{
  "id": "task-create-flu-vaccine",
  "step": "task-create",
  "tags": ["due-resolution", "verb-first-title"],
  "world": "ashford-family",
  "input": {
    "message": "Don't forget to book Ethan's flu vaccine before next Friday.",
    "intent": "Book Ethan's flu vaccine before next Friday",
    "referenceDate": "2026-06-24",
    "contexts": ["medical"],
    "urgency": "medium"
  },
  "expected": {
    "frontmatter": { "status": "pending", "priority": "medium", "context": ["medical"], "due": "2026-07-03" },
    "titleMatches": "^(Book|Schedule|Call)\\b",
    "bodyContains": ["flu vaccine"]
  }
}
```

`input` carries everything the four prompts need (the per-type `BuildUser` strings reference intent, raw
message, reference date, contexts, urgency). Seed a handful per type from **anonymised real** generations,
prioritising the tuning rules that drove past prompt fixes: title verb-first + `status: pending`;
relative-date → resolved `due` (date-only) / `when` (`+02:00`, `all_day` when only a date is known); note
title as a short **label** not the fact, with the fact in the body; context selection; the event
"date inferred" fallback. Anonymisation rule and worlds are exactly as Phase 1 (§4 of the framework spec).

`EvalDatasetIntegrityTests`'s `knownSteps` set extends to include the four new steps, so every committed
generative case is guarded (parses, names a known step, world loads).

## 5. Runner / Report / Args wiring

- **Runner:** `runCase` gains four `match case.Step` branches. Each seeds the case's world vault
  (`Worlds.load`), reads the generation inputs from `case.Input`, calls the matching `Steps.create*` with
  **neutral stub linkage**, and scores with the per-type scorer.
- **Report / Args / scorecard:** unchanged. They are already step-generic — new steps appear as extra
  rows in the per-step table and (when below par) the failures section. `Dataset.load` with no `--steps`
  already enumerates the new directories, and `--steps task-create,note-create` filters as before.

## 6. Testing

- Per-type scorer unit tests (exact/array/date/title/body checks; a fallback case scoring low).
- A Runner test per new step that drives a `FakeChatClient`-scripted generation through the real
  `Steps.create*` and asserts the resulting score — offline, like Phase 1's `EvalRunnerTests`.
- The Steps extraction is guarded behaviour-preserving by the existing `PipelineTests` (full
  `dotnet test` stays green).

## 7. Out of scope (Phase 2)

Person-stub generation + all person resolve/alias/dedup (→ Phase 3, with person-match); note/task/person
**match** steps (→ Phase 3); relationship extraction (→ Phase 3); LLM-as-judge; CI host provisioning.

## 8. Global constraints (inherited)

net10.0; deterministic scoring only; the Steps extraction is behaviour-preserving (full `dotnet test`
green); writes through the Verevoir MCP; the eval stays a non-default-test console project; no real PII
in any committed dataset/world file.
