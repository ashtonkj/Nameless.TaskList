# IMAP Email Channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add email as a second input channel — a background service polls IMAP, maps each new mail to the existing `ChatMessage`, and runs it through the unchanged extraction pipeline.

**Architecture:** Ports & adapters. The pipeline (`Pipeline.processMessage`) is reused as-is; its three WhatsApp-specific spots are generalized to read platform/broadcast from the message. A new `ImapMessageSource : IMessageSource` is fed by an `EmailPoller` that a `BackgroundService` drives on a timer. IMAP sits behind an `IMailbox` port so all logic is testable offline; only the MailKit adapter and one opt-in test touch a real server.

**Tech Stack:** F# / .NET 10, MailKit/MimeKit (IMAP+MIME), xUnit + Xunit.SkippableFact, YamlDotNet (existing), ASP.NET Core hosted service.

## Global Constraints

- Target framework: `net10.0` (all projects).
- KB timestamps are local SAST wall-clock, offset `+02:00` (DESIGN §4/§8). Email `Date` headers must be shifted into SAST before becoming a `ChatMessage.Timestamp`, exactly as `Adapters.readKbTimestamp` does for Postgres.
- No secrets in source. IMAP user/password live only in gitignored `appsettings.Development.json`; non-secret IMAP settings live in `appsettings.json`.
- Types that get JSON/YAML-serialized must NOT be `private` records (a private record serializes to `{}` — see `docs` / the `fsharp-private-record-serialization` lesson). `EmailCursor` is serialized; keep it public.
- All file writes go through `IVault`; the pipeline never deletes.
- Default `dotnet test` must stay fully offline (no live IMAP). Live IMAP coverage is opt-in in `Nameless.TaskList.IntegrationTests` (`-p:Integration=true`) and skips when config is absent.
- Build must pass (`dotnet build`) and all tests green (`dotnet test`) at the end of every task.
- F# compile order is significant: new files must be inserted in `Nameless.TaskList.Core.fsproj` at the position stated, and a type must be defined before it is used.

---

## File Structure

- `src/Nameless.TaskList.Core/Library.fs` — `ChatMessage` gains `Platform` + `IsBroadcast`; new `RawEmail`, `EmailCursor` records.
- `src/Nameless.TaskList.Core/KnowledgeBase.fs` — `Naming` gains `channelPathFor` / `messagePathFor` (platform-aware) + a private `shortHash`.
- `src/Nameless.TaskList.Core/Ports.fs` — new `IMailbox`, `IEmailCursorStore`.
- `src/Nameless.TaskList.Core/Pipeline.fs` — reads `msg.IsBroadcast` / `msg.Platform`; uses the new `Naming` helpers; drop the private `isBroadcastChannel`.
- `src/Nameless.TaskList.Core/Email.fs` — **new** (compiled after `Pipeline.fs`, before `Adapters.fs`): `Email` module (pure `extractText` / `isBulk` / `toChatMessage`) + `EmailPoller` module (`fetch`).
- `src/Nameless.TaskList.Core/Adapters.fs` — `PostgresMessageSource` sets the new fields; new `ImapMessageSource`, `FileSystemEmailCursorStore`, `MailKitMailbox`.
- `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` — add `Email.fs`, MailKit package.
- `src/Nameless.TaskList/ImapPoller.fs` — **new**: `ImapPollerService : BackgroundService`.
- `src/Nameless.TaskList/Program.fs` — config binding + conditional registration of the poller.
- `src/Nameless.TaskList/appsettings.json` — `Imap` section.
- Tests — `EmailTests.fs`, `EmailPollerTests.fs` (new in Core.Tests); updates to existing `ChatMessage` literals; new email cases in `PipelineTests.fs`; opt-in live test in IntegrationTests.

---

## Task 1: Generalize the platform leak

Make the pipeline source-agnostic: `ChatMessage` carries `Platform` + `IsBroadcast`; `Naming` produces email paths; `PostgresMessageSource` sets the fields; the pipeline reads them instead of inspecting the JID. WhatsApp output stays byte-identical.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (end of file — `ChatMessage`)
- Modify: `src/Nameless.TaskList.Core/KnowledgeBase.fs:215-265` (`Naming`)
- Modify: `src/Nameless.TaskList.Core/Pipeline.fs` (`isBroadcastChannel`, `updateChannel`, the `messagePath`/broadcast sites)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (`mapChat`)
- Test: `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`, `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`
- Modify (literal fixes): `tests/Nameless.TaskList.Core.Tests/PromptsTests.fs`, `BulkProcessorTests.fs`, `JobStoreTests.fs`, `tests/Nameless.TaskList.Tests/EndpointTests.fs`

**Interfaces:**
- Produces:
  - `ChatMessage.Platform : string` (`"whatsapp-direct"` | `"whatsapp-group"` | `"email"`)
  - `ChatMessage.IsBroadcast : bool`
  - `Naming.channelPathFor : platform:string -> channelSlug:string -> string`
  - `Naming.messagePathFor : platform:string -> channelSlug:string -> ts:System.DateTime -> messageId:string -> string`

- [ ] **Step 1: Write the failing Naming tests**

Add to `tests/Nameless.TaskList.Core.Tests/NamingTests.fs`:

```fsharp
[<Fact>]
let ``channelPathFor email nests under channels-email`` () =
    Assert.Equal("channels/email/dr-naidoo-practice.md", Naming.channelPathFor "email" "dr-naidoo-practice")

[<Fact>]
let ``channelPathFor whatsapp keeps the whatsapp dir`` () =
    Assert.Equal("channels/whatsapp/wife.md", Naming.channelPathFor "whatsapp-direct" "wife")

[<Fact>]
let ``messagePathFor whatsapp matches the legacy path`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("messages/wife/2026-06-15T14-17-45.md", Naming.messagePathFor "whatsapp-direct" "wife" ts "ignored")

