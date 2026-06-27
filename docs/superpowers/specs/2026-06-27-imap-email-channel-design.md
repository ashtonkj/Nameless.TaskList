# IMAP Email — Second Input Channel

**Date:** 2026-06-27
**Status:** Approved design, pre-implementation

## 1. Goal

Add email as a second input channel alongside WhatsApp, feeding the same
extraction pipeline (`Pipeline.processMessage`) that turns messages into the
markdown knowledge base. DESIGN.md already anticipates email throughout
(`channels/email/`, `platform: email`, `messages/email-*/`, the Person `email:`
field); none of it is implemented yet. This increment makes it real.

The app polls IMAP directly — it is self-contained, fits the privacy-first local
model, and needs no external bridge or extra datastore.

## 2. Decisions (settled)

| Question | Decision |
|---|---|
| Ingestion | The app polls IMAP itself via a background service. No external bridge, no new datastore. |
| Channel grouping | Per correspondent (the `From` address). One `channels/email/<slug>.md` per sender, matching DESIGN.md. |
| Scope & filter | Poll `INBOX`. Deterministically detect bulk/list mail (`List-Unsubscribe` / `Precedence: bulk`) and route it to message-only logging (like WhatsApp `@newsletter`/`@broadcast`) — logged, never classified or topic-threaded. |
| Auth | App-specific password over TLS (`LOGIN`), stored in gitignored `appsettings.Development.json` like the Postgres password. No token refresh. |
| Plug-in approach | Self-driving poller + a second `IMessageSource` (`ImapMessageSource`). Reuse the existing pipeline untouched; generalize its few WhatsApp-specific spots. |

## 3. Architecture

The pipeline is driven entirely by `IMessageSource` returning `ChatMessage`
records keyed by `(id, chatJid)`, and is platform-agnostic apart from three
WhatsApp-specific spots. The plan: generalize those three spots, add an email
`IMessageSource` and a poller that drives the existing pipeline with an *email*
`PipelineDeps`. The HTTP endpoints stay WhatsApp-only.

```
IMAP (INBOX)
   │  MailKit
   ▼
MailKitMailbox : IMailbox ──► RawEmail
   │                            │  pure: extractText / isBulk / toChatMessage
   ▼                            ▼
EmailPoller (timer) ──fills──► ImapMessageSource : IMessageSource
   │                            │
   │  per new mail              │
   └──► Pipeline.processMessage(emailDeps, messageId, fromAddress)
                                │
                                ▼
                         markdown vault (channels/email/, messages/email-*/, …)
```

### 3.1 Generalize the platform leak

The pipeline and `Naming` hardcode WhatsApp in three places. Generalize them so
the pipeline reads platform/broadcast from the message instead of inferring it:

- **`ChatMessage` (`Library.fs`)** gains two fields:
  - `Platform: string` — `whatsapp-direct` | `whatsapp-group` | `email`
  - `IsBroadcast: bool` — one-to-many feed (WhatsApp newsletter/status, or bulk email)

  `PostgresMessageSource` computes `Platform` from `is_group` and `IsBroadcast`
  from the `@newsletter`/`@broadcast` JID suffix (the logic currently inside
  `Pipeline.isBroadcastChannel`). The email source sets `Platform = "email"` and
  `IsBroadcast` from the bulk-header check.

- **`Pipeline.fs`** uses `msg.IsBroadcast` instead of calling
  `isBroadcastChannel chatJid`, and `msg.Platform` instead of the hardcoded
  `whatsapp-direct`/`whatsapp-group` literal in `updateChannel`.
  `isBroadcastChannel` moves to `PostgresMessageSource` (it is WhatsApp-JID logic).

- **`Naming` (`KnowledgeBase.fs`)** becomes platform-aware:
  - `channelPath` for email → `channels/email/<slug>.md` (currently hardcodes
    `channels/whatsapp/`). WhatsApp path unchanged.
  - `messagePath` for email → `messages/email-<slug>/<file>` (matching DESIGN.md
    §3, which namespaces message folders by platform). WhatsApp path unchanged
    (`messages/<slug>/…`) for back-compat with the existing vault.

  Both take the platform (or a derived subdir) so WhatsApp output is byte-identical
  to today and email lands in its own namespace — avoiding collisions between a
  WhatsApp contact and an email correspondent of the same name.

