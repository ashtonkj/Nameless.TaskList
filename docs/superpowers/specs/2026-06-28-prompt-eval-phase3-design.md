# Prompt Evaluation System — Phase 3 Design (match, merge, person-stub, relationships)

**Date:** 2026-06-28
**Status:** Approved (brainstorm) → ready for implementation planning
**Builds on:** `2026-06-28-prompt-eval-system-design.md` (the framework), Phase 1 (classify + topic-match),
and Phase 2 (generative creators) — both merged to `main`. This doc captures only the Phase-3-specific
decisions.

## 1. Goal

Complete the eval's coverage of every prompted-chat step. Phase 3 covers **seven** steps across three
families:

- **Match decisions** — `task-match`, `note-match`, `person-match`: does a new item match an existing one
  (the `taskMatchSystem` / `noteMatchSystem` / `personMatchSystem` "confirm" call over an
  embedding shortlist)?
- **Merge generation** — `task-update`, `note-update`: given an existing record and a new mention, rewrite
  the merged record (`taskUpdateSystem` returns a full file; `noteUpdateSystem` returns body-only).
  (Person-merge is a deterministic alias-add, not an LLM call, so there is no `person-update` step.)
- **New entity / graph** — `person-stub-create` (the `personStubSystem` creator deferred from Phase 2)
  and `relationship-extract` (`relationshipExtractSystem` typed edges).

After Phase 3 the eval scores every prompted step in the pipeline. Out of scope (and fine to leave):
LLM-as-judge; the non-prompted steps (embedding shortlist, vision, transcription); and provisioning a CI
host with Ollama to make the threshold exit a live gate (the one remaining infra follow-up).

## 2. Extraction boundary (consistent with Phase 1/2)

Only the LLM-call cores move into `Steps.fs`; ALL pipeline plumbing (candidate-building from the vault is
part of the step and moves with it; but writes, safe-merge, alias-add, resolve, context-filing,
`buildEdge`/`reconcile` stay). Each extraction is behaviour-preserving for `processMessage`, guarded by
the existing `PipelineTests`.

- **Shared match core:** move `shortlistAndConfirm` from `Pipeline.fs` into `Steps`, taking
  `chat` + `embedder` + `queryText` + `candidates: (slug * embedText * displayLine) list` + `floor` +
  `topK` + `systemPrompt` + `buildPayload` and returning the matched-slug `option`. (Today it takes
  `PipelineDeps`; the extracted form takes `chat`/`embedder` explicitly.)
- **Match decisions** (build candidates from the vault, call the core, return the decision only):
  - `Steps.matchTask : IChatClient -> IEmbedder -> IVault -> (intent:string) -> (floor:float) -> (topK:int) -> string option`
  - `Steps.matchNote : IChatClient -> IEmbedder -> IVault -> (intent:string) -> (floor:float) -> (topK:int) -> string option`
  - `Steps.matchPerson : IChatClient -> IEmbedder -> IVault -> (name:string) -> (contexts:string array) -> (floor:float) -> (topK:int) -> string option`
  `None` means "no confirmed match" (the pipeline then creates / stubs). Pipeline keeps the
  create/update/alias tail. `matchPerson` is the FUZZY second-chance only — the pipeline still runs exact
  alias-resolution before it, unchanged.
