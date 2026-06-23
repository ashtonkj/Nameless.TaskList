# Vault Consistency (Increment B) — Design Spec

> **Date:** 2026-06-22
> **Status:** Approved for planning
> **Scope:** The second deferred increment after the per-message spine + entity writers — keep the vault queryable: update `Channel` files inline per message, and regenerate `index.md` files on demand.
> **Builds on:** the spine (`2026-06-22-per-message-pipeline-design.md`) and entity writers (`2026-06-22-entity-writers-design.md`). KB conventions authoritative in `docs/DESIGN.md` (§4 schemas, §5 index examples, §8 naming).

---

## 1. Goal

The pipeline writes Message/Topic/Task/Event/Commitment/Note/Person files but never updates the `Channel` record or any `index.md`, so the vault has no roll-up views and channels never record activity. This increment adds two independent pieces:

1. **Channel update (inline, per message):** ensure a `Channel` file exists for the message's chat and record activity on it (`last_processed`, `message_count`, `active_topics`).
2. **Index regeneration (separate, on-demand):** a standalone operation that scans the vault and rebuilds the seven `index.md` files deterministically, exposed via `POST /reindex`.

These keep the vault queryable without coupling a whole-vault scan into the per-message hot path.

---

## 2. Scope

### In scope
- Extend `IVault` with `ListFilesRecursive : relDir -> string list` (filesystem adapter + in-memory fake).
- `Channel` update step in `Pipeline.processMessage` (create-if-missing + full update), in both the noise and signal branches.
- A new `Indexer` module in Core that regenerates indexes for all written concept types: tasks, topics, events, commitments, notes, people, channels.
- `POST /reindex` endpoint in the web host that runs the indexer against the configured vault and returns per-index counts.
- `Naming.channelPath` helper.
- Unit tests with in-memory fakes for the channel update, the indexer, and the recursive listing.

### Out of scope (later / unchanged)
- Digests / briefings (increment C) and `priority-weights.md` scoring.
- DESIGN §9 enhancements; live-service integration tests.
- The classification / topic / entity-writer steps are unchanged. `PipelineResult` is unchanged.
- Scheduling the reindex (the endpoint is the trigger; a schedule can call it later).

---

## 3. Port extension (`Ports.fs` + adapters)

Add one member to `IVault`:

```
ListFilesRecursive : relDir: string -> string list   // all files under the tree, vault-relative, '/'-separated; [] if dir absent
```

- `FileSystemVault`: `Directory.GetFiles(full relDir, "*", SearchOption.AllDirectories)` mapped to vault-relative `/`-separated paths; `[]` if the directory doesn't exist.
- `FakeVault`: filter the in-memory key set by the `relDir + "/"` prefix.

The existing `ListFiles` (single directory) is unchanged.

---

## 4. Channel update (inline, `Pipeline.fs`)

### 4.1 Path & naming
`Naming.channelPath (slug) -> "channels/whatsapp/{slug}.md"`. The platform family is `whatsapp` for this increment (the only message source). `slug` is `Naming.slug msg.NormalizedChatName` — the same slug used for the message directory, so channel and messages line up.

### 4.2 Behavior — `updateChannel deps msg channelSlug (topic: string option)`
1. Compute `path = Naming.channelPath channelSlug`.
2. Read the existing `Channel` (if `path` exists, parse frontmatter + keep body); else start from a stub:
   - `Type="Channel"`, `Title = msg.NormalizedChatName`, `Platform = if msg.IsGroup then "whatsapp-group" else "whatsapp-direct"`, `Context = ""`, `People = [||]`, `SignalWeight = "medium"`, `MessageCount = 0`, `LastProcessed = <min>`, `ActiveTopics = [||]`; body `""`.
3. Apply the update: `LastProcessed = msg.Timestamp`, `MessageCount = existing + 1`, and if `topic = Some t` and `t` is not already in `ActiveTopics`, append it.
4. Write `path` (re-serialize frontmatter, preserve the existing body). Never delete.

### 4.3 Where it runs
Called once per processed message before returning:
- **Noise branch:** `updateChannel deps msg channelSlug None` (counts the message, sets `last_processed`; no topic).
- **Signal branch:** `updateChannel deps msg channelSlug (Some topicPath)` after the topic path is known.

### 4.4 Idempotency
The spine's top-of-pipeline message-file-existence guard makes whole-message reprocessing a no-op, so `message_count` counts unique processed messages — the channel update inherits this and needs no extra guard.

---

## 5. Indexer (`Indexer.fs`, Core)

