# Image-Only Message Processing — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning (decisions made by the implementer at the user's request — proceed without per-gate approval)
> **Scope:** Make caption-less image messages (e.g. birthday-party invites sent as a picture) usable by extracting their text/description with a local vision model, then feeding that through the existing pipeline.
> **Builds on:** the per-message pipeline (`processMessage`) and the bulk reprocessor.

---

## 1. Goal

Many WhatsApp messages are images with no text caption (1757 of 1835 image rows). Today they reach the pipeline with empty `content` and are classified as noise — so invites, flyers, and notices sent as pictures are lost. This increment adds a **vision step**: for a caption-less image message, send the image bytes to a local multimodal model (`gemma3:latest` via Ollama), get back a description + verbatim text transcription, and use that as the message content for the rest of the pipeline (classify → topic → entities). It is **best-effort and additive**: when vision succeeds the image becomes text; when it fails or the bytes are unavailable, behavior is exactly as today.

**Grounding (verified against the live DB + Ollama):**
- Image bytes are stored in the Postgres `messages.media` bytea column (`octet_length(media)` == `file_length`) — **no on-disk file hunt needed.** 752 image rows have bytes; 674 are caption-less with bytes (the directly-processable set). The rest lack stored bytes and are skipped.
- `gemma3:latest` via `POST /api/chat` with `images: [<base64>]` on the user message returns an accurate description and verbatim text transcription (≈60s/image, cold — slow but correct; fine behind the background bulk job).

---

## 2. Scope

### In scope
- `IMessageSource.GetMediaBytes : id: string * chatJid: string -> byte array option` (lazy fetch of just the `media` column; `None` if absent/empty).
- An `IVision` port + `OllamaVision` adapter (`/api/chat`, base64 image, fixed describe+transcribe prompt; config `Ollama:VisionModel`, default `gemma3:latest`).
- A vision step in `Pipeline.processMessage`: for a caption-less `image` message with stored bytes, replace the working content with the vision description, then run the existing pipeline on it. Best-effort with a fallback to today's behavior.
- `PipelineDeps` gains `Vision: IVision`; host DI + config wiring.
- Unit tests with in-memory fakes for the vision wire shape and the pipeline image path (success + fallback).

### Out of scope (later / unchanged)
- Non-image media (video/audio/document).
- Caption images (already have text → unchanged normal flow).
- Re-downloading/decrypting media from WhatsApp servers for rows without stored bytes (those are skipped).
- `PipelineResult`, the existing routes, the Indexer, the Digest engine, the entity writers, and topic matching are **unchanged** except the single content-substitution point.

---

## 3. Media access + vision port

### 3.1 `IMessageSource.GetMediaBytes` (`Ports.fs`, `Library.fs`, `Adapters.fs`)
SQL `Queries.GetMediaBytes`: `SELECT media FROM messages WHERE id = @Id AND chat_jid = @ChatJid AND octet_length(media) > 0`. The adapter returns `Some bytes` (via `reader.GetFieldValue<byte[]>`) or `None`. Lazy — fetched only for caption-less image messages, so the per-message blob is never loaded for ordinary text messages.

### 3.2 `IVision` (`Ports.fs`) + `OllamaVision` (`Adapters.fs`)
```
IVision.Describe : imageBytes: byte array -> string
```
`OllamaVision(httpClient, url, model)` base64-encodes the bytes and POSTs to `{url}/api/chat`:
```
{ "model": <visionModel>, "stream": false,
  "messages": [ { "role": "user", "content": <describe prompt>, "images": [ <base64> ] } ] }
```
returning `message.content`. It builds its own request shape (a **public** `OllamaVisionRequest`/message records — a private record serializes to `{}`), independent of the chat `Agent` loop (vision is a one-shot describe, not a tool conversation). The describe prompt: *"Describe this image. If it contains text (an invitation, flyer, schedule, notice, or screenshot), transcribe the text verbatim."* A `FakeVision` test double returns a scripted string (or throws, for the fallback test).

Config: `Ollama:VisionModel`, default `gemma3:latest`.

---

## 4. Pipeline integration (`Pipeline.fs`)

`PipelineDeps` gains `Vision: IVision`. The vision step sits inside `processMessage`, **after** the idempotency guard (so a `Skipped` message never triggers the slow vision call) and **before** classify:

```
// after `if Vault.Exists messagePath then Skipped else …`, before classify:
let isImageOnly =
    (not (isNull msg.MediaType)) && msg.MediaType = "image"
    && System.String.IsNullOrWhiteSpace msg.Content
let visionText =
    if isImageOnly then
        match deps.Messages.GetMediaBytes(id, chatJid) with
        | Some bytes -> (try Some (deps.Vision.Describe bytes) with _ -> None)
        | None -> None
    else None
let msg =                              // shadow with derived content when vision succeeded
    match visionText with
    | Some t when not (System.String.IsNullOrWhiteSpace t) -> { msg with Content = t }
    | _ -> msg
let imageDerived = visionText |> Option.exists (System.String.IsNullOrWhiteSpace >> not)
```

After this, the rest of `processMessage` is unchanged — it reads `msg.Content` for classify, entity `BuildUser`, and the message body, so it automatically operates on the vision description. The signal-path Message body is written as `"## Image (vision-extracted)\n" + msg.Content` when `imageDerived`, else the existing `"## Raw\n" + msg.Content`.

When vision fails, returns blank, or there are no stored bytes, `msg` is unchanged (empty content) → the existing classify/noise path runs exactly as today. **No new failure mode; no abort.**

Idempotency, topic matching (incl. the new embedding step), entity writers, channel update — all unchanged.

---

## 5. Config & wiring
- `Ollama:VisionModel` — default `gemma3:latest`.
- `PipelineDeps = { …; Vision: IVision }` (added alongside the existing fields).
- `Program.fs`: register `IVision` (singleton `OllamaVision` over the shared `HttpClient`, model from config); build `PipelineDeps` with it in both the `/messages/process` and `/messages/process-since` handlers. Test `deps` helpers gain the field.
- No new endpoint — image handling is transparent inside `processMessage`; the bulk reprocessor picks it up automatically.

---

## 6. Error handling & idempotency
- Vision call wrapped in `try/with` → on any failure, fall back to the original (empty) content; the pipeline proceeds (likely noise). Never aborts.
- No stored media bytes → skip vision (fallback).
- Vision runs only after the idempotency guard, so it's called at most once per message and never on a `Skipped` reprocess.
- No new writes/deletes; the Message record is written as before (with the image-derived body header).

---

## 7. Testing (unit, in-memory fakes — no live services)
- `OllamaVision`: in-process-listener test — asserts it POSTs to `/api/chat` with a non-empty body containing `images` and the base64, the vision model, and returns the parsed `message.content` (and the request envelope is public/non-empty — the `{}`-serialization regression guard).
- Pipeline image path (seeded `FakeVault`, a `FakeMessages` that returns an `image`/empty-content message and `GetMediaBytes = Some bytes`, a `FakeVision` returning a description, scripted `FakeChatClient`): assert the classify/topic/entity flow runs on the **description** (e.g. the written Message body contains the vision text under the `## Image (vision-extracted)` header; a task is extracted from the description).
- Fallback: `FakeVision` throws (or `GetMediaBytes = None`) → content stays empty → the noise path writes a minimal message (no crash).
- Non-image unaffected: a normal text message (`MediaType = null`) never calls `GetMediaBytes`/`Vision` (the default test fakes throw if called) and behaves exactly as today.

---

## 8. Files touched
- `src/Nameless.TaskList.Core/Library.fs` — `GetMediaBytes` query.
- `src/Nameless.TaskList.Core/Ports.fs` — `IMessageSource.GetMediaBytes`, `IVision`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `PostgresMessageSource.GetMediaBytes`, `OllamaVision` (+ public request envelope).
- `src/Nameless.TaskList.Core/Pipeline.fs` — the vision step + `PipelineDeps.Vision`.
- `src/Nameless.TaskList/Program.fs` — `IVision` DI + the field in both deps-building handlers.
- `src/Nameless.TaskList/appsettings.json` — `Ollama:VisionModel`.
- Tests: `Fakes.fs` (`FakeVision`), the embedder/vision wire test file, `PipelineTests.fs` additions, and `GetMediaBytes` added to every `IMessageSource` fake.

> **Port-extension ripple:** adding `GetMediaBytes` to `IMessageSource` breaks `PostgresMessageSource` and every test `FakeMessages`/`FakeSince` until updated (same as `GetMessagesSince`/`ListFilesRecursive`). They change together.

---

## 9. Open follow-ups (later)
1. Re-fetch/decrypt media for image rows without stored bytes (≈1083 caption-less images lack bytes).
2. Video/audio/document handling (audio → Whisper transcription is the §9 voice-note item).
3. Cache vision output (it's slow ≈60s/image) so a re-run doesn't re-describe — though the message-file idempotency guard already prevents re-processing a once-written message.
