module Nameless.TaskList.Core.Tests.JobStoreTests

open System
open System.IO
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.BulkProcessor
open Xunit

let private job (id: string) : BulkJob =
    { JobId = id; Since = DateTime(2026, 6, 1); ChatJid = "chat@x"; StartedAt = DateTime(2026, 6, 2)
      Status = "done"; Total = 5; Processed = 3; Noise = 1; Skipped = 1; Errors = 0; Error = "" }

[<Fact>]
let ``FileSystemJobStore round-trips jobs through a JSON file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "bulk-jobs.json")
    let store = FileSystemJobStore(path) :> IJobStore
    let jobs = [ job "a"; job "b" ]
    store.Save jobs
    let loaded = store.Load()
    Assert.Equal(2, List.length loaded)
    let a = loaded |> List.find (fun j -> j.JobId = "a")
    Assert.Equal("chat@x", a.ChatJid)
    Assert.Equal("done", a.Status)
    Assert.Equal(3, a.Processed)
    Assert.Equal(DateTime(2026, 6, 1), a.Since)

[<Fact>]
let ``FileSystemJobStore Load returns empty when the file is missing`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "nope.json")
    let store = FileSystemJobStore(path) :> IJobStore
    Assert.Empty(store.Load())

[<Fact>]
let ``FileSystemJobStore Load returns empty on a corrupt file`` () =
    let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
    Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
    File.WriteAllText(path, "{ not json")
    let store = FileSystemJobStore(path) :> IJobStore
    Assert.Empty(store.Load())