### 5.1 Entry point
`Indexer.regenerate : IVault -> IndexSummary` where `IndexSummary` carries per-type item counts (returned to the endpoint). It calls one private renderer per concept type; each renderer:
1. Enumerates the type's files via `ListFilesRecursive` over the type's root (`tasks`, `topics`, `events`, `commitments`, `notes`, `people`, `channels`), excluding any existing `index.md`.
2. Reads + parses each file's frontmatter into the type's record; files that fail to parse are skipped (counted as skipped, not fatal).
3. Groups/sorts per §5.3 and renders a body.
4. Writes `{type-root}/index.md` with `type: Index` frontmatter (`title`, `last_updated` = now) + the body.

A malformed individual file never aborts the whole reindex. Reindex is a deterministic full rebuild → idempotent.

### 5.2 Recursion roots
- tasks: `tasks/` (covers `pending/`, `in-progress/`, `done/…`)
- topics: `topics/` (`active/`, `resolved/`)
- events: `events/` (`{yyyy}/{MM}/`)
- commitments: `commitments/`
- notes: `notes/`
- people: `people/` (context subdirs)
- channels: `channels/` (`whatsapp/`)

### 5.3 Per-index format (moderate-rich, deterministic)
Each line is `- [[{vault-relative-path-without-.md}]] — {metadata}`. Sorting is stable (specified key, then path).

| Index | Grouping | Per-item metadata | Sort |
|---|---|---|---|
| `tasks/index.md` | by `status` (pending, in-progress, blocked, done, cancelled) | `due {due} · {context joined}` | pending first; within a group by priority rank (critical>high>medium>low) then due then path |
| `topics/index.md` | by `status` (active, resolved, …) | `last updated {last_updated}` | active first; within group by `last_updated` desc then path |
| `events/index.md` | none (single chronological list) | `{when} · {context}` | by `when` ascending then path |
| `commitments/index.md` | by `status` (unresolved, resolved) | `due {due} · {priority}{ ⚑ if task_assigned empty}` | unresolved first; by due then path |
| `notes/index.md` | by first `context` (or "uncategorised") | `{tags joined}` | context name then path |
| `people/index.md` | by directory context (`people/{ctx}/`) | `{role}` | context then path |
| `channels/index.md` | none | `{signal_weight} · last processed {last_processed} · {active_topics count} active` | by `last_processed` desc then path |

Empty groups are omitted. An index with zero items still writes a valid (empty-body) file.

### 5.4 Endpoint
`POST /reindex` (web host): resolve the registered `IVault`, call `Indexer.regenerate`, return `200` with the `IndexSummary` (e.g. `{ tasks: 12, topics: 4, ... , skipped: 1 }`); `500` on unexpected error.

---

## 6. Idempotency, deletes, errors
- Channel update: create/overwrite-frontmatter only, body preserved, never deletes; counts inherit message idempotency (§4.4).
- Indexer: full deterministic rebuild; overwrites each `index.md`; never deletes other files; malformed source files are skipped, not fatal.
- Endpoint: unexpected exception → `500`.

---

## 7. Testing (unit, in-memory fakes)
- `ListFilesRecursive`: filesystem adapter against a temp dir with nested files; `FakeVault` prefix behaviour; `[]` for absent dir.
- Channel update: new channel created with correct platform (group vs direct); `message_count` increments and `last_processed` advances across two messages; `active_topics` gains the topic once (no duplicate); noise message updates count without a topic; existing body preserved.
- Indexer: seed a `FakeVault` with a few files of each type; assert each `index.md` is written with the expected grouped lines + sorting, that a malformed file is skipped (and counted), and that the summary counts are correct.
- Endpoint: `toHttp`-style mapping of `IndexSummary` → 200 with counts (consistent with the existing endpoint-test approach, no live services).

---

## 8. Files touched
- `src/Nameless.TaskList.Core/Ports.fs` — `ListFilesRecursive` on `IVault`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `FileSystemVault.ListFilesRecursive`.
- `src/Nameless.TaskList.Core/KnowledgeBase.fs` — `Naming.channelPath`.
- `src/Nameless.TaskList.Core/Pipeline.fs` — `updateChannel` step in both branches.
- `src/Nameless.TaskList.Core/Indexer.fs` — new module (regenerate + per-type renderers + `IndexSummary`).
- `src/Nameless.TaskList/` (web host) — `POST /reindex` endpoint + summary mapping.
- `tests/Nameless.TaskList.Core.Tests/` and `tests/Nameless.TaskList.Tests/` — tests per §7.

---

## 9. Open follow-ups (later increments)
1. **C** — daily/weekly digests reading these indexes + priority weights.
2. Scheduling the reindex (cron/agent calling `POST /reindex`).
3. DESIGN §9 enhancements; live-service integration tests.
