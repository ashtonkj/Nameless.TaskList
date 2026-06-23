module Nameless.TaskList.Core.Tests.BulkProcessorTests

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.BulkProcessor
open Xunit

// IMessageSource fake that returns a fixed since-list (ignores GetMessage/GetRecent).
type private FakeSince(msgs: ChatMessage list) =
    interface IMessageSource with
        member _.GetMessage(_id, _jid) = None
        member _.GetRecent(_jid, _before, _ex) = []
        member _.GetMessagesSince(_chatJid, _since) = msgs
        member _.GetMediaBytes(_id, _jid) = None

let private msg (id: string) : ChatMessage =
    { Id = id; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = "s"; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Content = "x"; MediaType = null
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = DateTime(2026, 6, 1) }

[<Fact>]
let ``runSince processes messages in order and tallies outcomes`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c"; msg "d" ]) :> IMessageSource
    let seen = System.Collections.Generic.List<string>()
    // a -> Processed, b -> Skipped, c -> ProcessedNoise, d -> LlmError
    let processOne id _jid =
        seen.Add(id)
        match id with
        | "a" -> Processed("t", [])
        | "b" -> Skipped
        | "c" -> ProcessedNoise
        | _ -> LlmError "bad"
    let result = runSince src processOne (DateTime(2026,6,1)) None ignore
    Assert.Equal<string list>([ "a"; "b"; "c"; "d" ], List.ofSeq seen)
    Assert.Equal(4, result.Total)
    Assert.Equal(1, result.Processed)
    Assert.Equal(1, result.Skipped)
    Assert.Equal(1, result.Noise)
    Assert.Equal(1, result.Errors)   // LlmError counts as an error
    Assert.True(result.Done)

[<Fact>]
let ``runSince counts a thrown processOne as an error and continues`` () =
    let src = FakeSince([ msg "a"; msg "b" ]) :> IMessageSource
    let processOne id _jid = if id = "a" then failwith "boom" else Processed("t", [])
    let result = runSince src processOne (DateTime(2026,6,1)) None ignore
    Assert.Equal(1, result.Errors)
    Assert.Equal(1, result.Processed)   // b still processed
    Assert.True(result.Done)

[<Fact>]
let ``runSince reports progress once per message`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    let mutable calls = 0
    runSince src (fun _ _ -> Skipped) (DateTime(2026,6,1)) None (fun _ -> calls <- calls + 1) |> ignore
    Assert.True(calls >= 3)   // at least once per message (final report may add one)

[<Fact>]
let ``isRunning is true when any job is not done`` () =
    let done_ = { Total=1; Processed=1; Noise=0; Skipped=0; Errors=0; Done=true; Error="" }
    let running = { done_ with Done=false }
    Assert.False(isRunning [ done_ ])
    Assert.True(isRunning [ done_; running ])
    Assert.False(isRunning [])
