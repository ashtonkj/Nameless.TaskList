# Per-Message Pipeline — Design Spec

> **Date:** 2026-06-22
> **Status:** Approved for planning
> **Scope:** First increment of the DESIGN.md per-message pipeline (the "spine"), implemented in F# on .NET 10.
> **Parent design:** `docs/DESIGN.md` (authoritative for KB conventions, frontmatter schemas, prompts §7, naming §8).

---

## 1. Goal

Implement the per-message ingestion pipeline from `docs/DESIGN.md` §6.1 as real F# code, triggered over HTTP (replacing the n8n no-code flow). This first increment delivers the end-to-end **spine**: ingest one WhatsApp message, classify it with a local LLM, match or create a Topic, create Task files, write the Message record, and update the Topic — writing markdown files back into the Obsidian vault.

The orchestration is a **deterministic chain** (DESIGN §6.1 step order is preserved), but each LLM step runs as a **tool-enabled conversation**: the model calls read-only tools (`GetContexts`, `GetTopics`, `GetPeople`, `GetTopic`) to pull only the vault context it needs, instead of us pre-stuffing the prompt. All **writes** are performed deterministically by the orchestrator, never by the model.

---

## 2. Scope

### In scope (this increment)
- HTTP endpoint `POST /messages/process` in the existing `Nameless.TaskList` ASP.NET project.
- Ingest a message from the WhatsApp PostgreSQL database (`Library.fs` queries).
- Write the **Message** file.
- Classify (noise vs signal; contexts; intent; entities) — DESIGN §7.1.
- **Topic** match-or-create and update — DESIGN §7.2, §7.3.
- Create **Task** files — DESIGN §7.4.
- Read-only tools over the vault: `GetContexts`, `GetTopics`, `GetTopic`, `GetPeople`.
- Idempotency: reprocessing the same message is a no-op.
- Configuration via `appsettings.json` (no secrets in source).
- Unit tests for codec, naming, agent loop, and pipeline orchestration with in-memory fakes.

### Out of scope (deferred to follow-up increments)
- Event, Commitment, and Person-stub writers (DESIGN §4.3, §4.8, §7.5).
- `Channel.last_processed` updates and index regeneration (`tasks/index.md`, `topics/index.md`, etc.).
- Weekly digest / daily briefing (DESIGN §6.2, §7.6).
- Embedding-based topic matching, voice notes, calendar sync, archive policy (DESIGN §9).
- CI-wired integration tests against live Ollama/Postgres/vault (a single ignored smoke-test stub will exist, but is not wired).

---

## 3. Architecture

All testable logic lives in `Nameless.TaskList.Core`. The web project is the composition root + endpoint only.

### 3.1 Module layout (Core, compile order)

| Module | Status | Role |
|---|---|---|
| `KnowledgeBase.fs` | extend | Markdown+YAML codec. Add the **write** side (`MarkdownFile.ToString` — serialize frontmatter + body). Add `Topic`, `Task`, `Message` records. Add `Naming` helpers (slug rules + DESIGN §8 filename formats). |
| `Library.fs` | keep | SQL queries + `ChatMessage` record, unchanged. |
| `Ports.fs` | new | Interfaces: `IMessageSource`, `IVault`, `IChatClient`. |
| `Conversation.fs` | extend | Ollama wire types (mostly present). Refactor `Conversation.Call()` into the `OllamaChatClient` adapter performing a single round-trip POST. |
| `Tools.fs` | new | Read-only tool definitions + handlers over `IVault`; a registry pairing each `ToolDefinition` with its handler. |
| `Agent.fs` | new | The tool-calling loop (pure; testable with a scripted fake `IChatClient`). |
| `Pipeline.fs` | new | `processMessage` — orchestrates the spine; depends only on the three ports + `Agent`. |
| `Adapters.fs` | new | `PostgresMessageSource`, `FileSystemVault`, `OllamaChatClient`. |

