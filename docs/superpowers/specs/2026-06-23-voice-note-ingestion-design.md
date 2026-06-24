# Voice-Note Ingestion — Design Spec

> **Date:** 2026-06-23
> **Status:** Approved for planning
> **Scope:** Transcribe caption-less WhatsApp voice notes (Opus/Ogg audio) with a local Whisper CLI, then feed the transcript through the existing classify → topic → entities pipeline.
> **Builds on:** the per-message pipeline (`processMessage`), the image-message increment (`IVision`/`OllamaVision`, `GetMediaBytes`), and the bulk reprocessor.

---

## 1. Goal

WhatsApp voice notes arrive as `audio` messages with no text content (all 121 audio rows are caption-less). Today they reach the pipeline with empty `content` and are classified as noise — so anything said in a voice note (a plan, a commitment, a date) is lost. This increment adds a **transcription step**: for a caption-less `audio` message with stored bytes, run the audio through a local Whisper CLI, get back the transcript, and use it as the message content for the rest of the pipeline. It is **best-effort and additive**: when transcription succeeds the voice note becomes text; when it fails or the bytes are unavailable, behavior is exactly as today.

**Grounding (verified against the live DB + host):**
- Audio bytes live in the Postgres `messages.media` bytea column — the same place image bytes live, reachable by the **existing** `GetMediaBytes` query (it is media-type-agnostic: `octet_length(media) > 0`). No on-disk file hunt.
- Of 121 `audio` rows, **26 have stored bytes** (the directly-processable set); the other 95 lack stored bytes and are skipped (a shared follow-up with images — see §8). All 121 are caption-less.
- Audio is `audio_YYYYMMDD_HHMMSS.ogg` (WhatsApp voice notes — Opus in an Ogg container). `ffmpeg` (present) decodes Ogg/Opus for Whisper.
- The `whisper` CLI (openai-whisper, pyenv `3.10.12`) and `ffmpeg` are installed on the host. Ollama has **no** audio/transcription endpoint, so — unlike vision — transcription cannot reuse the Ollama HTTP adapter; it shells out to the Whisper CLI.

---

## 2. Scope

### In scope
- An `ITranscriber` port (`Transcribe : byte array -> string`) + a `WhisperTranscriber` adapter that shells out to the Whisper CLI (temp `.ogg` → `Process.Start` → read transcript), with a pure, unit-tested argument builder.
- A transcription step in `Pipeline.processMessage`, parallel to the existing vision step, factored into one shared `enrich` helper so the image and audio paths do not duplicate logic.
- `PipelineDeps` gains `Transcriber: ITranscriber`; host DI + config wiring (`Whisper:*`).
- Unit tests for the argument builder and the pipeline audio path (success + fallbacks), using in-memory fakes.

### Out of scope (later / unchanged)
- Non-audio media beyond the existing image path (video/document).
- Re-downloading/decrypting media for `audio` rows without stored bytes (the 95 — shared follow-up with images).
- A larger Whisper model for accuracy; first-run weight-download latency.
- `PipelineResult`, the existing routes, the Indexer, the Digest engine, the entity writers, and topic matching are **unchanged** except the single content-substitution point (shared with vision).
- The actual `Process.Start` execution is not unit-tested (needs the Whisper binary), consistent with `PostgresMessageSource` (needs a live DB). The pure argument builder + pipeline fakes cover the logic.

---

## 3. Transcription port + adapter

### 3.1 `ITranscriber` (`Ports.fs`)
```
ITranscriber.Transcribe : audioBytes: byte array -> string
```
Mirrors `IVision.Describe` in shape (synchronous, one call, returns the extracted text; raises on failure). Audio bytes are fetched via the **existing** `IMessageSource.GetMediaBytes(id, chatJid)` — no new query.

### 3.2 `WhisperTranscriber` (`Adapters.fs`)
`WhisperTranscriber(command: string, model: string, language: string, timeoutSeconds: int)`.

