# Prompt Evaluation System — Design

**Date:** 2026-06-28
**Status:** Approved (brainstorm) → ready for implementation planning
**Scope of this spec:** the full framework + **Phase 1** (classify + topic-match) as the first deliverable. Phases 2–3 are described for context but planned separately.

## 1. Problem

Every consequential decision in the pipeline is made by a local LLM driven by a hand-tuned
prompt in `src/Nameless.TaskList.Core/Prompts.fs` (classify, topic-match, the note/task/person
match steps, the markdown-file creators, relationship extraction, the digests). These prompts have
been tuned repeatedly from live runs. We have **no way to measure prompt/model quality**, so we
cannot:

- detect **quality drift** when a prompt is edited or a model is upgraded, and
- **compare a new model** against the current one when it is released.

This system gives both: a reproducible, hand-labelled gold set scored deterministically, run from a
standalone CLI that produces a scorecard and can diff any two runs.

## 2. Goals / non-goals

**Goals**
- Measure true accuracy against **human-labelled gold**, not merely divergence from a baseline.
- Cover **all** LLM steps eventually (discriminative *and* generative), starting with the keystones.
- Be **faithful**: exercise the exact production prompts, parsers, and tool-calling loop — never a copy.
- Run from a **standalone CLI** producing a human-readable scorecard + a machine-readable JSON sibling.
- Support **drift detection** and **model comparison** through the same baseline-diff mechanism.
- Exit non-zero below a score **threshold** so it can later become a one-command CI gate.

**Non-goals (v1)**
- No LLM-as-judge scoring — deterministic only (reproducibility is required of a drift guard).
- Not part of the default `dotnet test` (needs live Ollama; mirrors the integration suite's opt-in stance).
- No automatic prompt optimisation / search. The eval measures; humans tune.
- No CI wiring yet (the runner is built CI-ready; standing up the runner host is a separate follow-up).

## 3. Faithfulness: shared step functions (keystone decision)

An eval that re-wires the prompt calls will silently drift from `processMessage` and start lying.
Therefore the per-step LLM calls are **extracted** from the `Pipeline.processMessage` monolith into a
`Pipeline.Steps` module — one function per step (e.g. `classify`, `matchTopic`, `createTask`), each
taking the pipeline deps + the step's inputs and returning the parsed result. `processMessage` is
refactored to call these functions, and the eval calls the **same** functions. This makes prompt +
parser + tool-loop a single source of truth, and incidentally untangles `Pipeline.fs`, which has grown
large.

Extraction is **incremental**: each phase extracts only the step functions it wires into the eval, so
no phase is a big-bang refactor. Phase 1 extracts `Steps.classify` and `Steps.matchTopic`. Each
extraction is behaviour-preserving for `processMessage` (verified by the existing pipeline tests).

## 4. Reproducible tool context — vault-fixture "worlds"

Several steps expose read-only vault tools mid-loop (`get_contexts`, `get_people`, `get_topics`,
`get_topic`, `get_relationships`). These tools read **real markdown files**: `get_contexts`/`get_people`
concatenate raw file contents under `contexts/`/`people/`, and `get_topics`/`get_relationships` parse
frontmatter. So tool context cannot be loose JSON slugs — if it were, the model's tool-driven reasoning
would diverge from production, and a case whose message names "Sarah" while `get_people` returns
real or unrelated names would be internally inconsistent.

Therefore each case references a **vault-fixture world**: a directory of actual markdown files under
`eval/dataset/_worlds/<world>/` mirroring the relevant vault subtree (`contexts/`, `people/…`,
`topics/active/`, `relationships/`). The eval seeds a `FakeVault` from that world and backs the real
`Tools.*` with it, so every tool call returns valid, fixed context in exactly the production format.

**Anonymisation must be consistent across the message and its world.** When a real case is scrubbed,
the *same* name/number/address map is applied to the message text **and** to the world's files, so a
person the message mentions exists as an anonymised `people/…` file the tools will surface, and the
case's `expected` values use the same fictional slugs. This consistency is the point the fixture must
enforce — it is what makes a tool-using run faithful.

**Sharing & DRY.** A `_worlds/_base/` world holds the generic, non-PII context-definition files
(`contexts/*.md`, copied from a real seed) plus any standing identities (e.g. the KB owner). The eval
**always seeds `_base/` first, then overlays the case's named world** (named-world files win on path
collision). Cases that share an anonymised "world" (same people/topics) name the same world, so the
scrubbing is authored once and reused. A case needing the model to *match an existing topic* simply
includes that topic's file in its world's `topics/active/`.

## 5. Dataset

**Location & format.** One JSON file per case under `eval/dataset/<step>/<id>.json`. JSON, to match the
codebase's System.Text.Json convention. Loaded by globbing the dataset dir; the `step` field selects the
scorer; `world` names the vault-fixture world (§4) seeded for the case's tool calls (`_base` is always
overlaid under it). Example (classify):

```json
{
  "id": "classify-school-picnic-notice",
  "step": "classify",
  "tags": ["event-not-task", "owner-task-discipline"],
  "world": "ashford-family",
  "input": {
    "message": "Reminder: bring a named teddy for Friday's class picnic at 10am.",
    "referenceDate": "2026-06-24",
    "history": []
  },
  "expected": {
    "noise": false,
    "contexts": ["school"],
    "tasks": [],
    "events": ["*picnic*"],
    "people": []
  }
}
```

A `world` of `"_base"` (or omitted → defaults to `_base`) means only the shared base world: generic
contexts and standing identities, no case-specific people/topics.

**Generative cases** (Phase 2) carry **salient-field assertions** rather than exact markdown bytes:

```json
{
  "id": "task-create-flu-vaccine",
  "step": "task-create",
  "world": "ashford-family",
  "input": { "intent": "Book Ethan's flu vaccine before next Friday", "referenceDate": "2026-06-24", "raw": "..." },
  "expected": {
    "frontmatter": { "status": "pending", "priority": "medium", "context": ["medical"], "due": "2026-07-03" },
    "titleMatches": "^(Book|Schedule|Call)\\b",
    "bodyContains": ["flu vaccine"]
  }
}
```

**Provenance & privacy.** Gold inputs are seeded from **anonymised real** pipeline cases — prioritising
the ones that drove past tuning (owner-vs-other task discipline, event-not-incident, person discipline,
the noise taxonomy, SAST date resolution). Names / numbers / addresses are scrubbed to fictional
stand-ins **before** commit, applying the *same* map to the message and to the case's world files
(§4) so the two stay consistent. No real PII enters the repo. A short `eval/dataset/README.md` documents
the scrubbing rule, the world layout, and the case-authoring format.

## 6. Scoring

A scorer per step kind, each producing per-field results aggregated to a **case score in [0,1]**; step
score = mean over its cases; overall = mean over steps.

- **Booleans / enums** (`noise`, `urgency`, `action_required`, `match`, matched `slug`, `priority`,
  `all_day`) → exact match (1/0).
- **Arrays** (`contexts`, `tasks`, `events`, `people_mentioned`, `commitments`, relationship edges) →
  set **precision / recall / F1**. Normalisation: trim + lowercase; expected entries may be glob/substring
  patterns (`*picnic*`) so benign wording variance does not false-fail. The field score is F1.
- **Dates** (`due`, `when`) → equality of the **resolved** value against the case's reference date
  (so "Friday" → the correct ISO date is what's checked, with the `+02:00` rule for events).
- **Generative** → the field assertions in §5 (frontmatter equality, `titleMatches` regex,
  `bodyContains` substrings). No LLM judge.

**Noise confusion summary.** Because noise mis-classification is the highest-cost error, the report
carries a dedicated precision/recall summary for the `noise` boolean across the whole set, naming the
false-positive and false-negative case ids.

**Robustness.** A model reply that fails to parse scores the case **0** for that step (never throws),
and is listed as a parse failure in the report — mirroring the pipeline's "never throw on bad model
output" contract.

## 7. Runner & report

**Project.** `eval/Nameless.TaskList.Eval` — an F# console app referencing `Core`, added to
`Nameless.TaskList.slnx` (so it keeps compiling under `dotnet build`) but only *run* manually.

**CLI.**
```
dotnet run --project eval/Nameless.TaskList.Eval -- \
  --model granite3.3:8b \        # default: appsettings Ollama:Model
  --steps classify,topic-match \ # default: all steps present in the dataset
  --report out.md \              # also writes out.json alongside
  --baseline baseline.json \     # optional: diff against a saved run
  --threshold 0.85               # default 0.85 overall; non-zero exit if below
```
Loads cases, groups by step, runs each through its `Pipeline.Steps` function against a real
`OllamaChatClient` (chosen model) + the case's seeded world vault (§4), scores, aggregates. Ollama-unreachable or a
dataset-load failure → a clear non-zero exit with a message, not a stack trace.

**Report.** Markdown scorecard to stdout and `--report`, plus a machine-readable JSON sibling
(`out.json`):
- Header: model, UTC date, case count, overall score, PASS/FAIL vs threshold.
- Per-step table: case count, mean score, array P/R/F1.
- The `noise` confusion summary with offending case ids.
- Failures section: each below-par case with id, and an expected-vs-actual field diff so a regression is
  immediately legible.

**Baseline & model comparison (one mechanism).** Every run can be saved as a JSON scorecard.
`--baseline old.json` adds a **delta column** (per-step and overall, e.g. `+0.04` / `−0.11`) and flags
cases that flipped pass↔fail. Drift = a model vs its own past baseline; model comparison = model A's
scorecard vs model B's — identical code path.

**Threshold / CI.** Exit non-zero when overall < `--threshold` (default **0.85**), so the runner can
later become a one-command CI gate on a host that has Ollama. (Standing up that host is out of scope.)

## 8. Phasing

One framework; coverage grows incrementally. Each phase extracts its step functions from `Pipeline.fs`
(shrinking the monolith) and adds gold cases.

1. **Phase 1 — framework + classify + topic-match** *(this spec's deliverable)*: the project, dataset
   loader, scorer registry, CLI, report, baseline diff, world-seeded fake vault; extract `Steps.classify` /
   `Steps.matchTopic`; seed the gold set from anonymised real cases covering the tuning rules.
2. **Phase 2 — generative creators**: `Steps.createTask/createEvent/createCommitment/createNote/
   createPersonStub`; the field-assertion scorer; gold cases.
3. **Phase 3 — remaining discriminative**: note/task/person match + relationship extraction; gold cases.

Phases 2–3 are mechanical repeats of Phase 1's pattern and are planned separately.

## 9. Risks & mitigations

- **Step extraction changes `processMessage` behaviour.** Mitigation: behaviour-preserving extraction,
  one step at a time, guarded by the existing `PipelineTests`.
- **Gold labels encode a human's wrong judgement.** Mitigation: cases are reviewable JSON with `tags`
  explaining intent; the set is version-controlled and correctable.
- **Array scoring too strict/loose.** Mitigation: glob/substring patterns + normalisation; F1 (not exact
  set equality) so partial credit is visible.
- **Model nondeterminism across runs.** Accepted: scores are a distribution; the threshold has headroom
  and the baseline-diff highlights movement rather than demanding bit-exact repeats.

## 10. Out of scope

LLM-as-judge scoring; automatic prompt optimisation; CI host provisioning; evaluating the embedding
shortlist / vision / transcription steps (this system targets the **prompted chat** steps).
