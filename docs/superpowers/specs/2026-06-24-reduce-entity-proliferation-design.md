# Reducing entity proliferation: durable notes, tighter topics, alias-aware people — Design

Date: 2026-06-24

## Problem

A bulk re-process of ~235 non-noise messages produced **235 notes**, 63 topics, 95 tasks, 56 events, and 83 people. The pipeline creates new files far too eagerly instead of updating related existing ones. Three concrete failures:

1. **Notes are never deduplicated.** `writeEntities` (`Pipeline.fs`) writes a brand-new note file per note-intent; `freePath` only appends `-2`/`-3` to avoid clobbering. The classifier emits notes for *"any factual information worth storing"* (`Prompts.fs`), so roughly one note is created per message — fragmenting a single subject across many files (e.g. `circuit-breaker-status-and-power-hypothesis`, `breaker-status-update`, `potential-cause-of-failure` are all one gate/power incident).
2. **Topic matching is biased toward creating new topics.** When no active topic scores ≥ `SimilarityFloor` (0.5), the pipeline creates a new topic *without consulting the LLM* (`Pipeline.fs:230-232`). The match prompt further biases toward new (*"Prefer creating a new topic over forcing a weak match"*, *"confidence below 0.75 → match:false"*).
3. **People are duplicated and mis-filed.** Person resolution is exact-slug only (`personExists`, `Pipeline.fs:50-51`); a person mentioned as "Mom", "Mum", and "Sarah" becomes three files. And every person lands in the `family` context because the stub derives its context from the *message's* context (a family chat mentioning a doctor → doctor filed under `family`), with a parse-failure fallback that hard-codes `family`.

This design reduces proliferation by preferring update-over-create, anchored on a conceptual decision: **most per-message factual content belongs in the topic's evolving "Current understanding" (which the topic-update step already maintains), not in standalone notes.** Notes are reserved for durable, cross-topic reference material.

## Decisions (from brainstorming)

- **Note's role:** durable cross-topic reference facts only (DESIGN §4.10's intent). Per-message observations fold into the topic.
- **Note mechanism:** tighten the extraction criteria (prompt) AND add match-vs-update (embedding shortlist + LLM confirm + LLM merge), mirroring the proven topic flow.
- **Topics:** tighten match-vs-create so related messages consolidate.
- **People:** deterministic name+alias matching, self-growing aliases, NO fuzzy/LLM identity guessing. Plus role-driven context assignment.
- Tasks/events/commitments are unchanged this increment.

## Part A — Notes: durable-reference-only, with match-and-merge

### A1. Tighten note extraction (classifier)

In `classifySystem` (`Prompts.fs`), replace the `notes` entity guidance (*"any factual information worth storing"*) with durable, cross-topic reference facts only, plus explicit exclusions. New text:

> `"notes": ["only DURABLE reference facts worth keeping long-term and across conversations — e.g. account/policy/membership numbers, addresses, contact details, medical records, standing preferences. Do NOT create notes for per-message observations, status updates, or anything specific to a single ongoing conversation — those belong to the topic. Empty array if none."]`

Tighten `noteCreateSystem` to describe an evolving, sectioned reference document (not a per-message capture).

### A2. Note match-vs-update (new)

Replace the unconditional create for notes with a match-or-merge path mirroring the topic flow. Tasks/events/commitments keep the existing create-only `writeEntities` writer.

For each note intent:

1. **List existing notes.** A new helper (analogous to `activeTopics`) reads `notes/` and yields `(slug, title, summary)`, where `summary` is the note's first body section / leading text.
2. **Embedding shortlist.** Embed the note intent; cosine against `title + "\n" + summary` for each note; filter ≥ `NoteMatch:SimilarityFloor`; sort desc; take `NoteMatch:TopK`.
3. **Empty shortlist → create new** (today's behaviour, via the existing note writer/`freePath`).
4. **Non-empty shortlist → LLM confirm.** A new `noteMatchSystem` prompt is given the intent and candidate notes; it returns the existing `TopicMatch` JSON shape (`match`, `topic_slug` reused as the matched note slug, `confidence`, `new_topic_title` reused as a suggested new note title). Parsed with the existing `Prompts.parseTopicMatch`.
   - **Match** → read the matched note, run a new `noteUpdateSystem` prompt (analogous to `topicUpdateSystem`) to merge the new fact into the note body, and rewrite the file **in place**. Refresh frontmatter: `last_verified` = message date; union `people_linked` and `context` with the message's; keep `tags`; keep the original `source`.
   - **No match** → create new.

Reusing `TopicMatch`/`parseTopicMatch` avoids a parallel parser; the `topic_slug`/`new_topic_title` fields carry the matched-note-slug / new-note-title semantically. This reuse is intentional and documented here so the field names are not mistaken for a topics-only coupling.

## Part B — Tighten topic matching (consolidate)

Config + prompt only; no structural change to the topic flow.

### B1. Lower the auto-create threshold

Change `TopicMatch:SimilarityFloor` from `0.5` to `0.35` (`appsettings.json`). More related topics reach the LLM-confirm step instead of auto-spawning when nothing clears the old floor.

### B2. Rebalance the match prompt

Rewrite `topicMatchSystem` (`Prompts.fs`) to prefer matching when the subject/incident/person overlaps:

- Lower the confidence cutoff from 0.75 to **0.6**.
- Replace *"Prefer creating a new topic over forcing a weak match"* with guidance to match a follow-up, status update, or related question about the same incident/event/person to the existing topic.
- Keep worked examples of same-topic vs different-topic. The LLM-confirm remains the safeguard against bad merges.

## Part C — Alias-aware, role-contextual people

### C1. Schema: add aliases

Add `Aliases: string array` to the `Person` record (`KnowledgeBase.fs`), mirrored as snake_case `aliases` via the existing `UnderscoredNamingConvention`. Add `aliases` to the DESIGN §4.10-area Person schema example. Existing files without the key deserialize to an empty array.

### C2. Deterministic name+alias resolution

Replace the exact-single-slug `personExists` check with a resolution index built once per message:

- Scan `people/**`; for each person file, map its *title-slug* and every *alias-slug* → that person's file path.
- A mention resolves to an existing person iff `Naming.slug mention` is a key in the map. Exact/normalized match only — no fuzzy or embedding matching.

### C3. Self-growing aliases (safe gate)

When a mention does **not** resolve via the index, run the stub generator as today, then gate on the **canonical title slug** the generator proposes:

- If `Naming.slug record.Title` already matches an existing person (by title or alias), **append the surface mention to that person's `aliases`** and rewrite that person file — do **not** create a new file.
- Otherwise create a new person file (the generator may seed `aliases` from explicit cues in the message).

The merge gate is exact slug-equality on the canonical title; the LLM only proposes a canonical name, never selects "which existing person this is," so two distinct people sharing a surface form (e.g. two "John"s) cannot be merged.

### C4. Role-driven context

Rewrite `personStubSystem` (`Prompts.fs`) so `context` is chosen from the person's **role/relationship**, not the chat's context, with an explicit mapping:

- doctor / dentist / specialist / physio / nurse → `medical`
- teacher / principal / coach / tutor → `school`
- accountant / advisor / banker / broker → `finance`
- colleague / manager / client / boss → `professional`
- relative / friend / neighbour → `family`
- unknown role → fall back to the message's first known context, else `family`.

Change the parse-failure fallback record (`Pipeline.fs`) so `Context` uses the message's first known context instead of the hard-coded `[| "family" |]`; the `family` default applies only when the message has no known context.

## Configuration

| Key | Default | Status |
|-----|---------|--------|
| `NoteMatch:TopK` | 5 | new |
| `NoteMatch:SimilarityFloor` | 0.35 | new |
| `TopicMatch:SimilarityFloor` | 0.35 | changed (was 0.5) |
| `TopicMatch:TopK` | 5 | unchanged |

`NoteMatch:TopK` / `NoteMatch:SimilarityFloor` are added to `PipelineDeps` and bound in `Program.fs`, mirroring the `TopicMatch` wiring.

## Testing

Deterministic tests via the in-memory `FakeVault` / `FakeChatClient`, mirroring the existing topic-match tests (scripted model replies):

- **Note match → merge:** an existing note in the shortlist that the scripted LLM confirms as a match results in the existing note file being rewritten (merged body, refreshed `last_verified`); no new file is created.
- **Note no-match → create:** scripted no-match (or empty shortlist) creates a new note file.
- **Person alias resolution:** a mention equal to a recorded alias resolves to the existing person file; no duplicate is written.
- **Person self-grow:** a novel surface form whose generator-proposed canonical title slug matches an existing person appends the mention to that person's `aliases` (no new file).
- **Role-driven context:** a doctor stub is written under `people/medical/`, not `people/family/`.

Not unit-tested (LLM judgment): the tightened classifier / topic / note *criteria*. These are validated empirically by the clean re-run and the resulting file counts.

## Out of scope

- Dedup for tasks, events, and commitments (still create-only via `writeEntities`).
- Fuzzy/embedding person matching (explicitly declined — exact name+alias only).
- Retroactive consolidation of previously-created files (the KB has been cleared; the clean re-run is the validation).

## Operational note

These changes alter the per-message pipeline, so the clean re-run of `process-since` happens **after** this work is built and reviewed. People, contexts, and meta were retained when the KB was cleared; the alias index and role-context logic operate over the retained people on the next run.