[<Fact>]
let ``messagePathFor email namespaces the folder and hashes the message id`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    let p = Naming.messagePathFor "email" "dr-naidoo-practice" ts "<abc@mail>"
    Assert.StartsWith("messages/email-dr-naidoo-practice/2026-06-15T14-17-45-", p)
    Assert.EndsWith(".md", p)

[<Fact>]
let ``messagePathFor email is stable for the same message id`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal(
        Naming.messagePathFor "email" "c" ts "<id-1@mail>",
        Naming.messagePathFor "email" "c" ts "<id-1@mail>")

[<Fact>]
let ``messagePathFor email differs for different message ids in the same second`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.NotEqual(
        Naming.messagePathFor "email" "c" ts "<id-1@mail>",
        Naming.messagePathFor "email" "c" ts "<id-2@mail>")
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: FAIL — `channelPathFor` / `messagePathFor` not defined (compile error).

- [ ] **Step 3: Add the platform-aware Naming helpers**

In `src/Nameless.TaskList.Core/KnowledgeBase.fs`, inside `module Naming`, after `messagePath` (line ~234) add:

```fsharp
    /// First 8 hex chars of the SHA-1 of the input — a short, stable disambiguator for
    /// email message filenames (timestamp-to-the-second alone can collide for one sender).
    let private shortHash (input: string) : string =
        use sha = System.Security.Cryptography.SHA1.Create()
        let bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(if isNull input then "" else input))
        (System.Convert.ToHexString bytes).ToLowerInvariant().Substring(0, 8)
```

Then after `channelPath` (line ~261) add:

```fsharp
    /// Channel file path for a platform. WhatsApp lands under channels/whatsapp (unchanged);
    /// email lands under channels/email (DESIGN §3).
    let channelPathFor (platform: string) (channelSlug: string) : string =
        let dir = if platform = "email" then "email" else "whatsapp"
        sprintf "channels/%s/%s.md" dir channelSlug

    /// Message file path for a platform. WhatsApp is byte-identical to messagePath. Email
    /// namespaces the folder (messages/email-<slug>/) and appends a message-id hash so two
    /// mails from one sender in the same second never collide (idempotency depends on it).
    let messagePathFor (platform: string) (channelSlug: string) (ts: System.DateTime) (messageId: string) : string =
        if platform = "email" then
            let fn = (messageFileName ts).Replace(".md", sprintf "-%s.md" (shortHash messageId))
            sprintf "messages/email-%s/%s" channelSlug fn
        else messagePath channelSlug ts
```

- [ ] **Step 4: Run the Naming tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~NamingTests"`
Expected: PASS.

- [ ] **Step 5: Add the two fields to `ChatMessage`**

In `src/Nameless.TaskList.Core/Library.fs`, in the `ChatMessage` record, add two fields after `IsFromMe`:

```fsharp
        IsFromMe: bool
        Platform: string
        IsBroadcast: bool
```

- [ ] **Step 6: Set the fields in the Postgres adapter**

In `src/Nameless.TaskList.Core/Adapters.fs`, in `mapChat`, replace the `IsFromMe` line with these three (compute from the JID/group flags already read):

```fsharp
          IsFromMe = reader.GetBoolean(reader.GetOrdinal("is_from_me"))
          Platform = (if reader.GetBoolean(reader.GetOrdinal("is_group")) then "whatsapp-group" else "whatsapp-direct")
          IsBroadcast =
            (let j = reader.GetString(reader.GetOrdinal("chat_jid"))
             j.EndsWith("@newsletter") || j.EndsWith("@broadcast"))
```

- [ ] **Step 7: Make the pipeline read the fields**

In `src/Nameless.TaskList.Core/Pipeline.fs`:

1. Delete the private `isBroadcastChannel` function (the `let private isBroadcastChannel ...` block).
2. Change the message-path line (currently `let messagePath = Naming.messagePath channelSlug msg.Timestamp`) to:

```fsharp
            let messagePath = Naming.messagePathFor msg.Platform channelSlug msg.Timestamp msg.Id
```

3. Change the broadcast guard (currently `if isBroadcastChannel chatJid then`) to:

```fsharp
            if msg.IsBroadcast then
```

4. In `updateChannel`, change the stub's `Platform` line from the `if msg.IsGroup ...` literal to `Platform = msg.Platform`, and change `let path = Naming.channelPath channelSlug` to:

```fsharp
        let path = Naming.channelPathFor msg.Platform channelSlug
```

- [ ] **Step 8: Fix every existing `ChatMessage` literal (build is red until done)**

Add `Platform = "whatsapp-direct"; IsBroadcast = false` after the `IsFromMe = ...` field in each `ChatMessage` literal:
- `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs` — `sampleMessage` (line ~25).
- `tests/Nameless.TaskList.Core.Tests/PromptsTests.fs`, `BulkProcessorTests.fs`, `JobStoreTests.fs`, `tests/Nameless.TaskList.Tests/EndpointTests.fs` — each `ChatMessage` literal (grep `ChatJid =` in each file to find them).

For any group-chat literal, use `Platform = "whatsapp-group"` to match its `IsGroup = true`.

- [ ] **Step 9: Update the broadcast pipeline test to drive off the flag**

In `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`, the test `broadcast channel suppresses task event and commitment extraction` (line ~670) currently relies on the `@newsletter` JID. Make it drive off the message flag instead. Replace its message construction line:

```fsharp
    let d = deps (FakeMessages(Some(sampleMessage ()))) vault chat
```

with:

```fsharp
    let broadcastMsg = { sampleMessage () with IsBroadcast = true }
    let d = deps (FakeMessages(Some broadcastMsg)) vault chat
```

(The `"...@newsletter"` argument to `processMessage` on the next line is now irrelevant but harmless — leave it.)

- [ ] **Step 10: Add a pipeline test that platform reaches the channel file**

