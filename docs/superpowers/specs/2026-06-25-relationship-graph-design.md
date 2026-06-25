# Relationship Graph — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorm), pending implementation plan
**DESIGN reference:** §9 "Relationship graph" — *a `relationships/` directory mapping explicit person-to-person relationships (e.g. `ethan-dr-naidoo: patient-doctor`) to support relational queries.*

## Summary

Add **typed person-to-person edges** on top of the existing person files, stored as one
markdown file per edge, captured by a hybrid of inline LLM extraction (per message) plus
deterministic reconciliation in `/reindex`, and consumed through three surfaces: a read-only
LLM tool, HTTP endpoints, and a reindex-built browsable index.

This is the first increment of the DESIGN §9 relationship graph. It deliberately keeps the
scope to person↔person edges with a single relation per pair.

## Goals

- Capture explicit, typed relationships between people the KB already knows about.
- Make relationships queryable by the LLM agent loop (the core "LLM-queryable graph" use), by
  external/programmatic callers (HTTP), and by a human browsing the vault (Obsidian).
- Inherit the existing idempotency and concurrency model for free, so the live pipeline and the
  bulk-reprocess job can both write edges without colliding.

## Non-goals (this increment)

- Multiple typed edges per pair (one edge per pair; higher-confidence relation wins).
- LLM-driven backfill inside `/reindex` (bulk-reprocess over history is the backfill path; reindex stays LLM-free).
- Person↔organisation or person↔location edges.
- Relationship strength / recency decay / weighting.
- Editing or deleting edges via the API (the vault is append-only / never-delete).

## 1. Concept type & storage

**One markdown file per edge** at `relationships/{slug-a}-{slug-b}.md`, where the two person
slugs are **sorted alphabetically** to form the filename. The path is therefore a deterministic
function of the *pair*, independent of mention order or direction — this is the idempotency key,
mirroring the existing message-file-existence check.

Example `relationships/dr-naidoo-ethan.md`:

```yaml
---
type: Relationship
title: Ethan ↔ Dr Naidoo
from: people/family/ethan.md          # subject
to: people/medical/dr-naidoo.md       # related person
relation: patient-doctor              # controlled enum
descriptor: paediatrician since 2022  # free-form, nullable
confidence: high                      # high | medium | low
people: [ethan, dr-naidoo]            # slug refs, for cheap indexing / tool lookup
source: messages/.../2026-06-10T11-04-00.md
---
Ethan is a patient of Dr Naidoo (paediatrician).
```

### Relation vocabulary (enum + free-form descriptor)

- `relation` is a **controlled enum** (the queryable field):
  `parent-child`, `sibling`, `partner`, `patient-doctor`, `student-teacher`, `colleague`,
  `friend`, `other`.
- `descriptor` is **free-form** nullable text capturing nuance (e.g. `paediatrician since 2022`).
  The model fills it for richness; queries hit the enum.

### Direction

Directed enums encode direction via `from` / `to`. For an enum of the form `X-Y`, `from` holds
role X and `to` holds role Y:

- `patient-doctor` → from = patient, to = doctor
- `parent-child` → from = parent, to = child
- `student-teacher` → from = student, to = teacher

Symmetric enums (`sibling`, `partner`, `colleague`, `friend`, `other`) ignore direction. The
canonical alphabetical filename ordering still applies in all cases; `from`/`to` preserve the
semantic direction independently of the filename.

### One edge per pair

A pair maps to exactly one edge file. If a second relation surfaces for the same pair, the
**higher-confidence** relation wins the `relation` field; the body/descriptor carries any
nuance. Multiple distinct typed edges per pair is an explicit non-goal here.

### Code

- New `[<CLIMutable>]` `Relationship` record in `KnowledgeBase.fs` (public, not `private` — see
  the `fsharp-private-record-serialization` lesson: a `private` record serializes to `{}` under
  YamlDotNet / STJ). Fields: `Type`, `Title`, `From`, `To`, `Relation`, `Descriptor`,
  `Confidence`, `People` (string array), `Source`.
- New `Naming.relationshipPath` that takes the two person slugs, sorts them alphabetically, and
  returns `relationships/{a}-{b}.md`.

## 2. Capture — the hybrid source

### Inline (primary)