`Transcribe(audioBytes)`:
1. Create a unique temp working directory; write `audio.ogg` into it.
2. Build the argument list with the **pure `WhisperArgs.build`** function (§3.3).
3. `Process.Start` the `command` (no shell; redirect stdout/stderr); `WaitForExit(timeoutSeconds * 1000)`.
   - Not exited in time → `Kill()` then raise.
   - Non-zero exit code → raise, including captured stderr.
4. Read `<dir>/audio.txt` (Whisper writes `<input-basename>.txt` into `--output_dir`), trim, return it.
5. `try/finally` deletes the temp directory on every path (success or raise).

Raises on any failure (timeout, non-zero exit, missing output, IO). The pipeline wraps the call in `try/with` (§4), so a raise becomes the unchanged-content noise fallback. Requires `ffmpeg` + the Whisper binary on PATH (both present). First run downloads model weights (cached by Whisper).

### 3.3 `WhisperArgs.build` (pure, in `Adapters.fs`)
```
WhisperArgs.build : model: string -> language: string -> inputName: string -> outputDir: string -> string list
```
Returns, in order:
- `inputName` (e.g. `audio.ogg`)
- `--model`, `model`
- `--output_format`, `txt`
- `--output_dir`, `outputDir`
- `--fp16`, `False`  (suppresses the CPU FP16 warning; harmless on GPU)
- `--language`, `language` **only when** `language` is non-empty/non-whitespace (omitted ⇒ Whisper auto-detects per note).

This is the contract most likely to regress (flag changes); it is unit-tested directly without invoking the binary.

---

## 4. Pipeline integration (`Pipeline.fs`)

`PipelineDeps` gains `Transcriber: ITranscriber`. The image (vision) and audio (transcription) enrichment steps are factored into one shared local helper inside `processMessage`, replacing the current standalone vision block. It sits **after** the idempotency guard (so a `Skipped` message never triggers the slow call) and **before** classify:

```fsharp
let enrich (mediaType: string) (extract: byte array -> string) (m: ChatMessage) =
    let isTarget =
        not (isNull m.MediaType) && m.MediaType = mediaType
        && System.String.IsNullOrWhiteSpace m.Content
    if not isTarget then false, m
    else
        match deps.Messages.GetMediaBytes(id, chatJid) with
        | Some bytes ->
            (try
                match extract bytes with
                | t when not (System.String.IsNullOrWhiteSpace t) -> true, { m with Content = t }
                | _ -> false, m
             with _ -> false, m)
        | None -> false, m

let imageDerived, msg = enrich "image" deps.Vision.Describe msg
let audioDerived, msg = enrich "audio" deps.Transcriber.Transcribe msg
```

The body header becomes three-way and is applied on **both** the signal path and the noise path (the noise path preserves derived text, per the existing image fix):

```fsharp
let mediaHeader =
    if imageDerived then "## Image (vision-extracted)\n"
    elif audioDerived then "## Voice note (transcribed)\n"
    else "## Raw\n"
```

- Signal path: body = `mediaHeader + msg.Content` (unchanged shape; just a third header value).
- Noise path: body = `(if imageDerived || audioDerived then mediaHeader + msg.Content else "")` — preserves the transcript/description even when classified noise, exactly as the image path already does.

Because the helper gates on `IsNullOrWhiteSpace m.Content`, applying image then audio is safe: an image message never matches `"audio"`, and once content is filled the second call's gate is false. All audio rows are caption-less, so audio always passes the gate; a captioned audio (rare) is left as normal text. Everything after this — classify, topic match (incl. embeddings), entity writers, channel update, idempotency — is unchanged and operates on `msg.Content`.

---

## 5. Config & wiring