Add to `tests/Nameless.TaskList.Core.Tests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``an email message writes a platform-email channel file under channels-email`` () =
    let vault = FakeVault()
    let classify = Responses.final """{"noise":true,"noise_reason":"chit-chat","contexts":[],"intent":"","action_required":false,"urgency":"low","people_mentioned":[],"entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    let chat = FakeChatClient([ classify ])
    let emailMsg =
        { sampleMessage () with
            Platform = "email"; ChatJid = "practice@example.com"
            NormalizedChatName = "Dr Naidoo Practice"; Id = "<m1@example.com>" }
    let d = deps (FakeMessages(Some emailMsg)) vault chat
    match Pipeline.processMessage d "<m1@example.com>" "practice@example.com" with
    | ProcessedNoise ->
        let channel = vault.Files.Keys |> Seq.find (fun k -> k.StartsWith "channels/")
        Assert.StartsWith("channels/email/", channel)
        Assert.Contains("platform: email", vault.Files.[channel])
        Assert.Contains((vault.Files.Keys |> Seq.find (fun k -> k.StartsWith "messages/")), "messages/email-dr-naidoo-practice/")
    | other -> failwithf "expected ProcessedNoise, got %A" other
```

- [ ] **Step 11: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "feat: generalize pipeline platform/broadcast off ChatMessage

Add Platform + IsBroadcast to ChatMessage, platform-aware Naming paths, and
have the pipeline read them instead of inspecting the WhatsApp JID. WhatsApp
output is unchanged; email channels/messages now have a home."
```

---

## Task 2: `Email.fs` — RawEmail + pure mapping

Pure, network-free email logic: text extraction, bulk detection, and `RawEmail → ChatMessage`. Also the `RawEmail`/`EmailCursor` data types and `IMailbox`/`IEmailCursorStore` ports they need.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Library.fs` (add `RawEmail`, `EmailCursor` after `ChatMessage`)
- Modify: `src/Nameless.TaskList.Core/Ports.fs` (add `IMailbox`, `IEmailCursorStore`)
- Create: `src/Nameless.TaskList.Core/Email.fs`
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (add `Email.fs` after `Pipeline.fs`)
- Test: `tests/Nameless.TaskList.Core.Tests/EmailTests.fs` (new; register in the test fsproj)

**Interfaces:**
- Consumes: `ChatMessage` (with `Platform`/`IsBroadcast`), `Adapters.sastOffset` concept (re-implemented locally — see note).
- Produces:
  - `RawEmail = { MessageId:string; FromAddress:string; FromDisplay:string; Date:System.DateTimeOffset; Subject:string; TextBody:string; HtmlBody:string; ListUnsubscribe:bool; Precedence:string; Uid:uint32 }`
  - `EmailCursor = { UidValidity:uint32; LastUid:uint32 }`
  - `IMailbox.UidValidity : folder:string -> uint32`
  - `IMailbox.FetchSince : folder:string * sinceUid:uint32 -> RawEmail list`
  - `IEmailCursorStore.Load : unit -> EmailCursor`
  - `IEmailCursorStore.Save : EmailCursor -> unit`
  - `Email.extractText : RawEmail -> string`
  - `Email.isBulk : RawEmail -> bool`
  - `Email.toChatMessage : RawEmail -> ChatMessage`

- [ ] **Step 1: Add the data types**

In `src/Nameless.TaskList.Core/Library.fs`, after the `ChatMessage` record add:

```fsharp
/// A raw email fetched from IMAP, before mapping to a ChatMessage. Public (not private)
/// because EmailCursor below is serialized and these travel together.
type RawEmail =
    { MessageId: string
      FromAddress: string
      FromDisplay: string
      Date: System.DateTimeOffset
      Subject: string
      TextBody: string
      HtmlBody: string
      ListUnsubscribe: bool
      Precedence: string
      Uid: uint32 }

/// Per-account IMAP poll cursor. Serialized to JSON — keep public (a private record
/// serializes to {}).
type EmailCursor = { UidValidity: uint32; LastUid: uint32 }
```

- [ ] **Step 2: Add the ports**

In `src/Nameless.TaskList.Core/Ports.fs`, after `ITranscriber` add:

```fsharp
/// Reads mail from an IMAP folder. Behind a port so the poller is testable offline.
type IMailbox =
    abstract member UidValidity : folder: string -> uint32
    abstract member FetchSince : folder: string * sinceUid: uint32 -> RawEmail list

/// Persists the per-account IMAP poll cursor.
type IEmailCursorStore =
    abstract member Load : unit -> EmailCursor
    abstract member Save : cursor: EmailCursor -> unit
```

(`RawEmail`/`EmailCursor` are in `Nameless.TaskList.Core` from `Library.fs`, already `open`ed at the top of `Ports.fs`.)

- [ ] **Step 3: Register the new files in the project files**

In `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj`, add a `Compile` line for `Email.fs` immediately AFTER the `Pipeline.fs` line:

```xml
        <Compile Include="Pipeline.fs" />
        <Compile Include="Email.fs" />
```

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add `EmailTests.fs` to the test `Compile` items (place it alongside the other `*Tests.fs`; before `Sanity.fs`/program entry if one exists — match the existing ordering convention in that file).

- [ ] **Step 4: Write the failing pure-logic tests**

