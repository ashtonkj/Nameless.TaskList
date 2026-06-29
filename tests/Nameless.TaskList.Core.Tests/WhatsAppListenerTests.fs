module Nameless.TaskList.Core.Tests.WhatsAppListenerTests

open System
open Nameless.TaskList.Core
open Xunit

let private sample =
    """{"id":"ACCC08","chat_jid":"123@newsletter","sender":"123","is_from_me":false,"media_type":"","album_id":null,"timestamp":"2026-06-18T13:08:11+02:00"}"""

[<Fact>]
let ``parse reads id, chat_jid and timestamp from a valid payload`` () =
    match WhatsAppListener.parse sample with
    | Some p ->
        Assert.Equal("ACCC08", p.Id)
        Assert.Equal("123@newsletter", p.ChatJid)
        Assert.Equal(DateTimeOffset(2026, 6, 18, 13, 8, 11, TimeSpan.FromHours 2.0), p.Timestamp)
    | None -> failwith "expected Some"

[<Fact>]
let ``parse returns None on malformed json`` () =
    Assert.Equal(None, WhatsAppListener.parse "{not json")

[<Fact>]
let ``parse returns None when id is missing`` () =
    Assert.Equal(None, WhatsAppListener.parse """{"chat_jid":"x@s"}""")

[<Fact>]
let ``parse returns None when chat_jid is blank`` () =
    Assert.Equal(None, WhatsAppListener.parse """{"id":"a","chat_jid":"  "}""")

[<Fact>]
let ``parse tolerates a missing timestamp`` () =
    match WhatsAppListener.parse """{"id":"a","chat_jid":"x@s"}""" with
    | Some p -> Assert.Equal(DateTimeOffset.MinValue, p.Timestamp)
    | None -> failwith "expected Some"

open Nameless.TaskList.Core.Ports

// IMessageSource fake whose GetMessagesSince returns a fixed list and records the `since` asked for.
type FakeSince(sinceList: ChatMessage list) =
    member val AskedSince = DateTime.MinValue with get, set
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = None
        member _.GetRecent(_jid, _before, _ex) = []
        member this.GetMessagesSince(_chatJid, since) =
            this.AskedSince <- since
            sinceList
        member _.GetMediaBytes(_id, _jid) = None

// IMessageSource fake whose GetMessagesSince honours the cursor like the real
// SQL (`m.timestamp >= @Since`), so it can exercise the inclusive boundary.
type private FilteringSince(msgs: ChatMessage list) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = None
        member _.GetRecent(_jid, _before, _ex) = []
        member _.GetMessagesSince(_chatJid, since) =
            msgs |> List.filter (fun m -> m.Timestamp >= since)
        member _.GetMediaBytes(_id, _jid) = None

let private msg id (ts: DateTime) : ChatMessage =
    { Id = id; ChatJid = "c@s"; ChatName = "C"; NormalizedChatName = "C"; IsGroup = false
      SenderId = "c"; SenderName = "C"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Platform = "whatsapp-direct"; IsBroadcast = false
      Content = "hi"; MediaType = null; FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = ts }

[<Fact>]
let ``catchUp processes since-cursor messages and advances to the latest timestamp`` () =
    let t1 = DateTime(2026, 6, 18, 10, 0, 0)
    let t2 = DateTime(2026, 6, 18, 11, 0, 0)
    let mb = FakeSince([ msg "<a>" t1; msg "<b>" t2 ])
    let seen = ResizeArray<string>()
    let next = WhatsAppListener.catchUp mb (fun id _ -> seen.Add id) { Since = t1 }
    Assert.Equal(t1, mb.AskedSince)                  // queried from the stored cursor
    Assert.Equal<string list>([ "<a>"; "<b>" ], List.ofSeq seen)
    Assert.Equal(t2, next.Since)                      // advanced to the latest processed

[<Fact>]
let ``catchUp on no new messages leaves the cursor unchanged`` () =
    let t = DateTime(2026, 6, 18, 10, 0, 0)
    let mb = FakeSince([])
    let seen = ResizeArray<string>()
    let next = WhatsAppListener.catchUp mb (fun id _ -> seen.Add id) { Since = t }
    Assert.Empty(seen)
    Assert.Equal(t, next.Since)

[<Fact>]
let ``catchUp re-fetches the boundary message at the cursor (inclusive >=)`` () =
    let t1 = DateTime(2026, 6, 18, 10, 0, 0)
    let t2 = DateTime(2026, 6, 18, 11, 0, 0)
    let mb = FilteringSince([ msg "<a>" t1; msg "<b>" t2 ])
    // First drain from the beginning: both processed, cursor advances to t2.
    let first = WhatsAppListener.catchUp mb (fun _ _ -> ()) { Since = DateTime.MinValue }
    Assert.Equal(t2, first.Since)
    // Second drain from the advanced cursor: the message exactly AT t2 is re-fetched
    // (SQL filter is `timestamp >= since`); idempotency, not the cursor, dedups it.
    let seen = ResizeArray<string>()
    let second = WhatsAppListener.catchUp mb (fun id _ -> seen.Add id) first
    Assert.Equal<string list>([ "<b>" ], List.ofSeq seen)
    Assert.Equal(t2, second.Since)

