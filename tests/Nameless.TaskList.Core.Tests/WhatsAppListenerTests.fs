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