Create `tests/Nameless.TaskList.Core.Tests/EmailTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EmailTests

open System
open Nameless.TaskList.Core
open Xunit

let raw () : RawEmail =
    { MessageId = "<m1@example.com>"; FromAddress = "practice@example.com"
      FromDisplay = "Dr Naidoo Practice"
      Date = DateTimeOffset(2026, 6, 15, 12, 17, 45, TimeSpan.Zero)  // 12:17 UTC = 14:17 SAST
      Subject = "Flu vaccine reminder"; TextBody = "Please book Ethan's flu vaccine."
      HtmlBody = ""; ListUnsubscribe = false; Precedence = ""; Uid = 42u }

[<Fact>]
let ``extractText prefers the plain-text body`` () =
    let r = { raw () with TextBody = "plain wins"; HtmlBody = "<p>html</p>" }
    Assert.Equal("plain wins", (Email.extractText r).Trim())

[<Fact>]
let ``extractText falls back to stripped html`` () =
    let r = { raw () with TextBody = ""; HtmlBody = "<p>Hello <b>there</b></p>" }
    Assert.Equal("Hello there", (Email.extractText r).Trim())

[<Fact>]
let ``extractText drops quoted reply chains`` () =
    let body = "My answer is yes.\n\nOn Mon, 14 Jun 2026, Dr Naidoo wrote:\n> original question\n> more quote"
    let r = { raw () with TextBody = body }
    let out = Email.extractText r
    Assert.Contains("My answer is yes.", out)
    Assert.DoesNotContain("original question", out)

[<Fact>]
let ``extractText drops a signature block`` () =
    let r = { raw () with TextBody = "See you then.\n-- \nDr Naidoo\nPaediatrician" }
    let out = Email.extractText r
    Assert.Contains("See you then.", out)
    Assert.DoesNotContain("Paediatrician", out)

[<Fact>]
let ``isBulk is true when list-unsubscribe is present`` () =
    Assert.True(Email.isBulk { raw () with ListUnsubscribe = true })

[<Fact>]
let ``isBulk is true for precedence bulk`` () =
    Assert.True(Email.isBulk { raw () with Precedence = "bulk" })

[<Fact>]
let ``isBulk is false for an ordinary personal mail`` () =
    Assert.False(Email.isBulk (raw ()))

[<Fact>]
let ``toChatMessage maps identity, platform and SAST timestamp`` () =
    let m = Email.toChatMessage (raw ())
    Assert.Equal("<m1@example.com>", m.Id)
    Assert.Equal("practice@example.com", m.ChatJid)
    Assert.Equal("Dr Naidoo Practice", m.NormalizedChatName)
    Assert.Equal("email", m.Platform)
    Assert.False(m.IsGroup)
    Assert.False(m.IsBroadcast)
    Assert.Equal("Please book Ethan's flu vaccine.", m.Content.Trim())
    // 12:17 UTC shifted to +02:00 SAST
    Assert.Equal(14, m.Timestamp.Hour)

[<Fact>]
let ``toChatMessage marks bulk mail as broadcast`` () =
    let m = Email.toChatMessage { raw () with ListUnsubscribe = true }
    Assert.True(m.IsBroadcast)

[<Fact>]
let ``toChatMessage falls back to the from address when display is blank`` () =
    let m = Email.toChatMessage { raw () with FromDisplay = "" }
    Assert.Equal("practice@example.com", m.NormalizedChatName)
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailTests"`
Expected: FAIL — `Email` module not defined.

- [ ] **Step 6: Implement `Email.fs`**

Create `src/Nameless.TaskList.Core/Email.fs`:

```fsharp
namespace Nameless.TaskList.Core

open System
open System.Text.RegularExpressions

module Email =

    // KB timestamps are SAST wall-clock (+02:00) per DESIGN §4/§8.
    let private sastOffset = TimeSpan.FromHours 2.0

    /// Crude HTML→text: drop tags, collapse whitespace, decode the few common entities.
    let private htmlToText (html: string) : string =
        if String.IsNullOrWhiteSpace html then ""
        else
            let noTags = Regex.Replace(html, "<[^>]+>", " ")
            let decoded =
                noTags.Replace("&nbsp;", " ").Replace("&amp;", "&")
                      .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            Regex.Replace(decoded, @"[ \t]+", " ").Trim()

    /// Strip quoted reply chains and signature blocks from a plain-text body.
    let private stripQuotedAndSignature (text: string) : string =
        if String.IsNullOrWhiteSpace text then ""
        else
            let lines = text.Replace("\r\n", "\n").Split('\n')
            // Cut at the first "On ... wrote:" attribution line (start of a reply chain).
            let attribution = Regex(@"^On .+ wrote:\s*$", RegexOptions.IgnoreCase)
            // Cut at a signature delimiter line ("-- ").
            let isSig (l: string) = l.TrimEnd() = "--" || l = "-- "
            let kept = ResizeArray<string>()
            let mutable stop = false
            for l in lines do
                if not stop then
                    if attribution.IsMatch l || isSig l then stop <- true
                    elif (l.TrimStart().StartsWith ">") then ()   // drop quoted lines
                    else kept.Add l
            String.Join("\n", kept).Trim()

    /// The clean message body: prefer text/plain, fall back to stripped HTML.
    let extractText (email: RawEmail) : string =
        let primary =
            if not (String.IsNullOrWhiteSpace email.TextBody) then email.TextBody
            else htmlToText email.HtmlBody
        stripQuotedAndSignature primary

    /// One-to-many / list mail — routed to log-only, like a WhatsApp newsletter.
    let isBulk (email: RawEmail) : bool =
        email.ListUnsubscribe
        || (match (if isNull email.Precedence then "" else email.Precedence).Trim().ToLowerInvariant() with
            | "bulk" | "list" | "junk" -> true
            | _ -> false)

    /// Map a fetched email onto the pipeline's ChatMessage shape.
    let toChatMessage (email: RawEmail) : ChatMessage =
        let display =
            if String.IsNullOrWhiteSpace email.FromDisplay then email.FromAddress else email.FromDisplay
        { Id = email.MessageId
          ChatJid = email.FromAddress
          ChatName = display
          NormalizedChatName = display
          IsGroup = false
          SenderId = email.FromAddress
          SenderName = display
          SenderPushName = null
          SenderSavedName = null
          SenderBusinessName = null
          IsFromMe = false
          Platform = "email"
          IsBroadcast = isBulk email
          Content = extractText email
          MediaType = null
          FileName = null
          AlbumId = null
          AlbumIndex = None
          Timestamp = email.Date.ToOffset(sastOffset).DateTime }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailTests"`
Expected: PASS.

