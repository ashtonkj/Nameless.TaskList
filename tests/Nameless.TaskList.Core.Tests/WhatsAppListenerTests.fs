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
