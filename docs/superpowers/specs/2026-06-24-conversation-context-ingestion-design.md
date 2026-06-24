# Conversation-Context Ingestion — Design Spec

> **Date:** 2026-06-24
> **Status:** Approved for planning
> **Scope:** Feed a window of recent prior messages from the same chat into the `classify` and `topic-update` LLM steps of `processMessage`, so messages that only make sense in conversational context are no longer mis-classified as noise.
> **Builds on:** the per-message pipeline (`processMessage`), the existing `IMessageSource.GetRecent` port + `GetPreviousMessagesByChatIdAndJid` query, and the hybrid embedding topic-matcher.

---

## 1. Goal

`Pipeline.processMessage` classifies and extracts from each message **in isolation**. Many WhatsApp messages only make sense given what came before — "yes the 19th works", "ok send it to her then", "👍 done" — and when processed individually they are classified as noise, so the signal (a confirmed date, an assigned task, a resolved commitment) is lost.

The fix is to give the LLM the **recent conversation history** of the same chat as context. The infrastructure already exists end-to-end and is simply unused:

- `Queries.GetPreviousMessagesByChatIdAndJid` (`Library.fs`) returns the chat's messages before a given timestamp, newest-first, `LIMIT 5`.
- `IMessageSource.GetRecent(chatJid, before, excludingId)` (`Ports.fs`) exposes it; `PostgresMessageSource.GetRecent` (`Adapters.fs`) implements it.
- **Nothing calls `GetRecent`** — `processMessage` never fetches history.

This increment wires that history into the two steps where it changes the outcome, with **no schema, config, DI, or `PipelineDeps` changes**.

**Grounding (verified against the code):**
- `GetRecent` is already implemented and unit-fakeable; the test fakes (`PipelineTests.FakeMessages`, `BulkProcessorTests`, `EndpointTests`) all stub it as `[]`.
- The classify step (`Agent.runConversation deps.Chat classifyTools Prompts.classifySystem msg.Content`) produces both the noise decision **and** `classification.Intent`. Intent flows to every downstream step (topic match, task/event/commitment/note creation, person stubs), so improving the classify input improves the whole pipeline transitively.

---

## 2. Scope

### In scope
- Two pure, unit-tested helpers in `Prompts.fs`:
  - `renderHistory : ChatMessage list -> string` — render `GetRecent`'s newest-first list as an **oldest→newest** `Sender: text` transcript.
  - `classifyUser : history:string -> content:string -> string` — wrap the current message with the history block, or pass `content` through unchanged when history is empty.
- One added sentence each to `classifySystem` and `topicUpdateSystem` explaining the history block.
- A best-effort `GetRecent` fetch in `processMessage`, after the enrich (vision/transcription) step and before classify; its rendered transcript is fed into the classify call and the topic-update payload.
- Test support: `FakeChatClient` captures the message arrays it receives; `PipelineTests.FakeMessages` gains an optional `?recent` list.
- Unit tests for the two helpers and a pipeline test asserting history reaches the classify call.

### Out of scope (later / unchanged)
- **Topic *matching*** (embedding shortlist + LLM confirm), entity creation, and person stubs are unchanged — they consume `classification.Intent`, which already benefits from context. (Decision: classify + topic-update only.)
- The SQL window stays at the existing hard-coded `LIMIT 5`. No configurable `HistoryWindow` setting; no `PipelineDeps`, `appsettings`, or DI changes.
- `PipelineResult`, the HTTP routes, the Indexer, the Digest engine, and the bulk reprocessor are untouched.
- No persistence/caching of history or embeddings.

---

## 3. The two helpers (`Prompts.fs`)

`ChatMessage` (defined in `Library.fs`, root namespace `Nameless.TaskList.Core`) is in scope for `Prompts.fs` (compiled later), so the helpers can live alongside the other prompt-shaping code and be unit-tested directly.

### 3.1 `renderHistory`

```fsharp
/// Render recent prior messages — as returned by GetRecent (newest-first) — into an
/// oldest→newest transcript for use as conversation context. Media-only turns (empty
/// content) render with a [type] placeholder so the model knows a non-text turn occurred.
let renderHistory (recent: ChatMessage list) : string =
    recent
    |> List.rev
    |> List.map (fun m ->
        let body =
            if not (System.String.IsNullOrWhiteSpace m.Content) then m.Content.Trim()
            else
                match (if isNull m.MediaType then "" else m.MediaType).ToLowerInvariant() with
                | "image"    -> "[image]"
                | "audio"    -> "[voice note]"
                | "video"    -> "[video]"
                | "document" -> "[document]"
                | _          -> "[no text]"
        sprintf "%s: %s" m.SenderName body)
    |> String.concat "\n"
```