- **Merge generation** (return the model's parsed output, BEFORE the pipeline's safe-merge):
  - `Steps.updateTask : IChatClient -> (existingFile:string) -> (intent:string) -> (raw:string) -> EntityOutcome<Task>`
    (parse the model's full updated file; fall back to the old record + raw body on parse failure, exactly
    as today).
  - `Steps.updateNote : IChatClient -> (existingBody:string) -> (intent:string) -> (raw:string) -> string`
    (the model's updated body; `noteUpdateSystem` emits body-only).
  Pipeline keeps its safe-merge (fill `due` only if empty, raise `priority` upward only, merge
  context/people, refresh source) + write.
- **`Steps.createPersonStub : IChatClient -> GenInput -> EntityOutcome<Person>`** — a Phase-2-style
  creator over `personStubSystem`. It reuses the existing `GenInput` record (no new type): the mention
  name travels in `Intent`, the contexts in `Contexts`, and the message path in `MessagePath` — the only
  fields `personStubSystem`'s `BuildUser` ("Person mentioned: … / Message context: … / Mentioned in: …")
  references; the other `GenInput` fields are ignored. Pipeline keeps the resolve / alias /
  role→context-filing / write.
- **`Steps.extractRelationships : IChatClient -> (resolvedSlugs:string list) -> (messageContent:string) -> Result<RelationshipExtraction,string>`**
  — runs `relationshipExtractSystem` over the resolved-slugs payload and `parseRelationships`. Pipeline
  keeps `buildEdge` / `confidenceRank` filter / `reconcile` / write.

## 3. Scoring (`Scoring.fs`)

Reuses the Phase 1/2 helpers (`setF1`, `mean`, `norm`, `FieldScore`, `CaseResult`, the scalar/array/
date/title/body field helpers).

- **`scoreMatch (case) (result: Result<string option, string>)`** — for `task-match`/`note-match`/
  `person-match`. `expected.decision` = `"match"` → require `Ok (Some slug)` with normalised-equal
  `expected.slug` (else 0); `"nomatch"` → require `Ok None` (else 0); `Error` → 0 + ParseError.
- **`task-update`** → reuse Phase 2 `scoreTask` on the returned `EntityOutcome<Task>` (frontmatter / title /
  body assertions; only fields the case declares).
- **`note-update`** → `scoreNoteUpdate (case) (result: Result<string,string>)` — asserts `bodyContains`
  (case-insensitive, all substrings) on the returned body; optional `titleMatches` is NOT applicable
  (body-only). `Error` → 0.
- **`person-stub-create`** → `scorePerson (case) (result: Result<EntityOutcome<Person>,string>)` —
  scalar `role`; array `context`/`aliases`/`tags`; plus `titleMatches`/`bodyContains`. Only declared
  fields scored.
- **`relationship-extract`** → `scoreRelationships (case) (result: Result<RelationshipExtraction,string>)` —
  **F1 over the edge set**. Canonicalise each edge to `(relation, key)`: directed relations
  (`parent-child`, `patient-doctor`, `student-teacher`) use `from→to`; symmetric relations
  (`sibling`, `partner`, `colleague`, `friend`, `other`) use the unordered (sorted) pair. Slugs normalised
  via `Naming.slug`. **Descriptor and confidence are not scored.** `expected.relationships` is the gold
  edge list `[{from,to,relation}, …]`; empty-expected + empty-actual = 1.0 (correctly produced nothing).

## 4. Dataset

Seven new step directories under `eval/dataset/`: `task-match/`, `note-match/`, `person-match/`,
`task-update/`, `note-update/`, `person-stub-create/`, `relationship-extract/`. Case shapes:

- **match** (`task/note/person-match`): the case's `world` seeds the candidate entities/people; `input`
  carries the new `intent` (for task/note) or `name` + `contexts` (for person); `expected` is
  `{"decision":"match","slug":"…"}` or `{"decision":"nomatch"}`. Needs the live embedder for the shortlist.

  ```json
  {
    "id": "task-match-same-action",
    "step": "task-match",
    "world": "ashford-family",
    "input": { "intent": "Register Ethan for swimming lessons" },
    "expected": { "decision": "match", "slug": "sign-up-ethan-for-swimming" }
  }
  ```

- **update** (`task/note-update`): `input` carries the existing record (`existingFile` for task,
  `existingBody` for note) + the new `intent` and `message`; `expected` is field-assertions
  (task: `frontmatter`/`titleMatches`/`bodyContains`; note: `bodyContains`).

- **person-stub-create**: `input` has the mention `name` + `contexts`; `expected` asserts
  `frontmatter` (`role`, `context`, `aliases`) + `titleMatches`/`bodyContains`.

- **relationship-extract**: the world has the people; `input` carries `slugs` (co-mentioned resolved
  slugs) + `message`; `expected.relationships` is the gold edge list.

  ```json
  {
    "id": "rel-parent-child",
    "step": "relationship-extract",
    "world": "ashford-family",
    "input": { "slugs": ["sarah-ashford", "ethan-ashford"], "message": "Sarah picked up her son Ethan from school." },
    "expected": { "relationships": [ { "from": "sarah-ashford", "to": "ethan-ashford", "relation": "parent-child" } ] }
  }
  ```

All anonymised, reusing/extending the `ashford-family` world (already has `sarah-ashford`, `teacher-nomsa`,
`dr-naidoo`); add the people + a candidate task/note the match/relationship cases resolve against.
`EvalDatasetIntegrityTests.knownSteps` extends to all seven new steps.

## 5. Runner / Report / Args wiring

- **Runner:** seven new `runCase` branches dispatching to the shared `Steps.*` + matching scorer. Match
  branches use chat + embedder + the seeded world vault (candidates); update/stub/relationship branches use
  chat only (generation wrapped in `try/with → Error` so a throw scores 0).
- **Report / Args / scorecard:** unchanged — already step-generic; the new steps appear as extra rows and
  (when below par) failures. `Dataset.load` with no `--steps` enumerates the new dirs.

## 6. Testing

- Per-scorer unit tests (`scoreMatch`, `scoreNoteUpdate`, `scorePerson`, `scoreRelationships`; task-update
  via the reused `scoreTask`) including a directed-vs-symmetric relationship case and a parse-error case.
- One offline Runner test per new step driving a `FakeChatClient`/`FakeEmbedder`-scripted reply through the
  real `Steps.*` + scorer.
- The Steps extractions are guarded behaviour-preserving by the existing `PipelineTests` (full
  `dotnet test` stays green).

## 7. Global constraints (inherited)

net10.0; the Steps extractions are behaviour-preserving (full `dotnet test` green, `PipelineTests`
unchanged); deterministic scoring only (no LLM-judge); `Steps.*` return the model's output / decision and
the eval scores only model-produced values (never pipeline-forced linkage / safe-merge results); the eval
stays a non-default-test console project; writes through the Verevoir MCP; no real PII in any committed
dataset/world file.
