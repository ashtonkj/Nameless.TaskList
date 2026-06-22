module Nameless.TaskList.Core.Tests.Fakes

open System.Collections.Generic
open Nameless.TaskList.Core.Ports

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
