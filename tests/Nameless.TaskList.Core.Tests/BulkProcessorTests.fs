module Nameless.TaskList.Core.Tests.BulkProcessorTests

open System
open System.Threading
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

let private newJob () : BulkJob =
    { JobId = "j1"; Since = DateTime(2026, 6, 1); ChatJid = ""; StartedAt = DateTime(2026, 6, 1)
      Status = "running"; Total = 0; Processed = 0; Noise = 0; Skipped = 0; Errors = 0; Error = "" }

[<Fact>]
let ``runSince processes messages in order and tallies outcomes`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c"; msg "d" ]) :> IMessageSource
    let seen = System.Collections.Generic.List<string>()
    let processOne id _jid =
        seen.Add(id)
        match id with
        | "a" -> Processed("t", [])
        | "b" -> Skipped
        | "c" -> ProcessedNoise
        | _ -> LlmError "bad"
    let result = runSince src processOne (newJob ()) CancellationToken.None ignore
    Assert.Equal<string list>([ "a"; "b"; "c"; "d" ], List.ofSeq seen)
    Assert.Equal(4, result.Total)
    Assert.Equal(1, result.Processed)
    Assert.Equal(1, result.Skipped)
    Assert.Equal(1, result.Noise)
    Assert.Equal(1, result.Errors)
    Assert.Equal("done", result.Status)

[<Fact>]
let ``runSince counts a thrown processOne as an error and continues`` () =
    let src = FakeSince([ msg "a"; msg "b" ]) :> IMessageSource
    let processOne id _jid = if id = "a" then failwith "boom" else Processed("t", [])
    let result = runSince src processOne (newJob ()) CancellationToken.None ignore
    Assert.Equal(1, result.Errors)
    Assert.Equal(1, result.Processed)
    Assert.Equal("done", result.Status)

[<Fact>]
let ``runSince reports progress once per message`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    let mutable calls = 0
    runSince src (fun _ _ -> Skipped) (newJob ()) CancellationToken.None (fun _ -> calls <- calls + 1) |> ignore
    Assert.Equal(4, calls)

[<Fact>]
let ``runSince stops early and reports cancelled when the token is already cancelled`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    use cts = new CancellationTokenSource()
    cts.Cancel()
    let result = runSince src (fun _ _ -> Skipped) (newJob ()) cts.Token ignore
    Assert.Equal(0, result.Processed)
    Assert.Equal(0, result.Skipped)
    Assert.Equal("cancelled", result.Status)

[<Fact>]
let ``runSince cancelled mid-run stops and reports partial counts`` () =
    let src = FakeSince([ msg "a"; msg "b"; msg "c" ]) :> IMessageSource
    use cts = new CancellationTokenSource()
    // Cancel after the first message is processed.
    let processOne _id _jid = cts.Cancel(); Skipped
    let result = runSince src processOne (newJob ()) cts.Token ignore
    Assert.Equal(1, result.Skipped)
    Assert.Equal("cancelled", result.Status)

[<Fact>]
let ``isRunning is true when any job has running status`` () =
    let job s = { newJob () with Status = s }
    Assert.False(isRunning [ job "done" ])
    Assert.True(isRunning [ job "done"; job "running" ])
    Assert.False(isRunning [])

[<Fact>]
let ``prune keeps all running jobs plus the latest N non-running`` () =
    let job id status (started: DateTime) =
        { newJob () with JobId = id; Status = status; StartedAt = started }
    let jobs =
        [ job "r1" "running" (DateTime(2026, 1, 1))
          job "d1" "done" (DateTime(2026, 1, 2))
          job "d2" "done" (DateTime(2026, 1, 3))
          job "d3" "done" (DateTime(2026, 1, 4)) ]
    let kept = prune 2 jobs |> List.map (fun j -> j.JobId) |> Set.ofList
    Assert.True(Set.contains "r1" kept)   // running never evicted
    Assert.True(Set.contains "d3" kept)   // newest non-running
    Assert.True(Set.contains "d2" kept)
    Assert.False(Set.contains "d1" kept)  // oldest non-running evicted
