# Remaining Entity Writers (Increment A) — Design Spec

> **Date:** 2026-06-22
> **Status:** Approved for planning
> **Scope:** The first deferred increment after the per-message pipeline spine — write the entity types the classifier already extracts but the spine drops: Events, Commitments, Notes, and Person-stubs.
> **Builds on:** `docs/superpowers/specs/2026-06-22-per-message-pipeline-design.md` (the spine) and `docs/DESIGN.md` (authoritative for §4 schemas, §7 prompts, §8 naming).

---

## 1. Goal

The per-message pipeline spine classifies each message into four entity buckets (`tasks`, `events`, `commitments`, `notes`) plus `people_mentioned`, but only writes **Tasks**. This increment writes the rest: **Event**, **Commitment**, and **Note** files from the classifier's buckets, and **Person-stub** files for mentioned people who don't yet exist in the vault. After this increment, every entity the classifier surfaces is persisted, and tasks/topics that reference people point at real (if minimal) person files.

No new ports, adapters, configuration, or HTTP surface. Everything reuses the spine's `IVault` / `IChatClient` / `Agent` machinery; all writes remain in `Pipeline.fs`; the model continues to call only read-only tools.

---

## 2. Scope

### In scope
- New `[<CLIMutable>]` domain records: `Event`, `Commitment`, `Note`, `Person` (DESIGN §4.3/§4.8/§4.10/§4.1).
- Extend the existing `Message` record with `SpawnedEvents` / `SpawnedNotes`, and the `Topic` record with `SpawnedEvents` (these fields already exist in DESIGN §4.5/§4.6).
- `Naming` helpers: `eventPath`, `commitmentPath`, `notePath`, `personPath`.
- Four new prompt constants in `Prompts.fs`: `eventCreateSystem`, `commitmentCreateSystem`, `noteCreateSystem` (newly authored, §7.4 style), and `personStubSystem` (verbatim from DESIGN §7.5).
- A shared `writeEntities` helper in `Pipeline.fs` that generalizes the spine's task-creation pattern (prompt → validate → fallback → collision-guard → write); the existing task step is refactored to use it.
- Pipeline integration: create events, commitments, notes, and person-stubs on the signal path; populate the new spawned-* links on the Message and Topic.
- Unit tests with in-memory fakes for all of the above.

### Out of scope (later increments / unchanged)
- `Channel.last_processed` and index regeneration (increment B).
- Digests / briefings (increment C).
- DESIGN §9 enhancements (embedding topic matching, voice notes, calendar sync, archive policy, relationship graph, conflict detection).
- Live-service integration tests.
- The noise path, idempotency guard, classification, and topic match/create steps are unchanged.

---

## 3. Domain records (`KnowledgeBase.fs`)

All `[<CLIMutable>]`, serialized via the existing `Frontmatter` (YamlDotNet, underscored naming). Field sets follow DESIGN §4; the stub/minimal subset is used where a full record needs data we don't have yet.

```
Event       (§4.3): Type, Title, When (ISO datetime string), AllDay (bool),
                    Context string[], Location, People string[], Topic,
                    TasksLinked string[], ReminderDaysBefore (int)
Commitment  (§4.8): Type, Title, Status (= "unresolved"), Priority, Due,
                    Context string[], Topic, TaskAssigned (= ""),
                    EscalateAfterDays (int), SourceMessage
Note        (§4.10): Type, Title, Context string[], PeopleLinked string[],
                    Tags string[], Source, LastVerified
Person      (§4.1 stub subset): Type, Title, Role, Context string[],
                    Channel, Phone, Email, Tags string[]
```

**Extensions to existing records:**
- `Message`: add `SpawnedEvents string[]`, `SpawnedNotes string[]` (alongside existing `SpawnedTasks`).
- `Topic`: add `SpawnedEvents string[]` (alongside existing `SpawnedTasks`).

Commitments and people are intentionally **not** added to the Message `spawned_*` set — DESIGN §4.5 only defines `spawned_tasks`/`spawned_events`/`spawned_notes`.

---

## 4. Naming (`KnowledgeBase.fs`, `Naming` module)

| Helper | Result | Notes |
|---|---|---|
| `eventPath (when: DateTime) (title)` | `events/{yyyy}/{MM}/{slug}-{yyyy-MM-dd}.md` | Date-pathed per DESIGN §8. |
| `commitmentPath title` | `commitments/{slug}.md` | |
| `notePath title` | `notes/{slug}.md` | |
| `personPath (context) (slug)` | `people/{context}/{slug}.md` | `context` is the stub's first/primary context, defaulting to `family`. |

Slug rules reuse the existing `Naming.slug`.

---

## 5. Prompts (`Prompts.fs`)

Four new system-prompt string constants, each in the established §7.4 "respond ONLY with a complete markdown file (frontmatter between `---` fences, then body)" style:

- **`eventCreateSystem`** — extract `when` as ISO 8601; the message timestamp is supplied as the reference point so relative dates ("next Friday", "the 19th") resolve correctly; set `all_day` when no time is given; fill `location`/`people` when named.
- **`commitmentCreateSystem`** — `status: unresolved`, infer `due`, `escalate_after_days` (default 7), `task_assigned: null`.
- **`noteCreateSystem`** — concise factual body + `tags`.
- **`personStubSystem`** — verbatim from DESIGN §7.5 (title = name or role, infer role + context, unknown fields null, body ends with "⚠ Stub — details to be completed.").