- [ ] **Step 8: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: email RawEmail type + pure mapping (extract/bulk/toChatMessage)"
```

---

## Task 3: `EmailPoller.fetch` — cursor-driven fetch loop

The pure fetch step: read the stored cursor, pull new mail (resetting on a UIDVALIDITY change), map to `ChatMessage`s, and compute the cursor to persist after they are processed. No pipeline dependency, so it is testable with a fake `IMailbox`.

**Files:**
- Modify: `src/Nameless.TaskList.Core/Email.fs` (add an `EmailPoller` module below `Email`)
- Test: `tests/Nameless.TaskList.Core.Tests/EmailPollerTests.fs` (new; register in the test fsproj)

**Interfaces:**
- Consumes: `IMailbox`, `EmailCursor`, `RawEmail`, `Email.toChatMessage`, `ChatMessage`.
- Produces: `EmailPoller.fetch : mailbox:IMailbox -> stored:EmailCursor -> folder:string -> ChatMessage list * EmailCursor`

- [ ] **Step 1: Register the test file**

In `tests/Nameless.TaskList.Core.Tests/Nameless.TaskList.Core.Tests.fsproj`, add `EmailPollerTests.fs` to the `Compile` items (next to `EmailTests.fs`).

- [ ] **Step 2: Write the failing poller tests**

Create `tests/Nameless.TaskList.Core.Tests/EmailPollerTests.fs`:

```fsharp
module Nameless.TaskList.Core.Tests.EmailPollerTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Xunit

// Fake mailbox: a fixed UIDVALIDITY and a canned list; FetchSince filters by UID.
type FakeMailbox(validity: uint32, mails: RawEmail list) =
    member val LastSince = 0u with get, set
    interface IMailbox with
        member _.UidValidity(_folder) = validity
        member this.FetchSince(_folder, sinceUid) =
            this.LastSince <- sinceUid
            mails |> List.filter (fun m -> m.Uid > sinceUid)

let mkRaw uid id : RawEmail =
    { MessageId = id; FromAddress = "a@b.com"; FromDisplay = "A B"
      Date = DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)
      Subject = "s"; TextBody = "body"; HtmlBody = ""
      ListUnsubscribe = false; Precedence = ""; Uid = uid }

[<Fact>]
let ``fetch returns new mail above the stored uid and advances the cursor`` () =
    let mb = FakeMailbox(7u, [ mkRaw 10u "<a>"; mkRaw 11u "<b>" ])
    let stored = { UidValidity = 7u; LastUid = 9u }
    let mails, next = EmailPoller.fetch mb stored "INBOX"
    Assert.Equal(9u, mb.LastSince)            // fetched since the stored uid
    Assert.Equal(2, List.length mails)
    Assert.Equal(7u, next.UidValidity)
    Assert.Equal(11u, next.LastUid)           // advanced to the highest seen

[<Fact>]
let ``fetch resets to a full scan when uidvalidity changed`` () =
    let mb = FakeMailbox(99u, [ mkRaw 1u "<a>" ])
    let stored = { UidValidity = 7u; LastUid = 50u }
    let mails, next = EmailPoller.fetch mb stored "INBOX"
    Assert.Equal(0u, mb.LastSince)            // ignored the stale cursor, scanned from 0
    Assert.Equal(1, List.length mails)
    Assert.Equal(99u, next.UidValidity)
    Assert.Equal(1u, next.LastUid)

[<Fact>]
let ``fetch on an empty mailbox keeps the cursor where it was`` () =
    let mb = FakeMailbox(7u, [])
    let stored = { UidValidity = 7u; LastUid = 9u }
    let mails, next = EmailPoller.fetch mb stored "INBOX"
    Assert.Empty(mails)
    Assert.Equal({ UidValidity = 7u; LastUid = 9u }, next)

[<Fact>]
let ``fetch maps raw mail to email-platform ChatMessages`` () =
    let mb = FakeMailbox(7u, [ mkRaw 10u "<a>" ])
    let mails, _ = EmailPoller.fetch mb { UidValidity = 7u; LastUid = 0u } "INBOX"
    Assert.Equal("email", (List.head mails).Platform)
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailPollerTests"`
Expected: FAIL — `EmailPoller` not defined.

- [ ] **Step 4: Implement `EmailPoller`**

Append to `src/Nameless.TaskList.Core/Email.fs` (below the `Email` module, same namespace):

```fsharp
open Nameless.TaskList.Core.Ports

module EmailPoller =

    /// Fetch new mail since the stored cursor, mapped to ChatMessages, and the cursor to
    /// persist once they are processed. A UIDVALIDITY change resets to a full re-scan
    /// (idempotency by message-id path makes the re-scan harmless). The cursor is returned,
    /// NOT saved, so the caller can persist it only after processing succeeds.
    let fetch (mailbox: IMailbox) (stored: EmailCursor) (folder: string) : ChatMessage list * EmailCursor =
        let validity = mailbox.UidValidity folder
        let since = if validity = stored.UidValidity then stored.LastUid else 0u
        let raws = mailbox.FetchSince(folder, since)
        let highest = raws |> List.fold (fun acc r -> max acc r.Uid) since
        let mails = raws |> List.map Email.toChatMessage
        mails, { UidValidity = validity; LastUid = highest }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailPollerTests"`
Expected: PASS.

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: EmailPoller.fetch — cursor-driven, uidvalidity-aware fetch"
```

---

## Task 4: Adapters — ImapMessageSource, cursor store, MailKit mailbox

The concrete adapters. `ImapMessageSource` (in-process buffer the poller fills) and `FileSystemEmailCursorStore` are unit-tested; `MailKitMailbox` is the live-only edge (covered by Task 6's opt-in test).

**Files:**
- Modify: `src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj` (add MailKit package)
- Modify: `src/Nameless.TaskList.Core/Adapters.fs` (add the three adapters)
- Test: `tests/Nameless.TaskList.Core.Tests/EmailPollerTests.fs` (add adapter tests here)

**Interfaces:**
- Consumes: `IMessageSource`, `IEmailCursorStore`, `IMailbox`, `ChatMessage`, `EmailCursor`, `RawEmail`.
- Produces:
  - `Adapters.ImapMessageSource` with `member Put : ChatMessage -> unit` and `IMessageSource`.
  - `Adapters.FileSystemEmailCursorStore(path:string) : IEmailCursorStore`.
  - `Adapters.MailKitMailbox(host:string, port:int, useSsl:bool, user:string, password:string) : IMailbox`.

- [ ] **Step 1: Add the MailKit package**

Run: `dotnet add src/Nameless.TaskList.Core/Nameless.TaskList.Core.fsproj package MailKit`
Expected: a `<PackageReference Include="MailKit" ... />` appears in the Core fsproj; `dotnet restore` succeeds. (MimeKit comes transitively.)

- [ ] **Step 2: Write the failing adapter tests**

Add to `tests/Nameless.TaskList.Core.Tests/EmailPollerTests.fs`:

```fsharp
open Nameless.TaskList.Core.Adapters
open System.IO