open System.Threading

// Fake listener: records Subscribe, then WaitNext returns scripted batches in order; when
// exhausted it throws OperationCanceledException to end the session cleanly.
type FakeListener(batches: string list list, events: ResizeArray<string>) =
    let queue = System.Collections.Generic.Queue<string list>(batches)
    interface INotificationListener with
        member _.Subscribe(channel) = events.Add(sprintf "subscribe:%s" channel)
        member _.WaitNext(_token) =
            if queue.Count = 0 then raise (OperationCanceledException())
            else queue.Dequeue()

type FakeCursorStore() =
    member val Saved = ResizeArray<ListenCursor>() with get
    member val Current = { Since = DateTime.MinValue } with get, set
    interface IListenCursorStore with
        member this.Load() = this.Current
        member this.Save(c) = this.Current <- c; this.Saved.Add c

let private payload id (iso: string) =
    sprintf """{"id":"%s","chat_jid":"c@s","timestamp":"%s"}""" id iso

[<Fact>]
let ``runSession subscribes before catch-up, then dispatches live payloads`` () =
    let events = ResizeArray<string>()
    // Catch-up source records "catchup" so we can assert ordering relative to subscribe.
    let mb =
        { new IMessageSource with
            member _.GetMessage(_, _) = None
            member _.GetRecent(_, _, _) = []
            member _.GetMessagesSince(_, _) = events.Add "catchup"; []
            member _.GetMediaBytes(_, _) = None }
    let listener = FakeListener([ [ payload "<a>" "2026-06-18T10:00:00+02:00" ] ], events)
    let store = FakeCursorStore()
    WhatsAppListener.runSession listener store mb
        (fun id _ -> events.Add(sprintf "process:%s" id))
        (TimeSpan.FromHours 2.0) "whatsapp_new_message" (fun _ -> ()) CancellationToken.None
    Assert.Equal<string list>(
        [ "subscribe:whatsapp_new_message"; "catchup"; "process:<a>" ], List.ofSeq events)
    // cursor advanced to the live payload's wall-clock timestamp at +02:00
    Assert.Equal(DateTime(2026, 6, 18, 10, 0, 0), store.Current.Since)

[<Fact>]
let ``runSession skips an unparseable payload without dispatching or dying`` () =
    let events = ResizeArray<string>()
    let mb =
        { new IMessageSource with
            member _.GetMessage(_, _) = None
            member _.GetRecent(_, _, _) = []
            member _.GetMessagesSince(_, _) = []
            member _.GetMediaBytes(_, _) = None }
    let listener = FakeListener([ [ "{garbage"; payload "<b>" "2026-06-18T11:00:00+02:00" ] ], ResizeArray())
    let store = FakeCursorStore()
    let skipped = ResizeArray<string>()
    WhatsAppListener.runSession listener store mb
        (fun id _ -> events.Add(sprintf "process:%s" id))
        (TimeSpan.FromHours 2.0) "whatsapp_new_message" (fun s -> skipped.Add s) CancellationToken.None
    Assert.Equal<string list>([ "process:<b>" ], List.ofSeq events)   // good one still processed
    Assert.Equal(1, skipped.Count)                                    // bad one logged

[<Fact>]
let ``runSession saves cursor in the configured offset's wall-clock`` () =
    // UTC instant: 2026-06-18T07:30:00Z  (+02:00 wall-clock: 09:30  |  +05:30 wall-clock: 13:00)
    // The payload carries +02:00 but runSession must re-shift to the configured offset.
    let mb =
        { new IMessageSource with
            member _.GetMessage(_, _) = None
            member _.GetRecent(_, _, _) = []
            member _.GetMessagesSince(_, _) = []
            member _.GetMediaBytes(_, _) = None }
    // Payload is expressed in +02:00 — its UTC equivalent is 07:30Z.
    let listener = FakeListener([ [ payload "<x>" "2026-06-18T09:30:00+02:00" ] ], ResizeArray())
    let store = FakeCursorStore()
    // Run with +05:30 offset — cursor Since should be 07:30Z re-expressed as 13:00 local.
    WhatsAppListener.runSession listener store mb
        (fun _ _ -> ())
        (TimeSpan.FromHours 5.5) "whatsapp_new_message" (fun _ -> ()) CancellationToken.None
    Assert.Equal(DateTime(2026, 6, 18, 13, 0, 0), store.Current.Since)

open System.IO
open Nameless.TaskList.Core.Adapters

[<Fact>]
let ``FileSystemListenCursorStore round-trips, defaulting to MinValue when missing`` () =
    let path = Path.Combine(Path.GetTempPath(), "wa-cursor-" + Guid.NewGuid().ToString("N") + ".json")
    try
        let store = FileSystemListenCursorStore(path) :> IListenCursorStore
        Assert.Equal({ Since = DateTime.MinValue }, store.Load())
        let c = { Since = DateTime(2026, 6, 18, 12, 0, 0) }
        store.Save c
        Assert.Equal(c, (FileSystemListenCursorStore(path) :> IListenCursorStore).Load())
    finally
        (try File.Delete path with _ -> ())
