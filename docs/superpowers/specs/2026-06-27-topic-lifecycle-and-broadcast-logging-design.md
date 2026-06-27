# Topic lifecycle & broadcast logging

Status: approved design (2026-06-27)

## Problem

Two scaling defects surfaced in multi-day runs:

1. **Topics never leave `active`.** Every thread becomes a `topics/active/*.md` with
   `status: active` and stays there forever. The digest *counts* stale topics but nothing
   resolves or retires them, so the active set grows without bound and the "active topics"
   view degrades.
2. **Broadcast feeds proliferate topics.** WhatsApp newsletters / statuses (`@newsletter`,
   `@broadcast` — CityPower outages, crime alerts) emit dozens of messages a day, each
   currently spawning its own throwaway topic.

## Constraints honoured

- **The vault is append-only and never deletes** (DESIGN §4/§8; `IVault` has no move/delete).
  Therefore topic lifecycle is expressed purely through the `status` frontmatter field — files
  are never moved between directories. All topics remain at `topics/active/{slug}.md`; their
  on-disk path never changes, so **no backlink ever breaks** and no reference rewriting is
  needed.
- Determinism: status changes are either deterministic (the sweep) or temperature-0 reproducible
  (the inline resolution judgement).

## Part 1 — Topic lifecycle

### States (`status` frontmatter)

- `active` — default for new and ongoing topics.
- `resolved` — the matter has concluded (set inline, best-effort).
- `archived` — retired by the dormancy sweep; excluded from matching and from the digest's
  stale-topic list. It still appears in the topic index, but under its own trailing `archived`
  group rather than among active topics (kept for the record, out of the active view).

Files physically stay in `topics/active/`. The Indexer already groups topics by status
(`active` → `resolved` → other); `archived` renders as its own trailing group.

### Transition 1: active → resolved (inline, per message, best-effort)

The topic-update step already makes one LLM call to rewrite the topic body. Extend it to also
report whether the thread is now concluded — **no extra LLM call**.

- `topicUpdateSystem` is instructed to begin its reply with a single line
  `STATUS: active` or `STATUS: resolved`, followed by the body.
- A tolerant parser `parseTopicUpdate (raw) : (resolved: bool * body: string)` reads the first
  non-blank line; if it matches `^\s*STATUS:\s*(resolved|active)\b` (case-insensitive) it sets
  the flag and strips that line; **anything else (missing/garbled) defaults to `active`** and the
  whole reply is treated as the body. Conservative by design — granite is shaky, and a
  false-negative (staying active) only delays archival, while a false-positive would hide a live
  topic.
- Only **matched (existing)** topics can be resolved; a brand-new topic is always `active`.
- When resolved is reported, the pipeline writes the merged topic frontmatter with
  `status = "resolved"`.

### Transition 2: active/resolved → archived (dormancy sweep in `/reindex`)

Deterministic, LLM-free, runs inside `Indexer.regenerate`:

- A `resolved` topic idle ≥ `Topics:ResolvedArchiveAfterDays` (default **14**) → `archived`.
- An `active` topic idle ≥ `Topics:DormantArchiveAfterDays` (default **90**) → `archived`
  (catch-all for threads that fizzle without an explicit resolution).
- "Idle" = `now − last_updated`.

Implemented as a pure function for testability:

```
type TopicSweepConfig = { ResolvedArchiveAfterDays: int; DormantArchiveAfterDays: int }

/// Given a topic's status + last_updated and the current time, return the new status
/// (Some s when it changed, None when unchanged). Pure; no IO.
nextTopicStatus : TopicSweepConfig -> now:DateTime -> status:string -> lastUpdated:string -> string option
```

`regenerate` (or a new `sweepTopics` it calls first) loads every topic, computes
`nextTopicStatus`, and for each change writes the file back to **its existing path** with the
updated `status` (body preserved). It then prunes any now-`archived` topic from every channel's
`active_topics` list (bounded — channels are few; same-path writes).

`regenerate`/`sweepTopics` take a `now: DateTime` parameter (callers pass `DateTime.Now`) so the
sweep is unit-testable.

### Transition 3: resolved/archived → active (re-activation)

Because no files move, the matching candidate set (`topics/active/`) now physically contains
topics of every status. Matching must therefore filter by status:

- The embedding shortlist (`Pipeline` topic-match, currently `ListFiles "topics/active"`) and the
  `get_topics` tool exclude `status: archived`; they offer `active` + `resolved` as candidates.
- When a new message matches a `resolved` topic, the pipeline sets it back to `active`
  (re-activation). `archived` topics are never matched, so a message about a long-dead matter
  starts a fresh topic.

### Digest

Stale-topic surfacing already filters topics; it must additionally exclude `archived` (they are
intentionally retired, not "stale and needing attention").

## Part 2 — Broadcast logging

For `isBroadcastChannel chatJid`, a **non-noise** message is recorded as a searchable log entry
with **no topic and no entities**.

- New result case: `PipelineResult.Logged`.
- After classification, if the channel is a broadcast and the message is not noise, the pipeline
  writes the message record (`noise: false`, `topic: ""`, content preserved via
  `mediaHeader + msg.Content`, empty spawned lists), calls `updateChannel`, and returns `Logged`
  — short-circuiting before topic-match, entity creation, people, and relationships.
- `ProcessMessageHandler.toHttp`: `Logged` → `200 {logged: true}` (parallel to `Skipped`).
- This **subsumes** the current broadcast handling, which cleared tasks/events/commitments but
  still created a topic. That entity-clearing block is removed; the early `Logged` return replaces
  it. (`isAutomatedNoise` TAP pre-filter and the general noise path are unchanged.)

## Configuration

`appsettings.json` under `Ollama`-style section:

```
"Topics": { "ResolvedArchiveAfterDays": 14, "DormantArchiveAfterDays": 90 }
```

Read in `Program.fs`; defaults applied when absent. The sweep config is passed into
`Indexer.regenerate`.

## Testing

Pure / unit:
- `nextTopicStatus`: active<90d → None; active≥90d → archived; resolved<14d → None;
  resolved≥14d → archived; archived → None; unparseable date → None (safe).
- `parseTopicUpdate`: `STATUS: resolved` → (true, body without the line); `STATUS: active` →
  (false, body); missing marker → (false, whole reply); garbled/lowercase tolerated.

Pipeline (FakeVault/FakeChat):
- A matched topic whose update reply says `STATUS: resolved` ends with `status: resolved`.
- A new topic is never resolved on creation.
- A message matching a `resolved` topic re-activates it (`status: active`).
- `sweepTopics` with a fixed `now`: a stale `active` topic → `archived` and is pruned from its
  channel's `active_topics`; a fresh topic is untouched.
- Matching excludes `archived` candidates (an archived topic is not matched even when textually
  similar; a new topic is created).
- Broadcast non-noise → `Logged`: no topic/task/event/commitment/note file is written, and the
  message record exists with `noise: false` and the raw content preserved.

Endpoint:
- `Logged` → 200 `{logged:true}`.

## Out of scope

- Physical `topics/resolved/` and `topics/archive/` directories (would require deletes/moves the
  vault forbids).
- Backlink rewriting (unnecessary — paths never change).
- LLM-judged resolution as a separate pass or for new topics.
- Message archival to `messages/archive/` (separate DESIGN §9 item).