[<Fact>]
let ``ImapMessageSource returns a buffered message and recent same-sender mail`` () =
    let src = ImapMessageSource()
    let baseTs = DateTime(2026, 6, 15, 14, 0, 0)
    let mk id ts =
        { (Email.toChatMessage (mkRaw 1u id)) with ChatJid = "a@b.com"; Timestamp = ts }
    let older = mk "<older>" (baseTs.AddMinutes -10.0)
    let target = mk "<target>" baseTs
    src.Put older
    src.Put target
    let isrc = src :> IMessageSource
    Assert.Equal(Some target, isrc.GetMessage("<target>", "a@b.com"))
    let recent = isrc.GetRecent("a@b.com", baseTs, "<target>")
    Assert.Equal(1, List.length recent)
    Assert.Equal("<older>", (List.head recent).Id)

[<Fact>]
let ``ImapMessageSource returns None for an unknown id`` () =
    let isrc = ImapMessageSource() :> IMessageSource
    Assert.Equal(None, isrc.GetMessage("<nope>", "a@b.com"))

[<Fact>]
let ``FileSystemEmailCursorStore round-trips, defaulting to zero when missing`` () =
    let path = Path.Combine(Path.GetTempPath(), "cursor-" + Guid.NewGuid().ToString("N") + ".json")
    try
        let store = FileSystemEmailCursorStore(path) :> IEmailCursorStore
        Assert.Equal({ UidValidity = 0u; LastUid = 0u }, store.Load())
        store.Save { UidValidity = 7u; LastUid = 42u }
        let reloaded = FileSystemEmailCursorStore(path) :> IEmailCursorStore
        Assert.Equal({ UidValidity = 7u; LastUid = 42u }, reloaded.Load())
    finally
        (try File.Delete path with _ -> ())
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailPollerTests"`
Expected: FAIL — `ImapMessageSource` / `FileSystemEmailCursorStore` not defined.

- [ ] **Step 4: Implement the buffer source + cursor store**

In `src/Nameless.TaskList.Core/Adapters.fs`, add `open System.Collections.Concurrent` to the top `open`s if not present, then add inside `module Adapters` (e.g. after `FileSystemJobStore`):

```fsharp
    // ---- Email message source: an in-process buffer the poller fills before invoking the
    //      pipeline, so GetMessage/GetRecent need no second IMAP round-trip. ----
    type ImapMessageSource() =
        let buffer = ConcurrentDictionary<string, ChatMessage>()
        member _.Put(m: ChatMessage) = buffer.[m.Id] <- m
        interface IMessageSource with
            member _.GetMessage(id, _chatJid) =
                match buffer.TryGetValue id with
                | true, m -> Some m
                | _ -> None
            member _.GetRecent(chatJid, before, excludingId) =
                buffer.Values
                |> Seq.filter (fun m -> m.ChatJid = chatJid && m.Timestamp < before && m.Id <> excludingId)
                |> Seq.sortByDescending (fun m -> m.Timestamp)
                |> Seq.truncate 5
                |> List.ofSeq
            member _.GetMessagesSince(_chatJid, _since) = []
            member _.GetMediaBytes(_id, _chatJid) = None

    // ---- Email poll cursor over a single JSON file. ----
    type FileSystemEmailCursorStore(path: string) =
        interface IEmailCursorStore with
            member _.Save(cursor) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                File.WriteAllText(path, JsonSerializer.Serialize(cursor))
            member _.Load() =
                try
                    if File.Exists path then JsonSerializer.Deserialize<EmailCursor>(File.ReadAllText path)
                    else { UidValidity = 0u; LastUid = 0u }
                with _ -> { UidValidity = 0u; LastUid = 0u }
```

(`ChatMessage`/`EmailCursor` are in this namespace; `JsonSerializer` and `IMessageSource`/`IEmailCursorStore` are already in scope from the existing `open`s.)

- [ ] **Step 5: Run the adapter tests to verify they pass**

Run: `dotnet test tests/Nameless.TaskList.Core.Tests --filter "FullyQualifiedName~EmailPollerTests"`
Expected: PASS.

- [ ] **Step 6: Implement the MailKit mailbox (no unit test — exercised live in Task 6)**

In `src/Nameless.TaskList.Core/Adapters.fs`, add `open MailKit` and `open MailKit.Net.Imap` and `open MailKit.Search` to the top `open`s, then add inside `module Adapters`:

