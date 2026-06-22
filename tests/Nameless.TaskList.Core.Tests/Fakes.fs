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

/// Returns scripted responses in order. Records how many times Chat was called.
type FakeChatClient(scripted: ChatResponse list) =
    let queue = Queue<ChatResponse>(scripted)
    member val Calls = 0 with get, set
    interface IChatClient with
        member this.Chat(_messages, _tools) =
            this.Calls <- this.Calls + 1
            queue.Dequeue()

module Responses =
    let final (content: string) : ChatResponse =
        { Message = { Content = content; ToolCalls = [||] } }

    let toolCall (name: string) : ChatResponse =
        { Message =
            { Content = ""
              ToolCalls = [| { Function = { Name = name; Arguments = Dictionary<string, obj>() } } |] } }