These return markdown files (not JSON), so they need no new parsers — they reuse `MarkdownFile.FromString` + `Frontmatter.deserialize` for validation, exactly like the task step.

---

## 6. Pipeline integration (`Pipeline.fs`)

### 6.1 Shared entity-writer helper

Factor the spine's task-creation loop into a reusable helper so all five entity types share one validated, collision-safe write path:

```
type EntitySpec<'T> =
    { prompt      : string                          // system prompt
      buildUser   : intent:string -> string         // user message for one intent
      titleOf     : 'T -> string                     // record -> title (for the path)
      pathOf      : 'T -> string                      // record -> vault path
      fallback    : intent:string -> 'T }            // deterministic record on invalid output

writeEntities deps (spec: EntitySpec<'T>) (intents: string list) : string list
```

For each intent: run `Agent.runConversation deps.Chat [] spec.prompt (spec.buildUser intent)`, strip code fences, parse with `MarkdownFile.FromString`; accept only if the frontmatter deserializes to `'T` with a non-empty title, else build `spec.fallback intent` and serialize it. Compute the path via `spec.pathOf`; if it already exists in the vault, append a numeric suffix until free; write; return the path. The existing **task** step is rewritten in terms of this helper (its current bespoke validation/collision logic moves into the helper, written once).

### 6.2 Signal-path ordering

Unchanged through classify and topic match/create, then:

```
tasks      = writeEntities deps taskSpec       classification.Entities.Tasks
events     = writeEntities deps eventSpec      classification.Entities.Events
commitments= writeEntities deps commitmentSpec classification.Entities.Commitments
notes      = writeEntities deps noteSpec       classification.Entities.Notes
people     = writePersonStubs deps classification.PeopleMentioned
→ write Message  (SpawnedTasks=tasks, SpawnedEvents=events, SpawnedNotes=notes)
→ topic update   (append SpawnedTasks + SpawnedEvents)
```

Events created this run also receive the run's task paths in `tasks_linked` (best-effort: all tasks from the same message).

### 6.3 Person-stub flow (`writePersonStubs`)

For each name in `classification.PeopleMentioned`: slug it; scan the vault's `people/` tree (`IVault.ListFiles` over `people/` and its subdirectories) for a file whose name matches `{slug}.md`. If none exists, run `personStubSystem`, derive the subdirectory from the stub's first context (default `family`), and write `people/{context}/{slug}.md`. Existing people are never overwritten. Returns the list of created stub paths (for logging/return summary only; not added to the Message `spawned_*`).

### 6.4 Undated events

If the event-creation step yields no parseable `when`, fall back to the **message timestamp**: path the event under the message's date (`events/{msg-yyyy}/{MM}/{slug}-{msg-date}.md`), set `When` to the message timestamp, and add an "inferred date — confirm" note to the body so it can be corrected later. Events are therefore always writable; nothing is dropped.

---

## 7. Idempotency, collisions, deletes

- **Idempotency:** the spine's top-of-pipeline message-file-existence guard already makes whole-message reprocessing a no-op; the new writes inherit this — no new idempotency logic.
- **Collisions:** the `writeEntities` helper appends `-2`, `-3`, … on path clashes (commitments/notes by slug; events by same-title-same-date). Person-stubs use existence-as-guard (skip if present).
- **Deletes:** none. Writes are create / overwrite-body only; person files are never overwritten.

---

## 8. Testing (unit, in-memory fakes — no live services)

- Codec round-trip for `Event`, `Commitment`, `Note`, `Person`, and the extended `Message`/`Topic`.
- `Naming` tests for `eventPath` (date-pathed), `commitmentPath`, `notePath`, `personPath`.
- Pipeline signal path: script entity-creation replies; assert files land at the expected paths and that the Message references spawned events/notes and the Topic references spawned events.
- Undated event → message-date path + inferred-date flag in body.
- Person-stub: created when absent; **not** written when a matching `people/{ctx}/{slug}.md` is pre-seeded in the `FakeVault` (no overwrite).
- Malformed entity reply (no frontmatter / empty title) → deterministic fallback file whose frontmatter parses to the right record.
- Regression: the task-loop → `writeEntities` refactor keeps all existing task tests green (e.g. the `call-acrobranch.md` signal test).

---

## 9. Files touched

- `src/Nameless.TaskList.Core/KnowledgeBase.fs` — new records, record extensions, `Naming` additions.
- `src/Nameless.TaskList.Core/Prompts.fs` — four new prompt constants.
- `src/Nameless.TaskList.Core/Pipeline.fs` — `writeEntities` helper, task-step refactor, entity/person integration, record-linking.
- `tests/Nameless.TaskList.Core.Tests/` — new/extended tests as in §8.

No new ports, adapters, configuration, or web-host changes.

---

## 10. Open follow-ups (later increments)
1. **B** — `Channel.last_processed` + index regeneration (so the new entities are queryable from index files).
2. **C** — daily/weekly digests reading these entities + priority weights.
3. **D** — DESIGN §9 enhancements.
4. **E** — live-service integration tests.