- `Whisper:Command` — default `whisper`.
- `Whisper:Model` — default `base`.
- `Whisper:Language` — default `""` (empty ⇒ auto-detect per note; handles a mixed English/Afrikaans household).
- `Whisper:TimeoutSeconds` — default `300`.
- `PipelineDeps = { …; Transcriber: ITranscriber }` (added alongside `Vision`).
- `Program.fs`: register `ITranscriber` as a singleton `WhisperTranscriber` (command/model/language/timeout from config); build `PipelineDeps` with it in **both** the `/messages/process` and `/messages/process-since` handlers. Test `deps` helpers gain the field.
- No new endpoint — audio handling is transparent inside `processMessage`; the bulk reprocessor picks it up automatically, so a bulk re-run backfills voice notes.

---

## 6. Error handling & idempotency

- Transcription wrapped in `try/with` → on any failure, fall back to the original (empty) content; the pipeline proceeds (likely noise). Never aborts.
- No stored media bytes → skip transcription (fallback).
- Runs only after the idempotency guard, so it is called at most once per message and never on a `Skipped` reprocess.
- Temp working directory deleted in `try/finally` on every path.
- No new vault writes/deletes beyond `processMessage`'s own; the Message record is written as before (with the audio-derived body header).

---

## 7. Testing (unit, in-memory fakes — no live services)

- **`WhisperArgs.build`** (pure):
  - includes `--model <model>`, `--output_format txt`, `--output_dir <dir>`, `--fp16 False`, and the input name;
  - **omits** `--language` when language is `""`/whitespace;
  - **includes** `--language <lang>` when language is set.
- **Pipeline audio path** (seeded `FakeVault`, a `FakeMessages` returning an `audio`/empty-content message with `GetMediaBytes = Some bytes`, a `FakeTranscriber` returning a transcript, scripted `FakeChatClient`):
  - classify/topic/entity flow runs on the **transcript** (e.g. a task is extracted from it);
  - the written Message body contains the transcript under the `## Voice note (transcribed)` header.
- **Fallbacks:** `FakeTranscriber` throws → content stays empty → noise path writes a minimal message (no crash); `GetMediaBytes = None` → transcriber never called, noise path. When transcription succeeds but classify returns noise, the noise message body still contains the `## Voice note (transcribed)` transcript (mirrors the image noise-preservation test).
- **Non-audio unaffected:** a normal text message (`MediaType = null`) and an image message never call `Transcribe` (the default `FakeTranscriber` throws if called).
- The actual `Process.Start` path in `WhisperTranscriber` is not unit-tested (needs the binary) — consistent with the project's no-live-service test stance.

---

## 8. Files touched

- `src/Nameless.TaskList.Core/Ports.fs` — `ITranscriber`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `WhisperTranscriber` + pure `WhisperArgs.build`.
- `src/Nameless.TaskList.Core/Pipeline.fs` — shared `enrich` helper (replacing the standalone vision block), `PipelineDeps.Transcriber`, 3-way `mediaHeader`.
- `src/Nameless.TaskList/Program.fs` — `ITranscriber` DI + the field in both deps-building handlers.
- `src/Nameless.TaskList/appsettings.json` — `Whisper` config block.
- Tests: `Fakes.fs` (`FakeTranscriber`), `AdapterWireTests.fs` (`WhisperArgs.build` cases), `PipelineTests.fs` (audio cases), and `Transcriber` added to every `PipelineDeps` test helper.

> **Port-extension ripple:** adding `ITranscriber` to `PipelineDeps` breaks every `deps` construction site until updated — both host handlers and every test deps helper (same pattern as the `Vision`/`Embedder` additions). They change together. (`IMessageSource` is **not** extended — `GetMediaBytes` already exists, so the message-source fakes are unaffected.)

---

## 9. Open follow-ups (later)

1. Re-fetch/decrypt media for `audio` rows without stored bytes (the 95) — shared with the image gap.
2. Video/document handling.
3. Larger Whisper model (`small`/`medium`) for accuracy; mitigate first-run weight-download latency (warm the model cache).
4. Transcription is CPU-bound and can be slow per note; fine behind the background bulk job, but revisit if throughput matters.
