# Embedding Topic Matching (Increment D-1) — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning
> **Scope:** The first DESIGN §9 enhancement — make the pipeline's topic-match step embedding-assisted: vector similarity shortlists candidate topics, the existing LLM prompt confirms among them. The other §9 items (relationship graph, voice notes, calendar sync, archive policy, conflict detection) remain a roadmap, each its own spec→plan→build.
> **Builds on:** the spine, entity writers, vault consistency, and digests increments. DESIGN §6.1 (topic-match step), §7.2 (topic-match prompt), §9 (embedding-based topic matching).

---

## 1. Goal

Today the pipeline's topic-match step runs a tool-enabled LLM call: the model browses *all* active topics via the `get_topics`/`get_topic` tools and decides match-or-new. That puts every active topic into the model's context and costs an LLM call even when the message is obviously a new topic.

This increment makes it **hybrid**: a local embedding model (`nomic-embed-text` via Ollama, already pulled) ranks active topics by cosine similarity to the message intent, shortlists the top-K, and the existing LLM prompt decides among just those (or "new"). When nothing clears a similarity floor, the message is treated as a clearly-new topic with **no** LLM call. If embeddings are unavailable, the step falls back to today's behavior, so the pipeline never gets more fragile.

The vector math is pure and deterministic (unit-tested); the LLM remains the final arbiter on a small candidate set.

---

## 2. Scope

### In scope
- `IEmbedder` port + `OllamaEmbedder` adapter (Ollama `/api/embed`, `nomic-embed-text`).
- A pure `Similarity.cosine`.
- Rewrite the `Pipeline.fs` topic-match step to the hybrid flow (gather active topics → embed + cosine-rank → shortlist top-K above a floor → clearly-new fast path OR LLM-confirm over candidates → hallucination guard → embedder-failure fallback to the current behavior).
- `PipelineDeps` gains `Embedder: IEmbedder`, `TopK: int`, `SimilarityFloor: float`; web-host DI composition + config keys.
- Unit tests with in-memory fakes for cosine, the embedder wire shape, and every hybrid branch.

### Out of scope (later / unchanged)
- Storing embeddings (computed on the fly each run; no persistence, no `_meta` index, no topic-frontmatter vectors).
- The other §9 items.
- `PipelineResult`, the `/messages/process` contract, the entity writers, the Indexer, and the digest engine are unchanged.
- Re-embedding/caching optimizations; batching multiple inputs in one embed call (one input per call this increment).

---

## 3. Port, adapter, similarity

### 3.1 `IEmbedder` (`Ports.fs`)
```
IEmbedder:
    Embed : text: string -> float array
```
One text → one vector. The pipeline calls it once per text (intent + each active topic).

### 3.2 `OllamaEmbedder` (`Adapters.fs`)
POSTs `{ model = embedModel; input = text }` to `{Ollama:Url}/api/embed`; parses `embeddings.[0]` (768 floats for `nomic-embed-text`). Synchronous, same construction/style as `OllamaChatClient`.

**The request envelope record MUST be public** (a `private` record serializes to `{}` with System.Text.Json — see the existing `OllamaRequest`/`IndexMeta` fixes). Add a code comment to that effect.

### 3.3 `Similarity` (`Similarity.fs`, new)
`cosine : float array -> float array -> float` = dot(a,b) / (‖a‖·‖b‖); returns `0.0` when either norm is 0 or lengths differ. Pure, no I/O.

### 3.4 Test double
`FakeEmbedder` (in `Fakes.fs`): maps known input strings → scripted vectors (and can be configured to throw, to exercise the fallback path).

---

## 4. Hybrid topic-match flow (`Pipeline.fs`)

Replaces the current tool-enabled topic-match step. Inputs available at that point: `classification.Intent`, `deps.Vault`, `deps.Chat`, `deps.Embedder`, `deps.TopK`, `deps.SimilarityFloor`, `channelSlug`, `msg`.

1. **Gather active topics.** Read `topics/active/` (the existing list+parse+skip-malformed pattern). For each, build `{ Slug; Path; Title; Understanding }` where `Understanding` is the "## Current understanding" section of the body (fallback: full body, else title).
2. **Try the embedding path** (when `deps.Embedder` succeeds and there is ≥1 active topic):
   - Embed `classification.Intent` (1 call).
   - Embed each topic's `Title + "\n" + Understanding` (N calls, on the fly).
   - Score each by `Similarity.cosine intentVec topicVec`; keep those `>= SimilarityFloor`; sort desc; take `TopK`.
   - **Empty shortlist** → clearly-new fast path: create a new topic from `classification.Intent` (title via a short heuristic or the existing new-topic path), **skip the LLM topic-match call**.
   - **Non-empty shortlist** → run `Agent.runConversation deps.Chat [] Prompts.topicMatchSystem payload` where `payload` injects `New message intent: …` + the candidates (`slug` / `title` / `understanding`). Parse with `Prompts.parseTopicMatch`.
     - On parse `Error` → `LlmError` (unchanged).
     - On `Ok matchResult`: if `Match` and `TopicSlug` is one of the shortlisted slugs → match that topic; otherwise (no match, or a slug not in the shortlist — **hallucination guard**) → create new.