### 3.2 Dependency direction
`Pipeline` / `Agent` → `Ports` + domain types **only**. `Adapters` implement `Ports`. The web host composes `Adapters` and calls `Pipeline`. No reverse dependencies.

### 3.3 Key seam
`IChatClient` is **thin**: one POST, one response. The loop and tool dispatch live in `Agent.fs`, so they can be unit-tested with a scripted fake client and an in-memory vault. Tools are **read-only**; writes are deterministic in `Pipeline.fs`. This is what keeps the hybrid flow predictable and testable.

---

## 4. Ports (`Ports.fs`)

```
IMessageSource:
  GetMessage : id:string * chatJid:string -> ChatMessage option
  GetRecent  : chatJid:string * before:DateTime * excludingId:string -> ChatMessage list

IVault:   // all paths relative to the vault root
  Exists    : relPath:string -> bool
  Read      : relPath:string -> string
  Write     : relPath:string * content:string -> unit   // creates parent dirs; never deletes
  ListFiles : relDir:string -> string list

IChatClient:
  Chat : ChatCall -> ChatResponse   // ONE round-trip; ChatResponse carries final content + tool_calls
```

`ChatCall` / `ChatResponse` are the typed Ollama request/response shapes (built on the existing `Conversation.fs` records). `ChatResponse` exposes the assistant message content and any `tool_calls`.

---

## 5. Tools (`Tools.fs`)

Read-only, backed by `IVault`. Each tool is a `ToolDefinition` (presented to the model) paired with a handler `args -> string` (returns text/JSON injected back as a `ToolMessage`).

| Tool | Returns |
|---|---|
| `GetContexts` | The defined contexts (`contexts/*.md`) with title + priority guidance summary. |
| `GetTopics` | Active topics (`topics/active/*.md`) with title + "Current understanding" summary. |
| `GetTopic(slug)` | The full body of one topic (used by the topic-update step). |
| `GetPeople` | Known people (slugs + role) from `people/`. May return stubs-not-yet-created as absent; that is acceptable for the spine. |

A registry assembles the subset of tools a given step exposes.

---

## 6. Agent loop (`Agent.fs`)

`runConversation client tools systemPrompt userMessage`:

1. `messages = [system; user]`.
2. `client.Chat messages` → response.
3. If response has `tool_calls`: for each call, look up the handler in `tools`, execute it (against `IVault`), append an assistant `tool_calls` message plus one `ToolMessage` per call; go to 2.
4. Otherwise return the final assistant `content`.
5. **Guard:** at most 5 iterations; exceeding raises `AgentError` (aborts the current step — see §9).

The loop is pure with respect to its dependencies (client + tool handlers are injected), so it is unit-tested with a `FakeChatClient` (a queue of scripted `ChatResponse`s).

---

## 7. Pipeline spine (`Pipeline.fs`)

`processMessage deps (id, chatJid)` where `deps` bundles the three ports + model name:

1. **Fetch** the message via `IMessageSource.GetMessage`. `None` → return `NotFound`.
2. **Idempotency:** derive the Message file path from the channel slug + ISO-8601 timestamp (DESIGN §8). If `IVault.Exists` → return `Skipped`, no-op.
3. **Classify:** `Agent.runConversation` with the DESIGN §7.1 prompt and the `GetContexts` tool. Parse the JSON response into a `Classification`. Parse failure → `LlmError` (abort; nothing written).
4. **If noise:** write a minimal Message file (`noise: true`); return `Processed(noise)`. STOP.
5. **Topic match:** agent conversation with the §7.2 prompt and `GetTopics` / `GetTopic` tools. Result is either a matched existing topic slug, or a decision to create a new topic (create the Topic file).
6. **Extract tasks:** §7.4 prompt turns task intents (from the classification entities) into one `tasks/pending/{verb-slug}.md` per task. (Events/commitments deferred.)
7. **Write the Message file** with final frontmatter (topic ref, `spawned_tasks`).
8. **Update the Topic:** §7.3 prompt rewrites the topic body (preserving the `## Resolved` section); append `message_refs` and `spawned_tasks`, bump `last_updated`; write the topic file.
9. Return `Processed` with a summary: topic slug + created task slugs.

