# WhatsApp Postgres LISTEN/NOTIFY Trigger

**Date:** 2026-06-27
**Status:** Approved design, pre-implementation

## 1. Goal

Make the WhatsApp channel self-triggering. Today the pipeline runs only when
something `POST`s `/messages/process { id, chatJid }`. The whatsmeow bridge
already issues a Postgres `NOTIFY` on every insert, so the app should `LISTEN`
for it and run `Pipeline.processMessage` automatically — symmetric with the new
IMAP poller, which made the email channel self-triggering.

The bridge emits on channel **`whatsapp_new_message`** with a JSON payload:

```json
{
  "id": "ACCC08919536DDCF6EAB7B9800407C21",
  "chat_jid": "120363241214508891@newsletter",
  "sender": "120363241214508891",
  "is_from_me": false,
  "media_type": "",
  "album_id": null,
  "timestamp": "2026-06-18T13:08:11+02:00"
}
```

Only `id` and `chat_jid` drive the pipeline; `timestamp` advances the catch-up
cursor.

## 2. Decisions (settled)

| Question | Decision |
|---|---|
| NOTIFY source | The bridge already emits `NOTIFY whatsapp_new_message`. The app only `LISTEN`s — no DB trigger/DDL owned by us. |
| Catch-up | Payload-driven processing for live messages, plus a one-time `GetMessagesSince` catch-up on startup and on every reconnect (Postgres does not queue notifications for a disconnected listener). |
| Structure | Mirror the IMAP channel: a port + pure logic in Core, an Npgsql adapter, and a gated `BackgroundService` in the host. |

## 3. Architecture

```
Postgres (whatsmeow bridge issues NOTIFY whatsapp_new_message)
   │
   ▼
NpgsqlNotificationListener : INotificationListener ──► raw JSON payload
   │                                                     │  pure: WhatsAppListener.parse
   ▼                                                     ▼
WhatsAppListenerService (BackgroundService)        NotifyPayload { Id; ChatJid; Timestamp }
   │  LISTEN → catch-up → live-consume → (reconnect: re-LISTEN → catch-up → resume)
   │
   ├─ catch-up: IMessageSource.GetMessagesSince(cursor) ─► Pipeline.processMessage ─► advance cursor
   └─ live:     parse payload ─► Pipeline.processMessage(id, chatJid) ─► advance cursor
```

The pipeline itself is unchanged. Dedup is the existing idempotency guard
("message file exists ⇒ `Skipped`"); the listener adds no new dedup.

### 3.1 Config & gating

- New `WhatsApp:Listen:Enabled` (bool, default `false`). When `"true"`, the host
  registers `WhatsAppListenerService` as a hosted service (exactly the pattern
  `ImapPollerService` uses with `Imap:Enabled`). Off by default and in tests.
- Reuses the existing `ConnectionStrings:WhatsApp`. No new connection config.

### 3.2 Payload parsing — pure (`WhatsAppListener.fs`, new; compiled after `Pipeline.fs`)

- `NotifyPayload = { Id: string; ChatJid: string; Timestamp: System.DateTimeOffset }`
- `parse : string -> NotifyPayload option` — System.Text.Json, tolerant: returns
  `None` on invalid JSON or when `id`/`chat_jid` are missing/blank, never throws.
  The caller logs a skipped payload (failure-legibility) and continues.

### 3.3 Listener port + adapter

- **`INotificationListener`** (Ports.fs) — the testable seam over the persistent
  connection:
  `Listen : channel: string * onPayload: (string -> unit) * token: CancellationToken -> unit`
  Blocks, invoking `onPayload` for each notification until the token is cancelled
  or the connection drops (on drop it returns/throws so the service can
  reconnect).
- **`NpgsqlNotificationListener(connectionString)`** (Adapters.fs, live-only):
  opens a dedicated `NpgsqlConnection`, issues `LISTEN whatsapp_new_message`,
  subscribes to `conn.Notification`, and loops `conn.WaitAsync(token)` to receive,
  forwarding each `e.Payload` to `onPayload`. Synchronous-style like the other
  adapters. Not unit-tested (covered by the opt-in live integration test).

### 3.4 Cursor + catch-up driver

- **Cursor:** a persisted last-processed timestamp at
  `<Vault:Root>/.taskmeister/whatsapp-listen-cursor.json`, mirroring
  `FileSystemEmailCursorStore`. `IListenCursorStore.Load/Save` over a small public
  record `ListenCursor = { Since: System.DateTime }` (public — it is serialized).
  Defaults to `DateTime.MinValue` (process everything) when absent; in practice an
  operator can seed it, but a fresh install simply catches up from the beginning
  once (idempotent).