3. **Fallback** (embed call throws, or no active topics): run today's behavior — `Agent.runConversation deps.Chat [ getTopics; getTopic ] Prompts.topicMatchSystem (intent)` and parse as now.
4. **Downstream unchanged.** The resulting "existing topic slug+path vs new topic" feeds the exact same create-or-reuse + later topic-update code as today.

New-topic creation (title/slug/record/body) and the topic-update step are unchanged.

---

## 5. Config & wiring
- `Ollama:EmbedModel` — default `nomic-embed-text`.
- `TopicMatch:TopK` — default `5`.
- `TopicMatch:SimilarityFloor` — default `0.5`.
- `PipelineDeps = { Messages; Vault; Chat; Model; Embedder: IEmbedder; TopK: int; SimilarityFloor: float }`.
- `Program.fs`: register `IEmbedder` (singleton `OllamaEmbedder` over the shared `HttpClient`); build `PipelineDeps` with the embedder + the two params (config with the defaults above). Test `deps` helpers gain the new fields.
- No `appsettings` is required for the defaults to work; the keys override them.

---

## 6. Error handling & idempotency
- Embed failure → fall back to the current LLM-with-tools topic match (no pipeline regression).
- Malformed topic files skipped during gather.
- LLM JSON parse failure → `LlmError` (unchanged).
- Hallucinated slug (not in shortlist) → treated as new.
- No new writes/deletes; idempotency is inherited (the message-existence guard is upstream, unaffected).

---

## 7. Testing (unit, in-memory fakes — no live services)
- `Similarity.cosine`: identical → 1.0; orthogonal → 0.0; opposite → −1.0; mismatched-length or zero-norm → 0.0.
- `OllamaEmbedder`: in-process-listener test — asserts it POSTs `{ "model", "input" }` to `/api/embed`, that the request body is non-empty (public-envelope regression), and that it returns the parsed `embeddings[0]`.
- Pipeline hybrid (seeded `FakeVault` active topics, `FakeEmbedder`, `FakeChatClient`):
  - **shortlist→confirm:** topic A scored above the floor → LLM payload contains A's slug, and an `Ok match` to A links the message to A.
  - **clearly-new fast path:** all topics below the floor → a new topic is created and the topic-match LLM call is **not** made (assert chat-call count / scripted-queue position).
  - **top-K limiting:** more than `TopK` topics above the floor → only `TopK` candidates injected.
  - **hallucination guard:** LLM returns a slug not in the shortlist → new topic created.
  - **embedder-failure fallback:** `FakeEmbedder` throws → the old tool-enabled path runs and still matches/creates (assert it used the tools path).
- Existing pipeline/endpoint tests stay green (the `deps` helper gains the new fields; `PipelineResult` unchanged).

---

## 8. Files touched
- `src/Nameless.TaskList.Core/Ports.fs` — `IEmbedder`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `OllamaEmbedder` (+ public request envelope).
- `src/Nameless.TaskList.Core/Similarity.fs` — new (`cosine`).
- `src/Nameless.TaskList.Core/Pipeline.fs` — hybrid topic-match step + `PipelineDeps` fields.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — register `Similarity.fs` (before `Pipeline.fs`) and ensure `Adapters.fs`/`Ports.fs` ordering holds.
- `src/Nameless.TaskList/Program.fs` — DI for `IEmbedder` + deps params.
- `src/Nameless.TaskList/appsettings.json` — `Ollama:EmbedModel`, `TopicMatch:TopK`, `TopicMatch:SimilarityFloor`.
- `tests/…Core.Tests/` — `Fakes.fs` (`FakeEmbedder`), `SimilarityTests.fs`, embedder wire test, `PipelineTests.fs` additions.
- `Prompts.fs` — unchanged (reuse `topicMatchSystem`; candidates injected via the user payload).

---

## 9. Open follow-ups (later)
1. Persist/cache topic embeddings (a `_meta` index or sidecars) if on-the-fly embedding becomes a bottleneck at scale.
2. Batch embedding (multiple inputs per `/api/embed` call).
3. The remaining §9 items (relationship graph, voice notes, calendar sync, archive policy, conflict detection).
