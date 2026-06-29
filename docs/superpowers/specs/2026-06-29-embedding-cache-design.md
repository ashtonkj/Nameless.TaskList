# Embedding cache (topic / task / note / person match) — design

**Date:** 2026-06-29
**Status:** Approved (ready for plan)
**Roadmap item:** Medium — "Topic-match embedding caching" (generalised to all four embedding match sites).

## Problem

Every non-noise message runs `Steps.matchTopic`, which for each active topic recomputes
`embedder.Embed (title + "\n" + understanding)` — an Ollama `/api/embed` HTTP round-trip per
candidate, on **every message**, even though that document text only changes when the file changes.
With N active topics that is N redundant embed calls per message. The identical N+1 pattern lives in
`Steps.shortlistAndConfirm` (shared by task / note / person matching): `embedder.Embed embedText` is
recomputed per candidate, per message.

Only the per-message **query** embed (`intent`) is genuinely unique each message; the **document**
embeds are pure functions of file content and are wastefully recomputed.

## Goal

Eliminate the redundant document re-embeds by caching embeddings by content, persisted across
restarts and bounded so it cannot grow without limit. No behaviour change to match results — only
fewer embed calls.

## Approach: a transparent caching decorator at the `IEmbedder` port

`IEmbedder` is a single-method port (`Embed : text -> float array`) registered as a DI singleton via
`OllamaEmbedder(http, url, embedModel)`. Wrap it in a `CachingEmbedder` decorator and register the
decorator as the `IEmbedder` singleton instead. Because every match site goes through `IEmbedder.Embed`,
this fixes the N+1 at **all four sites** (topic, task, note, person) with **zero changes to `Steps.fs`
or `Pipeline.fs`**.

**Rejected alternative:** threading a cache object through `PipelineDeps` → `Steps`. It touches every
call site, leaks caching into the orchestration layer, and forces the eval harness to thread it too.
The decorator keeps caching entirely at the adapter boundary.

**Host-only.** The decorator is wired only in the host (`Program.fs` DI). The eval harness and unit
tests construct embedders directly, so their behaviour and determinism are unchanged.

## Components

Following the repo's pure-Core / thin-adapter convention (cf. `WhisperArgs.build` pure + tested while
the Whisper adapter shells out; `FileSystemSchedulerStateStore` for `.taskmeister/` JSON state):

1. **`EmbeddingCache.fs` (new, pure Core, unit-tested)** — a thread-safe **bounded LRU map**
   `key (string) → float[]`. Operations:
   - `tryGet key` — returns the vector if present and refreshes its recency.
   - `set key vector` — inserts; when over capacity, evicts the least-recently-used entry.
   - `snapshot ()` — current entries most-recent-first (for persistence).
   - `ofSnapshot capacity entries` — rebuild from a loaded snapshot.
   - Internally lock-guarded (the pipeline is invoked concurrently by the HTTP endpoint, the
     WhatsApp/IMAP listeners, and the scheduler). The eviction policy is deterministic and fully
     unit-testable independent of any I/O.

2. **`EmbeddingCacheState` record (Library.fs)** — alongside the other persisted state records
   (`SchedulerState`, `ListenCursor`, `EmailCursor`):
   ```fsharp
   [<CLIMutable>] type EmbeddingCacheEntry = { Key: string; Vector: float[] }
   [<CLIMutable>] type EmbeddingCacheState = { Model: string; Entries: EmbeddingCacheEntry[] }
   ```
   `Model` stamps the embed model the vectors were produced with (see Invalidation).

3. **`IEmbeddingCacheStore` port (Ports.fs)** — `Load : unit -> EmbeddingCacheState` /
   `Save : EmbeddingCacheState -> unit`.

4. **`FileSystemEmbeddingCacheStore` adapter (Adapters.fs)** — `Load`/`Save` over a single JSON file,
   best-effort (missing/corrupt → empty state), mirroring `FileSystemSchedulerStateStore` exactly.

5. **`CachingEmbedder` decorator (Adapters.fs)** — implements `IEmbedder`; holds the inner embedder,
   the LRU cache, the store, the configured model, and a dirty counter.

## Data flow, keying & invalidation

- **Key** = SHA256 hex of `embedModel + "\n" + text`. Folding the model name into the key means a
  change to `Ollama:EmbedModel` **cannot serve stale, wrong-dimension vectors** — a different model
  yields different keys.
- **`Embed text`:** compute key → `tryGet` → **hit** returns the cached vector; **miss** calls
  `inner.Embed text`, `set`s the result, increments the dirty counter, and returns it.
- **Invalidation is automatic.** A topic/task/note/person file whose text changes produces a new key;
  the stale entry simply ages out via LRU. No explicit invalidation path.
