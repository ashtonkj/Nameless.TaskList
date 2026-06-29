module Nameless.TaskList.Core.Tests.Fakes

open System.Collections.Generic
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Conversation

type FakeVault() =
    let files = Dictionary<string, string>()
    member _.Files = files
    member _.Seed(path: string, content: string) = files.[path] <- content
    interface IVault with
        member _.Exists(relPath) = files.ContainsKey(relPath)
        member _.Read(relPath) = files.[relPath]
        member _.Write(relPath, content) = files.[relPath] <- content
        member _.ListFiles(relDir) =
            let prefix = relDir.TrimEnd('/') + "/"
            files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq
        member _.ListFilesRecursive(relDir) =
            let prefix = relDir.TrimEnd('/') + "/"
            files.Keys |> Seq.filter (fun k -> k.StartsWith(prefix)) |> List.ofSeq
        member _.Relocate(src, dst) =
            match files.TryGetValue src with
            | true, content when not (files.ContainsKey dst) ->
                files.Remove src |> ignore
                files.[dst] <- content
            | _ -> ()   // src missing or dst exists -> no-op

/// Returns scripted responses in order. Records how many times Chat was called
/// and the message array passed to each call (for asserting on prompt payloads).
type FakeChatClient(scripted: ChatResponse list) =
    let queue = Queue<ChatResponse>(scripted)
    member val Calls = 0 with get, set
    member val Received = ResizeArray<obj array>() with get
    interface IChatClient with
        member this.Chat(messages, _tools) =
            this.Calls <- this.Calls + 1
            this.Received.Add(messages)
            queue.Dequeue()

/// Test embedder: maps text -> vector via the supplied function (which may throw).
type FakeEmbedder(embed: string -> float array) =
    interface IEmbedder with
        member _.Embed(text) = embed text

/// Test vision adapter: maps image bytes -> text via the supplied function (which may throw).
type FakeVision(describe: byte array -> string) =
    interface IVision with
        member _.Describe(imageBytes) = describe imageBytes

/// Test transcriber: maps audio bytes -> text via the supplied function (which may throw).
type FakeTranscriber(transcribe: byte array -> string) =
    interface ITranscriber with
        member _.Transcribe(audioBytes) = transcribe audioBytes

module Responses =
    let final (content: string) : ChatResponse =
        { Message = { Content = content; ToolCalls = [||] } }

    let toolCall (name: string) : ChatResponse =
        { Message =
            { Content = ""
              ToolCalls = [| { Function = { Name = name; Arguments = Dictionary<string, obj>() } } |] } }