In `Pipeline.processMessage`, **after** the existing people-resolution step (so all co-mentioned
people are already resolved or stubbed), add **one LLM step**, fired **only when ≥2 people were
resolved** for the message:

- A new `Prompts.relationshipExtractSystem` prompt instructs the model to emit directed edges
  among the named people, choosing a `relation` from the enum, an optional `descriptor`, and a
  `confidence`.
- A new `Prompts.parseRelationships` parser (System.Text.Json, snake_case, fence-tolerant,
  `Result`-returning — same discipline as `parseClassification` / `parseTopicMatch`; never throws
  on bad model output).
- Write edges with `confidence` of **high or medium**. Idempotent on the canonical path:
  - path does not exist → write the edge;
  - path exists → best-effort reconcile: overwrite (taking the new `relation`/`confidence`/
    `descriptor`/`source`) only when the new edge is **strictly higher-confidence**; otherwise
    leave the existing file untouched. Equal-or-lower confidence is a no-op, so re-processing the
    same message — or a bulk-reprocess replay — produces byte-identical output (true idempotency).
    The trade-off is that `source` is not refreshed on an equal-confidence re-mention; the earliest
    high-confidence source is retained.

This step is skipped entirely for messages with fewer than two resolved people, so the common
case adds no LLM call.

### Backfill (free, via bulk reprocess)

The existing `POST /messages/process-since` bulk job re-runs `processMessage` over historical
messages in order (idempotent / resumable). Re-running it after this feature ships populates
relationships from past messages. No separate LLM backfill is built into `/reindex`.

### Reconcile (reindex, deterministic / LLM-free)

`/reindex` stays LLM-free. Add a deterministic `renderRelationships` pass to `Indexer` that:

- builds the human-readable index view (`relationships/index.md`, matching every other index and `loadAll`'s `index.md` skip filter);
- drops edges whose referenced person files no longer exist (dangling-ref cleanup), consistent
  with how `loadAll` already skips unparseable files;
- contributes a `Relationships` count to `IndexSummary`.

## 3. Query surfaces (all three)

### LLM tool

`get_relationships(person_slug)` in `Tools.fs`: returns the edge files where the given person is
`from` or `to` (matched via the `people` slug array). Registered alongside `get_people` wherever
the tool set is offered to the agent loop, so classification / entity / digest steps can pull a
person's relationships as vault context.

### HTTP

- `GET /relationships` — the whole graph as JSON (list of edges).
- `GET /relationships/{slug}` — one person's edges as JSON.

Both mirror the existing endpoint and `toHttp` mapping style in `ProcessMessage.fs` / `Program.fs`.

### Reindex view

`relationships/index.md`, each edge rendered as a wikilink with its relation and descriptor —
browsable in Obsidian. Regenerable; written by `renderRelationships`.

## 4. Testing

**`Core.Tests`** (FakeVault, no live services):

- Canonical path determinism: order-invariance and direction-invariance of `relationshipPath`.
- Idempotent re-write: writing the same edge twice yields one file.
- Confidence-upgrade reconciliation: a higher-confidence edge replaces the relation; a
  lower-confidence one does not.
- Dangling-ref drop: reindex omits edges pointing at non-existent person files.
- `parseRelationships`: well-formed model output parses; garbage / fenced output returns `Error`
  rather than throwing.
- `get_relationships` tool output: returns matching edges for a person, empty for an unknown one.
- Inline step gating: skipped when fewer than two people resolved.

**`Tests`** project:

- Endpoint → HTTP mapping for `GET /relationships` and `GET /relationships/{slug}`.

## 5. Files touched (anticipated)

- `KnowledgeBase.fs` — `Relationship` record, `Naming.relationshipPath`.
- `Prompts.fs` — `relationshipExtractSystem`, the extracted DTO + `parseRelationships`.
- `Pipeline.fs` — inline extraction/reconcile step after people resolution.
- `Tools.fs` — `get_relationships`.
- `Indexer.fs` — `renderRelationships`, `IndexSummary.Relationships`, wire into `regenerate`.
- `ProcessMessage.fs` / `Program.fs` — the two GET endpoints + handler mapping.
- Tests in `Core.Tests` and `Tests` as above.
- `docs/DESIGN.md` §9 — mark the relationship-graph item as delivered (first increment) once built.
