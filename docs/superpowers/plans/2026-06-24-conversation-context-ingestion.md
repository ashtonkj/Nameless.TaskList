# Conversation-Context Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Feed a window of recent prior messages from the same chat into the `classify` and `topic-update` LLM steps of `Pipeline.processMessage`, so context-dependent messages are no longer mis-classified as noise.

**Architecture:** Two pure helpers in `Prompts.fs` (`renderHistory`, `classifyUser`) shape the conversation transcript and the classify payload. `processMessage` calls the already-existing-but-unused `IMessageSource.GetRecent` (best-effort) once before classify, and threads the rendered transcript into the classify call and the topic-update payload. No schema, config, DI, or `PipelineDeps` changes.

**Tech Stack:** F# / .NET 10, xUnit 2.9.2, System.Text.Json. Tests use in-memory fakes (`FakeVault`/`FakeChatClient`/`FakeMessages`) — no live Postgres/Ollama.

## Global Constraints

- F# `type private` records serialize to `{}` under YamlDotNet + STJ — never make a serialized record `private`. (Not expected to arise here; no new records.)
- JSON sent to Ollama uses `JsonSerializerOptions.Web` (camelCase); message-shape fields stay records, protocol constants (`role`/`type`) stay computed members.
- Build must pass (`dotnet build`) and the default test suite must pass (`dotnet test`) before any task is considered complete.
- Spec: `docs/superpowers/specs/2026-06-24-conversation-context-ingestion-design.md`.
- Window stays at the existing SQL `LIMIT 5`; no configurable size. Scope is **classify + topic-update only** — topic *matching*, entity creation, and person stubs are untouched.
- Determinism guarantee: with empty history, classify/topic-update payloads must be byte-for-byte identical to the pre-change pipeline (so existing tests stay green unchanged).

---

### Task 1: Pure prompt helpers `renderHistory` and `classifyUser`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Prompts.fs` (add two functions to the `Prompts` module; append one sentence each to `classifySystem` and `topicUpdateSystem`)
- Test: `tests/Nameless.TaskList.Core.Tests/PromptsTests.fs` (create)

**Interfaces:**
- Consumes: `ChatMessage` (record in `Library.fs`, root namespace `Nameless.TaskList.Core`; fields used: `Content: string`, `MediaType: string`, `SenderName: string`).
- Produces:
  - `Prompts.renderHistory : ChatMessage list -> string`
  - `Prompts.classifyUser : history:string -> content:string -> string`

**Note on test project:** `PromptsTests.fs` is a new file — it must be added to `tests/Nameless.TaskList.Core.Tests/*.fsproj` in `<Compile Include=...>` order (after `Fakes.fs`, before/after other test files is fine as long as it compiles). Check the existing fsproj and insert the `<Compile Include="PromptsTests.fs" />` line alongside the other test files.

- [ ] **Step 1: Write the failing tests**

