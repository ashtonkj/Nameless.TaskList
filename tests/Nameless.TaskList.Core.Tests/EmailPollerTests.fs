module Nameless.TaskList.Core.Tests.EmailPollerTests

open System
open System.IO
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Adapters
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