- **Model-mismatch guard on load.** The persisted state stamps `Model`. On startup, if the stored
  `Model` differs from the configured embed model, the loaded state is discarded (start empty). This is
  belt-and-suspenders on top of the model-in-key scheme (covers a file produced by an older build).
- **Bounding.** `MaxEntries` cap with LRU eviction. Document embeds are touched every message, so they
  stay hot and never evict; one-shot query embeds churn through the LRU tail.

## Persistence cadence

- **Load** the JSON file at startup into the LRU (subject to the model-mismatch guard).
- **Save** is debounced: a flush fires every `SaveEveryN` misses (a dirty-counter threshold inside the
  decorator), **not** per embed — this avoids a file write on every message. A final flush is also
  registered on graceful shutdown via `IHostApplicationLifetime.ApplicationStopping`.
- Worst-case data loss on a hard kill (SIGKILL, no graceful stop) is at most `SaveEveryN` embeds, all
  cheaply recomputable. **All store writes are best-effort** — a store failure is swallowed and never
  breaks embedding.

## Configuration (`appsettings.json`, with defaults)

```json
"EmbeddingCache": {
  "Enabled": true,
  "MaxEntries": 2000,
  "SaveEveryN": 50,
  "StatePath": "<Vault:Root>/.taskmeister/embedding-cache.json"
}
```

- `Enabled: false` → DI registers the bare `OllamaEmbedder` (escape hatch; no decorator).
- `MaxEntries` 2000 comfortably holds the active document set (topics + pending tasks + notes + people,
  typically hundreds) plus query headroom. Raise it if the active vault is much larger so documents
  never evict.
- `StatePath` defaults under the existing `.taskmeister/` state dir (same convention as bulk-jobs,
  scheduler, listen/email cursors). Absolute paths are honoured; a blank value falls back to the
  default.

## DI wiring (`Program.fs`)

Replace the `AddSingleton<IEmbedder>` registration so that, when `EmbeddingCache:Enabled` is true, it
constructs `OllamaEmbedder(...)` as the inner embedder and returns `CachingEmbedder(inner, cache, store,
model, saveEveryN)`; when false it returns the bare `OllamaEmbedder` (today's behaviour). Parse
`MaxEntries`/`SaveEveryN` with the existing `Int32.TryParse |> default` idiom and resolve `StatePath`
with the same blank-falls-back-to-`<Vault:Root>/.taskmeister/...` idiom used by the bulk-job store.

## Testing

- **Pure LRU (`EmbeddingCacheTests`, Core.Tests):**
  - insert beyond capacity evicts the least-recently-used entry;
  - `tryGet` refreshes recency so the LRU victim is the correct (untouched) entry;
  - `snapshot` → `ofSnapshot` round-trips entries and order;
  - capacity is honoured after restore.
- **Decorator (`CachingEmbedderTests`, Core.Tests, with a call-counting `FakeEmbedder`):**
  - same text embedded twice → inner called **once** (cache hit), same vector returned;
  - two distinct texts → inner called for each;
  - capacity overflow evicts so the cold entry re-embeds (inner called again) while the hot entry does
    not;
  - persistence: after `SaveEveryN` misses the store receives a snapshot; a new decorator loading that
    snapshot serves hits without calling inner;
  - model change between save and load discards the cache (inner called again).
- **Store adapter (`FileSystemEmbeddingCacheStoreTests`, Core.Tests):** filesystem round-trip; missing
  file → empty state; corrupt file → empty state (mirrors the scheduler-store test).

## File / compile-order summary

- `Ports.fs` — add `IEmbeddingCacheStore`.
- `Library.fs` — add `EmbeddingCacheEntry` / `EmbeddingCacheState`.
- `EmbeddingCache.fs` — **new**, after `Library.fs`, before `Adapters.fs`: the pure LRU type.
- `Adapters.fs` — add `FileSystemEmbeddingCacheStore` + `CachingEmbedder`.
- `Nameless.TaskList.Core.fsproj` — register `EmbeddingCache.fs` in the right slot.
- `src/Nameless.TaskList/Program.fs` — rewire the `IEmbedder` singleton.
- `appsettings.json` — add the `EmbeddingCache` section.
- Tests: `EmbeddingCacheTests.fs`, `CachingEmbedderTests.fs`, store test (in the existing adapters test
  file or a new one).

## Out of scope

- Caching the chat/vision/transcription models (only embeddings are content-pure and hot).
- Precomputing/warming the cache (e.g. a reindex-time embed pass) — lazy population on first use is
  sufficient; revisit only if cold-start latency becomes a concern.
- Sharing the cache across processes / a networked cache — single long-lived host process only.