Create `tests/Nameless.TaskList.Core.Tests/PromptsTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.PromptsTests

open System
open Nameless.TaskList.Core
open Xunit

// Minimal ChatMessage builder for history rendering (only Content/MediaType/SenderName matter).
let private histMsg (sender: string) (content: string) (mediaType: string) : ChatMessage =
    { Id = "x"; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = sender; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = content; MediaType = mediaType
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = DateTime(2026, 6, 24) }

[<Fact>]
let ``renderHistory of empty list is empty string`` () =
    Assert.Equal("", Prompts.renderHistory [])

[<Fact>]
let ``renderHistory reverses newest-first to oldest-first transcript`` () =
    // GetRecent returns newest-first; the transcript must read oldest->newest.
    let recent = [ histMsg "Me" "newest" null; histMsg "Wife" "oldest" null ]
    Assert.Equal("Wife: oldest\nMe: newest", Prompts.renderHistory recent)

[<Fact>]
let ``renderHistory renders media-only turns as placeholders`` () =
    let recent =
        [ histMsg "Wife" "" "document"
          histMsg "Wife" "" "video"
          histMsg "Wife" "" "audio"
          histMsg "Wife" "" "image" ]
    // reversed: image, audio, video, document
    Assert.Equal("Wife: [image]\nWife: [voice note]\nWife: [video]\nWife: [document]",
                 Prompts.renderHistory recent)

[<Fact>]
let ``renderHistory renders empty content with unknown media as no-text`` () =
    Assert.Equal("Wife: [no text]", Prompts.renderHistory [ histMsg "Wife" "" null ])

[<Fact>]
let ``classifyUser with empty history returns content verbatim`` () =
    Assert.Equal("hello there", Prompts.classifyUser "" "hello there")

[<Fact>]
let ``classifyUser with whitespace history returns content verbatim`` () =
    Assert.Equal("hello there", Prompts.classifyUser "   " "hello there")

[<Fact>]
let ``classifyUser with history wraps content with markers`` () =
    let payload = Prompts.classifyUser "Wife: oldest\nMe: newest" "yes the 19th works"
    Assert.Contains("Wife: oldest", payload)
    Assert.Contains("Me: newest", payload)
    Assert.Contains("Message to classify:", payload)
    Assert.Contains("yes the 19th works", payload)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PromptsTests"`