### 3.2 Email port + pure mapping (`Email.fs`, new — compiled before `Pipeline.fs`)

Keep everything that can be pure, pure, so it is unit-testable with no network:

- **`IMailbox`** (port, in `Ports.fs`):
  `FetchSince : folder: string * sinceUid: uint32 -> MailboxFetch`
  where `MailboxFetch` carries the `UidValidity`, the highest UID seen, and the
  `RawEmail list`. Putting IMAP behind this port makes the poller testable with a
  fake mailbox.

- **`RawEmail`** record: `MessageId`, `FromAddress`, `FromDisplay`, `Date`
  (`DateTimeOffset`), `Subject`, `TextBody` (text/plain), `HtmlBody`,
  `ListUnsubscribe` (header present?), `Precedence` (header value), `Uid`.

- **Pure functions** (in `Email.fs`):
  - `extractText : RawEmail -> string` — prefer text/plain; if only HTML, strip to
    text. Strip quoted reply chains (`>` blocks, `On … wrote:` markers) and common
    signature delimiters (`-- `). Result is the clean body stored and classified.
  - `isBulk : RawEmail -> bool` — true when `List-Unsubscribe` is present or
    `Precedence` is `bulk`/`list`/`junk`.
  - `toChatMessage : RawEmail -> ChatMessage` —
    `Id` = Message-ID, `ChatJid` = `FromAddress`, `NormalizedChatName` =
    `FromDisplay` (falling back to address), `SenderName` = same, `IsGroup` = false,
    `Platform = "email"`, `IsBroadcast = isBulk`, `Content = extractText`,
    `Timestamp` = `Date` shifted to SAST (+02:00) per DESIGN §4/§8,
    `MediaType = ""`, media/album fields empty.

### 3.3 Adapters (`Adapters.fs`)

- **`MailKitMailbox(host, port, user, password, useSsl) : IMailbox`** — MailKit /
  MimeKit. New `PackageReference` in `Nameless.TaskList.Core.fsproj` (MIT, no
  native deps, supported on .NET 10). Connects, authenticates with the
  app-password, `SELECT`s the folder, `UID SEARCH UID <since+1:*>`, fetches
  headers + body, maps each to `RawEmail`.

- **`ImapMessageSource(buffer) : IMessageSource`** — backed by an in-process
  buffer the poller fills before invoking the pipeline, so `GetMessage(id, jid)`
  returns the already-fetched mail without a second IMAP round-trip.
  - `GetMessage` — look up `(messageId, fromAddress)` in the buffer.
  - `GetRecent(jid, before, excludingId)` — recent same-correspondent mail from the
    buffer, best-effort (the quoted chain already carries context; `[]` is fine).
  - `GetMessagesSince` — not used by the poller path; returns `[]`.
  - `GetMediaBytes` — `None` (attachments deferred).

- **`FileSystemEmailCursorStore(path) : IEmailCursorStore`** — persists the
  per-account cursor `{ UidValidity; LastUid }` as a small JSON file under
  `vault/.taskmeister/email-cursor.json`, exactly like `FileSystemJobStore`. On a
  `UidValidity` change the cursor resets to 0 (full re-scan; idempotency below makes
  that safe).

### 3.4 Poller

- **`EmailPoller`** (core, testable with fake `IMailbox` + fake cursor store): one
  tick = load cursor → `FetchSince(folder, lastUid)` → for each `RawEmail`: map →
  `processMessage emailDeps messageId fromAddress` → after the batch, advance the
  cursor to the highest UID seen and persist. UIDVALIDITY mismatch → reset cursor
  first.