```fsharp
    // ---- IMAP mailbox over MailKit. Synchronous by design, like the other adapters. ----
    type MailKitMailbox(host: string, port: int, useSsl: bool, user: string, password: string) =
        let connect () =
            let client = new ImapClient()
            client.Connect(host, port, useSsl)
            client.Authenticate(user, password)
            client
        interface IMailbox with
            member _.UidValidity(folder) =
                use client = connect ()
                let fld = client.GetFolder(folder)
                fld.Open(FolderAccess.ReadOnly) |> ignore
                let v = fld.UidValidity
                client.Disconnect(true)
                uint32 v
            member _.FetchSince(folder, sinceUid) =
                use client = connect ()
                let fld = client.GetFolder(folder)
                fld.Open(FolderAccess.ReadOnly) |> ignore
                // UID range strictly greater than the cursor: [sinceUid+1 .. *].
                let range =
                    UniqueIdRange(UniqueId(fld.UidValidity, sinceUid + 1u), UniqueId.MaxValue)
                let uids = fld.Search(SearchQuery.Uids(UniqueIdSet(range)))
                let results =
                    [ for uid in uids do
                        let msg = fld.GetMessage(uid)
                        let fromAddr =
                            msg.From.Mailboxes |> Seq.tryHead
                        yield
                            { MessageId = (if isNull msg.MessageId then "" else msg.MessageId)
                              FromAddress = (match fromAddr with Some m -> m.Address | None -> "")
                              FromDisplay = (match fromAddr with Some m -> (if String.IsNullOrWhiteSpace m.Name then m.Address else m.Name) | None -> "")
                              Date = msg.Date
                              Subject = (if isNull msg.Subject then "" else msg.Subject)
                              TextBody = (if isNull msg.TextBody then "" else msg.TextBody)
                              HtmlBody = (if isNull msg.HtmlBody then "" else msg.HtmlBody)
                              ListUnsubscribe = msg.Headers.Contains("List-Unsubscribe")
                              Precedence = (let h = msg.Headers.["Precedence"] in if isNull h then "" else h)
                              Uid = uid.Id } ]
                client.Disconnect(true)
                results
```

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds (MailKit resolves); all tests PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: email adapters — ImapMessageSource, cursor store, MailKit mailbox"
```

---

## Task 5: Host wiring — ImapPollerService + config

A `BackgroundService` that, when `Imap:Enabled`, polls on a timer: build an email `PipelineDeps`, run `EmailPoller.fetch`, push each mail into the buffer source, run the pipeline, then persist the cursor.

**Files:**
- Create: `src/Nameless.TaskList/ImapPoller.fs`
- Modify: `src/Nameless.TaskList/Nameless.TaskList.fsproj` (add `ImapPoller.fs` before `Program.fs`)
- Modify: `src/Nameless.TaskList/Program.fs` (config + conditional registration)
- Modify: `src/Nameless.TaskList/appsettings.json` (Imap section)

**Interfaces:**
- Consumes: `EmailPoller.fetch`, `Adapters.ImapMessageSource`, `Adapters.MailKitMailbox`, `Adapters.FileSystemEmailCursorStore`, `Pipeline.processMessage`, `PipelineDeps`, all the existing port singletons.
- Produces: `ImapPollerService : Microsoft.Extensions.Hosting.BackgroundService`.

- [ ] **Step 1: Add the Imap config section**

In `src/Nameless.TaskList/appsettings.json`, add a top-level `"Imap"` object:

```json
  "Imap": {
    "Enabled": false,
    "Host": "imap.gmail.com",
    "Port": 993,
    "UseSsl": true,
    "Folder": "INBOX",
    "PollSeconds": 120
  }
```

(User/Password are added by the operator to the gitignored `appsettings.Development.json`, e.g. `{ "Imap": { "User": "you@example.com", "Password": "<app-password>" } }` — do not commit them.)

- [ ] **Step 2: Create the poller service**

Create `src/Nameless.TaskList/ImapPoller.fs`:

```fsharp
namespace Nameless.TaskList

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline

/// Polls IMAP on an interval and drives each new mail through the pipeline.
type ImapPollerService
    (mailbox: IMailbox, cursorStore: IEmailCursorStore, source: ImapMessageSource,
     buildEmailDeps: ImapMessageSource -> PipelineDeps, folder: string, pollSeconds: int,
     logger: ILogger<ImapPollerService>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let deps = buildEmailDeps source
            while not stoppingToken.IsCancellationRequested do
                try
                    let stored = cursorStore.Load()
                    let mails, next = EmailPoller.fetch mailbox stored folder
                    for cm in mails do
                        source.Put cm
                        Pipeline.processMessage deps cm.Id cm.ChatJid |> ignore
                    cursorStore.Save next
                    if not (List.isEmpty mails) then
                        logger.LogInformation("IMAP poll processed {Count} message(s)", List.length mails)
                with ex ->
                    logger.LogWarning(ex, "IMAP poll failed; will retry next interval")
                do! Task.Delay(TimeSpan.FromSeconds(float pollSeconds), stoppingToken)
        } :> Task
```

- [ ] **Step 3: Register `ImapPoller.fs` in the web project**

In `src/Nameless.TaskList/Nameless.TaskList.fsproj`, add a `Compile` line for `ImapPoller.fs` immediately BEFORE the `Program.fs` line.

- [ ] **Step 4: Wire it in `Program.fs` behind the `Enabled` flag**

In `src/Nameless.TaskList/Program.fs`, after the existing singleton registrations and before `let app = builder.Build()`, add:

```fsharp
        // Email channel: register the IMAP poller only when enabled (off by default + in tests).
        if cfg.["Imap:Enabled"] = "true" then
            builder.Services.AddHostedService<ImapPollerService>(fun sp ->
                let port = match System.Int32.TryParse(cfg.["Imap:Port"]) with | true, n -> n | _ -> 993
                let useSsl = cfg.["Imap:UseSsl"] <> "false"
                let folder = if System.String.IsNullOrWhiteSpace cfg.["Imap:Folder"] then "INBOX" else cfg.["Imap:Folder"]
                let pollSeconds = match System.Int32.TryParse(cfg.["Imap:PollSeconds"]) with | true, n -> n | _ -> 120
                let mailbox =
                    MailKitMailbox(cfg.["Imap:Host"], port, useSsl, cfg.["Imap:User"], cfg.["Imap:Password"]) :> IMailbox
                let cursorPath = System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "email-cursor.json")
                let cursorStore = FileSystemEmailCursorStore(cursorPath) :> IEmailCursorStore
                let source = ImapMessageSource()
                let buildEmailDeps (s: ImapMessageSource) =
                    buildDeps
                        (s :> IMessageSource)
                        (sp.GetRequiredService<IVault>())
                        (sp.GetRequiredService<IChatClient>())
                        (sp.GetRequiredService<IEmbedder>())
                        (sp.GetRequiredService<IVision>())
                        (sp.GetRequiredService<ITranscriber>())
                let logger = sp.GetRequiredService<ILogger<ImapPollerService>>()
                ImapPollerService(mailbox, cursorStore, source, buildEmailDeps, folder, pollSeconds, logger)) |> ignore