- **Catch-up driver** (pure-ish, in `WhatsAppListener.fs`, testable with
  `FakeMessages`): `catchUp : IMessageSource -> processOne -> ListenCursor ->
  ListenCursor` — calls `GetMessagesSince(None, cursor.Since)` (all chats), runs
  each through `processOne` in ascending time order (the query already
  `ORDER BY timestamp ASC`), and returns the cursor advanced to the latest
  processed message's timestamp (unchanged if none).

### 3.5 Service loop (`WhatsAppListenerService : BackgroundService`, host project)

Ordering is load-bearing:

1. `LISTEN whatsapp_new_message` **first**, so any insert after subscribe is
   buffered by Postgres rather than lost during the next step.
2. Run `catchUp` from the stored cursor (covers the downtime gap), persist cursor.
3. Consume live notifications: `parse` each payload →
   `Pipeline.processMessage id chatJid` → advance + persist the cursor to the
   payload's timestamp. A `None` parse is logged and skipped.
4. On connection drop/exception: log, back off (fixed delay, configurable later),
   reconnect → re-`LISTEN` → re-run `catchUp` → resume live.

Processing is **sequential** within the loop. The LLM steps are slow, but
Postgres buffers notifications on the connection while we process, and the
idempotency guard makes any catch-up/live overlap a no-op. Cancellation
(`stoppingToken`) exits the loop quietly, like the IMAP poller.

## 4. Components / files

- `src/Nameless.TaskList.Core/Library.fs` — add `NotifyPayload`, `ListenCursor`.
- `src/Nameless.TaskList.Core/Ports.fs` — add `INotificationListener`, `IListenCursorStore`.
- `src/Nameless.TaskList.Core/WhatsAppListener.fs` — **new**: `parse`, `catchUp`.
- `src/Nameless.TaskList.Core/Adapters.fs` — add `NpgsqlNotificationListener`, `FileSystemListenCursorStore`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — add `WhatsAppListener.fs` after `Pipeline.fs`.
- `src/Nameless.TaskList/WhatsAppListener.fs` — **new**: `WhatsAppListenerService : BackgroundService` (registered before `Program.fs`).
- `src/Nameless.TaskList/Program.fs` — register the service when `WhatsApp:Listen:Enabled = "true"`.
- `src/Nameless.TaskList/appsettings.json` — `WhatsApp:Listen` section.

## 5. Testing

All offline; default `dotnet test` stays green and starts no listener.

- **Pure `parse`** units: valid payload; malformed JSON → `None`; missing/blank
  `id` or `chat_jid` → `None`; payload with extra fields parses fine.
- **`catchUp`** units (`FakeMessages` + fake cursor): drains since-cursor in
  ascending order through a recording `processOne`; advances the cursor to the
  last message's timestamp; empty result is a no-op (cursor unchanged).
- **Service-logic** units with a fake `INotificationListener` that emits scripted
  payloads: LISTEN happens before catch-up; each good payload dispatches
  `processMessage(id, chatJid)`; cursor advances; a bad payload is skipped (and
  logged) without killing the loop.
- **Opt-in live integration test** (`Nameless.TaskList.IntegrationTests`, gated by
  `-p:Integration=true`, skips without DB config): `NpgsqlNotificationListener`
  receives a payload after a manual `NOTIFY whatsapp_new_message, '...'` on a real
  connection.

## 6. Out of scope

- A DB trigger we own (the bridge already emits NOTIFY).
- Parallel / queued processing of notifications (sequential only).
- Multiple NOTIFY channels or accounts.
- Delivery guarantees beyond the startup/reconnect catch-up.
- Backpressure / rate limiting beyond sequential processing.

## 7. Risks / notes

- **Timezone consistency of the cursor.** `GetMessagesSince` currently binds the
  `since` parameter without the SAST-offset conversion that `GetMessage` applies.
  The cursor must be stored and compared as the same instant the existing bulk
  `process-since` path uses, so catch-up neither skips nor re-floods. Implement
  the cursor in terms of the message timestamp as `PostgresMessageSource` already
  exposes it (SAST wall-clock), matching the bulk path, and add a test asserting
  the boundary (a message exactly at the cursor is not reprocessed, the next one
  is).
- **Slow-consumer lag.** A burst while each message takes seconds in the LLM will
  queue on the connection; acceptable for this single-user workload and bounded by
  idempotency. Revisit with a queue + worker only if lag becomes real (YAGNI).