### 7.1 Write ordering & partial-failure semantics
Tasks (step 6) and the Message file (step 7) are written **before** the topic update (step 8) so the topic can reference them. If step 8's LLM call fails, the tasks and message already exist and idempotency blocks reprocessing, so the topic may be left un-updated. This is logged as a **recoverable warning**, not rolled back (the design forbids deletes). Accepted for v1.

### 7.2 Person references
Person-stub creation is deferred, so `people` references on tasks/topics may point at slugs without files yet. This matches DESIGN's eventual-consistency style; `GetPeople` may therefore omit not-yet-created people. Acceptable for the spine.

---

## 8. Configuration & HTTP host

### 8.1 `appsettings.json` (bound via Options in the web host)
```
Ollama:    { "Url": "http://localhost:11434", "Model": "gemma4:e4b" }
Vault:     { "Root": "/data/@documents/Synced-Vault/Knowledge-Base" }
WhatsApp:  { "ConnectionString": "Server=127.0.0.1;Port=5432;Database=whatsapp;..." }
```
The model is configurable; `gemma4:e4b` is the default. No secrets in source — the connection string (currently hardcoded in `TestScript.fsx`) moves into config, with the real value in the gitignored `appsettings.Development.json`. Adapters receive these via constructor.

### 8.2 Endpoint
Remove the WeatherForecast scaffold. Register the adapters as their port interfaces in DI. Add:
```
POST /messages/process    body: { "id": "...", "chatJid": "...@s.whatsapp.net" }
```

| Pipeline outcome | HTTP response |
|---|---|
| `Processed` (signal) | 200, `{ topic, tasks: [...] }` |
| `Processed(noise)` | 200, `{ noise: true }` |
| `Skipped` (idempotent no-op) | 200, `{ skipped: true }` |
| `NotFound` (no such message) | 404 |
| `LlmError` / `AgentError` | 502, error logged |
| unexpected exception | 500 |

---

## 9. Error handling principles
- Validate each LLM step's JSON **before** writing anything.
- Never delete (writes are create / overwrite-body only).
- Idempotency makes retries safe.
- The step-8 topic-update failure is logged as a warning, not rolled back (§7.1).
- The agent loop's iteration guard prevents runaway tool-calling.

---

## 10. Testing

New project `tests/Nameless.TaskList.Core.Tests` (xUnit), added to `Nameless.TaskList.slnx`. Unit tests use in-memory fakes — no Postgres, Ollama, or real vault:

- **Codec round-trip:** parse → serialize → parse is stable; `## Resolved` preserved on topic rewrite.
- **Naming/slug rules (DESIGN §8):** message timestamp → filename; task `verb-slug`; topic slug.
- **Agent loop:** scripted `FakeChatClient` drives tool-call → dispatch → final content; max-iteration guard raises `AgentError`.
- **Pipeline orchestration** with fakes:
  - noise path → minimal Message file, no task created;
  - signal path → Task + Topic created, Message references them;
  - idempotency → second run is a no-op (`Skipped`);
  - topic match branch vs. new-topic branch;
  - classification / topic-match JSON parse failure → `LlmError`.

**Test doubles:** `FakeVault` (in-memory `Map<string,string>`) and `FakeChatClient` (queue of scripted `ChatResponse`s).

**Integration tests** (real Ollama + temp-dir vault + Postgres) are out of scope this increment: a single ignored/manual smoke-test stub establishes the harness but is not wired into CI.

---

## 11. Open follow-ups (next increments)
1. Event, Commitment, Person-stub writers.
2. `Channel.last_processed` + index regeneration.
3. Weekly digest / daily briefing.
4. Wire integration smoke tests.