- `GetRecent` returns newest-first (`ORDER BY timestamp DESC`); `List.rev` yields oldest→newest, the natural reading order for a transcript.
- Empty list → `""` (drives the passthrough in `classifyUser`).
- The "include all" decision means media-only turns are kept as placeholders rather than dropped.

### 3.2 `classifyUser`

```fsharp
/// Build the classify user-message: the current message, optionally preceded by a
/// conversation-history block. Empty history passes the content through unchanged so
/// no-history processing (and existing tests) are byte-for-byte identical to before.
let classifyUser (history: string) (content: string) : string =
    if System.String.IsNullOrWhiteSpace history then content
    else
        sprintf "Recent conversation (oldest to newest, for context only):\n%s\n\n---\nMessage to classify:\n%s"
            history content
```

---

## 4. Prompt text changes

Append to `classifySystem`:

> You may be given recent conversation history for context. Use it only to disambiguate the meaning of the current message; classify and extract from the **"Message to classify"** alone, not the history.

Append to `topicUpdateSystem`:

> You may be given recent conversation history for context. Use it to interpret the new message; do not summarise the history itself into the topic body.

---

## 5. `Pipeline.processMessage` changes

After the enrich step (`imageDerived`/`audioDerived` content substitution) and before the classify call:

```fsharp
// --- Step: pull recent conversation history for context (best-effort) ---
let recent = try deps.Messages.GetRecent(chatJid, msg.Timestamp, id) with _ -> []
let historyText = Prompts.renderHistory recent
```

- Fetched once, after enrich (so `msg.Content` reflects any vision/transcription) but using `msg.Timestamp`/`id`, which enrich never changes.
- **Best-effort:** a history-query failure yields `[]` and the pipeline proceeds exactly as today. (`GetMessage` already succeeded by this point, so the DB is reachable; the guard covers transient/edge failures without ever blocking ingestion.)

Classify call changes from:

```fsharp
Agent.runConversation deps.Chat classifyTools Prompts.classifySystem msg.Content
```

to:

```fsharp
Agent.runConversation deps.Chat classifyTools Prompts.classifySystem (Prompts.classifyUser historyText msg.Content)
```

Topic-update user payload (in the best-effort topic-update block) gains a history section:

```fsharp
let user =
    sprintf "Current topic body:\n%s\n\nRecent conversation (oldest to newest, for context):\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
        existing.Content historyText msg.Content classification.Intent
```

When `historyText` is `""`, both call sites degrade to current behaviour (classify passes content through unchanged; the topic-update block carries an empty section).

---

## 6. Testing (TDD)

### 6.1 Pure helper unit tests (`Prompts`)
- `renderHistory`: reverses to oldest→newest; formats `Sender: text`; renders media-only turns as `[image]`/`[voice note]`/etc.; empty list → `""`.
- `classifyUser`: empty history returns `content` verbatim; non-empty history produces a payload containing both the history and the `Message to classify:` marker + content.

### 6.2 Test-fake changes
- `FakeChatClient` records each call's message array:
  ```fsharp
  member val Received = ResizeArray<obj array>() with get
  // in Chat: this.Received.Add(messages)
  ```
- `PipelineTests.FakeMessages` gains `?recent: ChatMessage list` (default `[]`) returned from `GetRecent`. Other fakes (`BulkProcessorTests`, `EndpointTests`) keep returning `[]` and are unaffected.

### 6.3 Pipeline integration test
- Configure `FakeMessages` with a one-message `recent` list whose `Content` is a distinctive string.
- Run `processMessage`; assert the **classify call** (`chat.Received.[0]`, index 1 = the user message, unboxed to `Conversation.UserMessage`) `.Content` contains the prior message's text and the `Message to classify:` marker.
- Existing pipeline tests (no `recent` → passthrough) remain green unchanged.

---

## 7. Risks & non-goals

- **Token growth:** up to 5 extra turns per classify/topic-update call. Bounded by `LIMIT 5` and acceptable for a local model; revisit only if it proves costly.
- **Group chats:** history mixes multiple senders; `Sender: text` lines preserve attribution, which is what the model needs.
- **Idempotency unchanged:** the message-file existence guard still short-circuits before any LLM call; history is fetched only on the processing path.
- **Determinism:** with no history (`recent = []`) the prompts and payloads are identical to the pre-change pipeline, so no existing behaviour regresses.