```

Add `open Microsoft.Extensions.Logging` to the top of `Program.fs` if it is not already there. Note: `buildDeps` is the local `let` already defined in `Program.fs`; it must be in scope at this point — if the registration block sits above `buildDeps`'s definition, move this block to just below `buildDeps` (still before `app.Build()`).

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build`
Expected: build succeeds. (No new unit test — this is composition; behavior is covered by Task 3/4 units and Task 6's live test. The build itself verifies the DI signatures line up.)

- [ ] **Step 6: Run the full suite (confirm nothing regressed and poller stays off in tests)**

Run: `dotnet test`
Expected: all tests PASS (the poller is not registered because `Imap:Enabled` defaults to `false`).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: ImapPollerService background poller + Imap config wiring"
```

---

## Task 6: Opt-in live IMAP integration test

A single test that connects to a real mailbox and skips when IMAP config is absent — matching the existing opt-in integration pattern.

**Files:**
- Create: `tests/Nameless.TaskList.IntegrationTests/ImapTests.fs` (register in that project's fsproj)

**Interfaces:**
- Consumes: `Adapters.MailKitMailbox`, `IMailbox`.

- [ ] **Step 1: Inspect an existing integration test for the skip + config pattern**

Run: `ls tests/Nameless.TaskList.IntegrationTests` and open one existing `*.fs` test there.
Note how it (a) reads connection config (env var or appsettings), (b) uses `Xunit.SkippableFact` / `Skip.If` to skip when the service is absent. Mirror that exact pattern below.

- [ ] **Step 2: Write the live test**

Create `tests/Nameless.TaskList.IntegrationTests/ImapTests.fs` (adjust the config-reading + skip mechanism to match the sibling tests found in Step 1):

```fsharp
module Nameless.TaskList.IntegrationTests.ImapTests

open System
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Xunit

// Reads IMAP config from environment; skips when not configured.
let private env k = Environment.GetEnvironmentVariable k

[<SkippableFact>]
let ``MailKitMailbox connects and reports a uidvalidity`` () =
    let host = env "IMAP_HOST"
    let user = env "IMAP_USER"
    let password = env "IMAP_PASSWORD"
    Skip.If(String.IsNullOrWhiteSpace host || String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace password,
            "IMAP_HOST/IMAP_USER/IMAP_PASSWORD not set")
    let port = match Int32.TryParse(env "IMAP_PORT") with | true, n -> n | _ -> 993
    let mailbox = MailKitMailbox(host, port, true, user, password) :> IMailbox
    let validity = mailbox.UidValidity "INBOX"
    Assert.True(validity > 0u)

[<SkippableFact>]
let ``MailKitMailbox fetches recent mail and maps it`` () =
    let host = env "IMAP_HOST"
    let user = env "IMAP_USER"
    let password = env "IMAP_PASSWORD"
    Skip.If(String.IsNullOrWhiteSpace host || String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace password,
            "IMAP_HOST/IMAP_USER/IMAP_PASSWORD not set")
    let port = match Int32.TryParse(env "IMAP_PORT") with | true, n -> n | _ -> 993
    let mailbox = MailKitMailbox(host, port, true, user, password) :> IMailbox
    // Fetch from 0 = everything; just assert it returns without error and the shape is sane.
    let mails = mailbox.FetchSince("INBOX", 0u)
    mails |> List.truncate 1 |> List.iter (fun m -> Assert.False(String.IsNullOrWhiteSpace m.FromAddress))
```

- [ ] **Step 3: Register the test file**

In `tests/Nameless.TaskList.IntegrationTests/*.fsproj`, add `ImapTests.fs` to the `Compile` items (match the existing ordering).

- [ ] **Step 4: Confirm it builds and skips by default**

Run: `dotnet test tests/Nameless.TaskList.IntegrationTests -p:Integration=true --filter "FullyQualifiedName~ImapTests"`
Expected: tests are reported as **skipped** (no IMAP_* env vars set). With `IMAP_HOST`/`IMAP_USER`/`IMAP_PASSWORD` exported, they run and PASS.

- [ ] **Step 5: Confirm the default suite is unaffected**

Run: `dotnet build` then `dotnet test`
Expected: build succeeds; all default tests PASS; integration suite excluded.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "test: opt-in live IMAP integration test for MailKitMailbox"
```

---

## Self-Review

**Spec coverage:**
- Poll IMAP directly → Task 5 (`ImapPollerService`). ✓
- Per-correspondent channels → Task 1 (`channelPathFor` email, `ChatJid` = From address in Task 2). ✓
- INBOX + bulk→log-only → Task 2 (`isBulk` → `IsBroadcast`), Task 1 (pipeline `Logged` path on `IsBroadcast`). ✓
- App-password auth → Task 4 (`MailKitMailbox.Authenticate`), Task 5 (config). ✓
- Generalize platform leak (ChatMessage fields, Naming, Pipeline, Postgres) → Task 1. ✓
- Email port + pure mapping → Task 2. ✓
- Adapters (MailKit, ImapMessageSource, cursor store) → Task 4. ✓
- Poller core → Task 3; hosted service → Task 5. ✓
- Idempotency via message-id hash → Task 1 (`messagePathFor`). ✓
- Config + Enabled gate → Task 5. ✓
- Tests (pure, poller, pipeline email cases, opt-in live) → Tasks 1–4, 6. ✓
- Out of scope (attachments, OAuth2, outbound, IDLE, multi-account) → not implemented, as intended. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; `<app-password>` is illustrative operator config, not plan code.

**Type consistency:** `Platform`/`IsBroadcast` (Task 1) used consistently in Tasks 2–5. `EmailCursor`/`RawEmail` fields (Task 2) match their uses in Tasks 3–4. `EmailPoller.fetch` signature (Task 3) matches its call in Task 5. `ImapMessageSource.Put` + `MailKitMailbox` ctor (Task 4) match their use in Task 5. `IMailbox`/`IEmailCursorStore` (Task 2) match adapters (Task 4) and poller (Tasks 3, 5).

Note for the implementer: MailKit's exact `Search`/`UniqueId` API (Task 4 Step 6) and the IntegrationTests skip mechanism (Task 6) should be confirmed against the installed MailKit version and the sibling integration tests respectively; the shapes shown are correct for MailKit 4.x and the repo's `Xunit.SkippableFact` setup, but adjust if the local versions differ.
