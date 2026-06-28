module Nameless.TaskList.Core.Tests.EmailPollerTests

open System
open System.IO
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Ports
open Xunit

// Fake mailbox: a fixed UIDVALIDITY and a canned list. FetchSince filters by UID and records
// the sinceUid it was asked for; HighestUid reports the largest canned UID (0 when empty).
type FakeMailbox(validity: uint32, mails: RawEmail list) =
    member val LastSince = 0u with get, set
    interface IMailbox with
        member _.UidValidity(_folder) = validity
        member _.HighestUid(_folder) = mails |> List.fold (fun acc m -> max acc m.Uid) 0u
        member this.FetchSince(_folder, sinceUid) =
            this.LastSince <- sinceUid
            mails |> List.filter (fun m -> m.Uid > sinceUid)

let mkRaw uid id : RawEmail =
    { MessageId = id; FromAddress = "a@b.com"; FromDisplay = "A B"
      Date = DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero)
      Subject = "s"; TextBody = "body"; HtmlBody = ""
      ListUnsubscribe = false; Precedence = ""; Uid = uid }

// An initialised cursor for a known mailbox (the normal resume case).
let resumeFrom validity lastUid : EmailCursor =
    { UidValidity = validity; LastUid = lastUid; Initialized = true }

[<Fact>]
let ``fetch resumes from the stored uid and advances the cursor`` () =
    let mb = FakeMailbox(7u, [ mkRaw 10u "<a>"; mkRaw 11u "<b>" ])
    let mails, next = EmailPoller.fetch mb (resumeFrom 7u 9u) "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal(9u, mb.LastSince)            // fetched since the stored uid
    Assert.Equal(2, List.length mails)
    Assert.Equal(7u, next.UidValidity)
    Assert.Equal(11u, next.LastUid)           // advanced to the highest seen
    Assert.True(next.Initialized)

[<Fact>]
let ``fetch on an empty mailbox keeps the resumed cursor where it was`` () =
    let mb = FakeMailbox(7u, [])
    let mails, next = EmailPoller.fetch mb (resumeFrom 7u 9u) "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Empty(mails)
    Assert.Equal(resumeFrom 7u 9u, next)

[<Fact>]
let ``fetch maps raw mail to email-platform ChatMessages`` () =
    let mb = FakeMailbox(7u, [ mkRaw 10u "<a>" ])
    let mails, _ = EmailPoller.fetch mb (resumeFrom 7u 0u) "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal("email", (List.head mails).Platform)

// --- Backfill guard: first enable / UIDVALIDITY reset must NOT scan the whole mailbox ---

[<Fact>]
let ``fetch on first enable seeds the cursor forward and processes nothing with backfill 0`` () =
    let mb = FakeMailbox(7u, [ mkRaw 10u "<a>"; mkRaw 11u "<b>" ])
    // Default (uninitialised) cursor — the first time the poller ever runs.
    let mails, next = EmailPoller.fetch mb { UidValidity = 0u; LastUid = 0u; Initialized = false } "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal(11u, mb.LastSince)           // seeded to the highest UID, not 0 (no backfill)
    Assert.Empty(mails)                       // go-forward only: nothing processed on first enable
    Assert.Equal(7u, next.UidValidity)
    Assert.Equal(11u, next.LastUid)
    Assert.True(next.Initialized)

[<Fact>]
let ``fetch on first enable backfills only the newest N when backfill is set`` () =
    let mb = FakeMailbox(7u, [ mkRaw 8u "<a>"; mkRaw 9u "<b>"; mkRaw 10u "<c>"; mkRaw 11u "<d>" ])
    let mails, next = EmailPoller.fetch mb { UidValidity = 0u; LastUid = 0u; Initialized = false } "INBOX" 2u (TimeSpan.FromHours 2.0)
    Assert.Equal(9u, mb.LastSince)            // highest(11) - backfill(2) = 9
    Assert.Equal(2, List.length mails)        // only uids 10 and 11
    Assert.Equal(11u, next.LastUid)

[<Fact>]
let ``fetch backfill larger than the mailbox starts from zero`` () =
    let mb = FakeMailbox(7u, [ mkRaw 3u "<a>" ])
    let mails, _ = EmailPoller.fetch mb { UidValidity = 0u; LastUid = 0u; Initialized = false } "INBOX" 100u (TimeSpan.FromHours 2.0)
    Assert.Equal(0u, mb.LastSince)            // highest(3) <= backfill(100) -> from 0, no underflow
    Assert.Equal(1, List.length mails)

[<Fact>]
let ``fetch re-seeds forward on a uidvalidity change instead of rescanning everything`` () =
    let mb = FakeMailbox(99u, [ mkRaw 40u "<a>"; mkRaw 41u "<b>" ])
    // Stored cursor is initialised but for a stale UIDVALIDITY.
    let mails, next = EmailPoller.fetch mb (resumeFrom 7u 50u) "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal(41u, mb.LastSince)           // re-seeded forward to the new highest, not 0
    Assert.Empty(mails)
    Assert.Equal(99u, next.UidValidity)
    Assert.Equal(41u, next.LastUid)

[<Fact>]
let ``fetch does not skip the first message that arrives after seeding an empty mailbox`` () =
    // Tick 1: first enable against an empty mailbox seeds {validity, 0, Initialized=true}.
    let empty = FakeMailbox(7u, [])
    let _, seeded = EmailPoller.fetch empty { UidValidity = 0u; LastUid = 0u; Initialized = false } "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal(0u, seeded.LastUid)
    Assert.True(seeded.Initialized)
    // Tick 2: a message now arrives; resuming from the seeded cursor must fetch it (not skip).
    let withMail = FakeMailbox(7u, [ mkRaw 5u "<first>" ])
    let mails, next = EmailPoller.fetch withMail seeded "INBOX" 0u (TimeSpan.FromHours 2.0)
    Assert.Equal(1, List.length mails)
    Assert.Equal("<first>", (List.head mails).Id)
    Assert.Equal(5u, next.LastUid)

[<Fact>]
let ``ImapMessageSource returns a buffered message and recent same-sender mail`` () =
    let src = ImapMessageSource()
    let baseTs = DateTime(2026, 6, 15, 14, 0, 0)
    let mk id ts =
        { (Email.toChatMessage (TimeSpan.FromHours 2.0) (mkRaw 1u id)) with ChatJid = "a@b.com"; Timestamp = ts }
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
        Assert.Equal({ UidValidity = 0u; LastUid = 0u; Initialized = false }, store.Load())
        store.Save { UidValidity = 7u; LastUid = 42u; Initialized = true }
        let reloaded = FileSystemEmailCursorStore(path) :> IEmailCursorStore
        Assert.Equal({ UidValidity = 7u; LastUid = 42u; Initialized = true }, reloaded.Load())
    finally
        (try File.Delete path with _ -> ())
