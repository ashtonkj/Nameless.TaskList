namespace Nameless.TaskList.Eval

open System.Collections.Generic
open System.IO
open Nameless.TaskList.Core.Ports

module Worlds =

    /// Prefix-based in-memory vault, matching the test FakeVault's ListFiles semantics, so the
    /// read-only Tools.* operate over a world exactly as they do over a real FileSystemVault.
    type InMemoryVault(files: IDictionary<string, string>) =
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

    /// Read every *.md under `worldDir` into (vault-relative path, content). The vault-relative
    /// path is the path under the world root with '\' normalised to '/'.
    let private readWorldDir (worldDir: string) : (string * string) list =
        if not (Directory.Exists worldDir) then []
        else
            Directory.GetFiles(worldDir, "*.md", SearchOption.AllDirectories)
            |> Array.map (fun full ->
                let rel = Path.GetRelativePath(worldDir, full).Replace('\\', '/')
                rel, File.ReadAllText full)
            |> Array.toList

    /// Seed `_base`, then overlay the named world (named files win on path collision).
    let load (datasetRoot: string) (world: string) : IVault =
        let worldsRoot = Path.Combine(datasetRoot, "_worlds")
        let files = Dictionary<string, string>()
        for (path, content) in readWorldDir (Path.Combine(worldsRoot, "_base")) do
            files.[path] <- content
        if not (System.String.IsNullOrWhiteSpace world) && world <> "_base" then
            for (path, content) in readWorldDir (Path.Combine(worldsRoot, world)) do
                files.[path] <- content
        InMemoryVault(files) :> IVault