- **`ImapPollerService : BackgroundService`** (web project): a timer loop at
  `Imap:PollSeconds` that builds the email `PipelineDeps` (same chat/vault/embedder/
  vision/transcriber singletons; `Messages` = `ImapMessageSource`) and runs one
  `EmailPoller` tick per interval. Errors are logged and swallowed so a transient
  IMAP failure doesn't kill the loop.

### 3.5 Idempotency

The pipeline's idempotency is "message file exists ⇒ `Skipped`", with the path
derived from channel slug + timestamp-to-the-second. For email that granularity
can collide (same correspondent, same second). So the **email `messagePath`
includes a short stable hash of the Message-ID** — deterministic and re-derivable,
so re-processing the same email always targets the same path and is a no-op. This
keeps the existence check correct even if the UID cursor is lost or reset (the only
times a mail is re-fetched in normal operation).

## 4. Configuration

`appsettings.json` (non-secret):

```json
"Imap": {
  "Host": "imap.gmail.com",
  "Port": 993,
  "UseSsl": true,
  "Folder": "INBOX",
  "PollSeconds": 120,
  "Enabled": false
}
```

`appsettings.Development.json` (gitignored):

```json
"Imap": { "User": "you@example.com", "Password": "<app-password>" }
```

`Imap:Enabled` gates whether `ImapPollerService` is registered, so the poller is
off by default and in test/CI.

## 5. Testing

All offline; no live IMAP in the default `dotnet test`.

- **Pure units** (`Email.fs`): `extractText` (plain preferred, HTML fallback,
  quoted-chain + signature stripping), `isBulk` (header matrix),
  `toChatMessage` (field mapping, SAST timestamp, IsBroadcast wiring).
- **Poller** (`EmailPoller`): fake `IMailbox` + fake cursor store — new mail
  processed, cursor advances, UIDVALIDITY reset triggers re-scan, empty batch is a
  no-op.
- **Pipeline** (extending existing `FakeMessages`): an email `ChatMessage` with
  `IsBroadcast = true` yields `Logged`; a normal email writes a `platform: email`
  channel file under `channels/email/` and a message under `messages/email-*/`;
  idempotency holds (same Message-ID ⇒ `Skipped`).
- **Live IMAP** (opt-in, like the existing integration suite): a single test in
  `Nameless.TaskList.IntegrationTests` that connects to a real mailbox and skips
  when `Imap:*` config is absent.

## 6. Out of scope this increment

- Attachments (image/PDF) — the pipeline already has vision/transcription, but
  wiring MIME attachments through `GetMediaBytes` is deferred.
- OAuth2 / `XOAUTH2` — app-password only for now.
- Outbound / sent-mail processing — inbound only.
- IMAP `IDLE` push — timer-poll only.
- Multiple accounts — single account, but config is shaped to extend later.

## 7. Affected files

- `src/Nameless.TaskList.Core/Library.fs` — `ChatMessage` gains `Platform`, `IsBroadcast`.
- `src/Nameless.TaskList.Core/KnowledgeBase.fs` — `Naming.channelPath`/`messagePath` platform-aware.
- `src/Nameless.TaskList.Core/Ports.fs` — `IMailbox`, `IEmailCursorStore`.
- `src/Nameless.TaskList.Core/Email.fs` — **new**: `RawEmail`, pure mapping/bulk/extract, `EmailPoller`.
- `src/Nameless.TaskList.Core/Pipeline.fs` — read `msg.IsBroadcast`/`msg.Platform`; drop `isBroadcastChannel`.
- `src/Nameless.TaskList.Core/Adapters.fs` — `MailKitMailbox`, `ImapMessageSource`, `FileSystemEmailCursorStore`; `PostgresMessageSource` sets the new fields.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — add `Email.fs`, MailKit `PackageReference`.
- `src/Nameless.TaskList/Program.fs` — config binding + register `ImapPollerService` when `Imap:Enabled`.
- `src/Nameless.TaskList/appsettings.json` — `Imap` section.
- Tests — new `Email`/poller unit tests; pipeline email cases; opt-in live IMAP test.