Expected: compile error or FAIL — `Prompts.renderHistory` / `Prompts.classifyUser` not defined. (If you get a compile error because the file isn't in the fsproj, add the `<Compile Include="PromptsTests.fs" />` line first, then re-run.)

- [ ] **Step 3: Implement the two helpers in `Prompts.fs`**

In `src/Nameless.TaskList.Core/Prompts.fs`, inside `module Prompts`, add (a good spot is just before `parseClassification`, after the parser helpers, or right after the prompt strings — anywhere in the module is fine):

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

    /// Build the classify user-message: the current message, optionally preceded by a
    /// conversation-history block. Empty history passes the content through unchanged so
    /// no-history processing (and existing tests) is byte-for-byte identical to before.
    let classifyUser (history: string) (content: string) : string =
        if System.String.IsNullOrWhiteSpace history then content
        else
            sprintf "Recent conversation (oldest to newest, for context only):\n%s\n\n---\nMessage to classify:\n%s"
                history content
```

- [ ] **Step 4: Append the prompt-text sentences**

In `classifySystem`, append before the closing `"""` (after the existing final line `You may call the get_contexts tool to see context definitions before deciding.`):

```
You may be given recent conversation history for context. Use it only to disambiguate the meaning of the current message; classify and extract from the "Message to classify" alone, not the history.
```

In `topicUpdateSystem`, append before the closing `"""` (after `Respond ONLY with the updated markdown body (no frontmatter, no explanation).`):

```
You may be given recent conversation history for context. Use it to interpret the new message; do not summarise the history itself into the topic body.
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~PromptsTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Nameless.TaskList.Core/Prompts.fs tests/Nameless.TaskList.Core.Tests/PromptsTests.fs tests/Nameless.TaskList.Core.Tests/*.fsproj
git commit -m "feat: renderHistory + classifyUser prompt helpers

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 2: Test-fake support for capturing chat calls and supplying history

**Files:**
- Modify: `tests/Nameless.TaskList.Core.Tests/Fakes.fs` (add `Received` capture to `FakeChatClient`)
- Modify: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs:11-16` (add `?recent` to `FakeMessages`)

**Interfaces:**
- Consumes: `Conversation.ChatResponse`, `IChatClient`, `IMessageSource`, `ChatMessage`.
- Produces:
  - `FakeChatClient.Received : ResizeArray<obj array>` — one entry per `Chat` call, in call order.
  - `FakeMessages(msg, ?media, ?recent)` — `GetRecent` returns `recent` (default `[]`).

This task has no standalone test; it is verified by the build compiling and the existing suite staying green. (It is a prerequisite for Task 3's test, but small and reviewable on its own.)

- [ ] **Step 1: Add `Received` capture to `FakeChatClient`**

In `tests/Nameless.TaskList.Core.Tests/Fakes.fs`, replace the `FakeChatClient` type with:

```fsharp
/// Returns scripted responses in order. Records how many times Chat was called
/// and the message array passed to each call (for asserting on prompt payloads).
type FakeChatClient(scripted: ChatResponse list) =
    let queue = Queue<ChatResponse>(scripted)
    member val Calls = 0 with get, set
    member val Received = ResizeArray<obj array>() with get
    interface IChatClient with
        member this.Chat(messages, _tools) =
            this.Calls <- this.Calls + 1
            this.Received.Add(messages)
            queue.Dequeue()
```

- [ ] **Step 2: Add `?recent` to `FakeMessages` in `PipelineTests.fs`**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, replace the `FakeMessages` type (lines ~11-16) with:

```fsharp
// IMessageSource fake returning a single configured message, optional media bytes,
// and an optional recent-history list (newest-first, as the real GetRecent returns).
type FakeMessages(msg: ChatMessage option, ?media: byte array, ?recent: ChatMessage list) =
    let recentList = defaultArg recent []
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = msg
        member _.GetRecent(_jid, _before, _ex) = recentList
        member _.GetMessagesSince(_chatJid, _since) = []
        member _.GetMediaBytes(_id, _jid) = media
```

- [ ] **Step 3: Run the full Core test suite to verify nothing broke**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests`
Expected: PASS (all existing tests + the 7 from Task 1). The `?recent` default `[]` keeps all current tests behaving identically.

- [ ] **Step 4: Commit**

```bash
git add tests/Nameless.TaskList.Core.Tests/Fakes.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "test: FakeChatClient captures calls; FakeMessages supplies history

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

### Task 3: Wire history into `processMessage`

**Files:**
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (fetch `GetRecent`; change the classify call; add history to the topic-update payload)
- Test: `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (add one test)

**Interfaces:**
- Consumes: `Prompts.renderHistory`, `Prompts.classifyUser` (Task 1); `FakeChatClient.Received`, `FakeMessages(..., ?recent)` (Task 2); `IMessageSource.GetRecent` (existing); `Conversation.UserMessage` (record with `Content: string`).
- Produces: no new public API.

- [ ] **Step 1: Write the failing test**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` (it can reuse `sampleMessage`/`deps`/`FakeMessages` already in that file; `Nameless.TaskList.Core.Conversation` is already in scope via `open` — if not, add `open Nameless.TaskList.Core.Conversation` at the top):

```fsharp
[<Fact>]
let ``classify call includes recent conversation history`` () =
    let vault = FakeVault()
    // A distinctive prior message the classifier should receive as context.
    let prior =
        { sampleMessage () with
            Id = "M0"; SenderName = "Wife"
            Content = "Are you free for Ethan's party on the 19th?" }
    let classify = Responses.final """{"noise":false,"noise_reason":null,"contexts":["family"],"intent":"confirm party date","action_required":true,"urgency":"medium","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let topicMatch = Responses.final """{"match":false,"topic_slug":null,"confidence":0.1,"match_reason":"new","new_topic_title":"Ethan party"}"""
    let topicBody = Responses.final "## Current understanding\n\n## Open questions\n\n## Resolved\n"
    let chat = FakeChatClient([ classify; topicMatch; topicBody ])
    let messages = FakeMessages(Some(sampleMessage ()), recent = [ prior ])
    let d = deps messages vault chat
    Pipeline.processMessage d "M1" "jid" |> ignore
    // First Chat call is classify; index 0 is the system message, index 1 the user message.
    let classifyUserMsg = (chat.Received.[0].[1] :?> UserMessage).Content
    Assert.Contains("Are you free for Ethan's party on the 19th?", classifyUserMsg)
    Assert.Contains("Message to classify:", classifyUserMsg)
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~classify call includes recent conversation history"`
Expected: FAIL — the classify user message is bare `msg.Content`, so it contains neither the prior text nor the `Message to classify:` marker.

- [ ] **Step 3: Fetch history in `processMessage`**

In `src/Nameless.TaskList.Core/Pipeline.fs`, immediately after the `mediaHeader` block (the lines computing `imageDerived`, `audioDerived`, `mediaHeader`) and before the `// --- Step: classify ...` comment, insert:

```fsharp
            // --- Step: pull recent conversation history for context (best-effort) ---
            let recent = try deps.Messages.GetRecent(chatJid, msg.Timestamp, id) with _ -> []
            let historyText = Prompts.renderHistory recent
```

- [ ] **Step 4: Use the history in the classify call**

In the same file, change the classify call from:

```fsharp
            let classifyReply =
                Agent.runConversation deps.Chat classifyTools Prompts.classifySystem msg.Content
```

to:

```fsharp
            let classifyReply =
                Agent.runConversation deps.Chat classifyTools Prompts.classifySystem (Prompts.classifyUser historyText msg.Content)
```

- [ ] **Step 5: Add history to the topic-update payload**

In the best-effort topic-update block near the end of `processMessage`, change the `user` payload from:

```fsharp
                let user =
                    sprintf "Current topic body:\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                        existing.Content msg.Content classification.Intent
```

to:

```fsharp
                let user =
                    sprintf "Current topic body:\n%s\n\nRecent conversation (oldest to newest, for context):\n%s\n\nNew message raw text:\n%s\n\nExtracted intent:\n%s"
                        existing.Content historyText msg.Content classification.Intent
```

- [ ] **Step 6: Run the new test to verify it passes**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~classify call includes recent conversation history"`
Expected: PASS.

- [ ] **Step 7: Run the full default test suite (regression check)**

Run: `dotnet test`
Expected: PASS — all Core + endpoint tests. Existing pipeline tests use `recent = []` (default), so `historyText = ""`, `classifyUser` passes content through unchanged, and the topic-update payload carries an empty section — behaviour is unchanged.

- [ ] **Step 8: Build the whole solution**

Run: `dotnet build`
Expected: build succeeds with no errors.

- [ ] **Step 9: Commit**

```bash
git add src/Nameless.TaskList.Core/Pipeline.fs tests/Nameless.TaskList.Core.Tests/PipelineTests.fs
git commit -m "feat: feed recent conversation history into classify + topic-update

processMessage now best-effort fetches GetRecent and threads the rendered
transcript into the classify payload and the topic-update prompt, so
context-dependent messages are judged in conversation.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_017K47hca5Y46mp278UcfQJn"
```

---

## Self-Review

**1. Spec coverage:**
- §3.1 `renderHistory` → Task 1. §3.2 `classifyUser` → Task 1. §4 prompt-text changes → Task 1 (Step 4). §5 pipeline fetch + classify call + topic-update payload → Task 3 (Steps 3-5). §6.1 helper unit tests → Task 1. §6.2 fake changes → Task 2. §6.3 pipeline integration test → Task 3 (Step 1). All covered.

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step shows complete code.

**3. Type consistency:**
- `Prompts.renderHistory : ChatMessage list -> string` and `Prompts.classifyUser : string -> string -> string` are used with matching signatures in Task 3.
- `FakeChatClient.Received : ResizeArray<obj array>` defined in Task 2, consumed in Task 3 as `chat.Received.[0].[1]`.
- `FakeMessages(msg, ?media, ?recent)` defined in Task 2, called as `FakeMessages(Some(...), recent = [ prior ])` in Task 3 — named optional argument, valid F#.
- `UserMessage.Content` matches `Conversation.fs` (`type UserMessage = { Content: string }`).
- The classify/topic-update edits quote the exact current source lines from `Pipeline.fs`.
