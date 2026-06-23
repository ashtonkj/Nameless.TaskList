module Nameless.TaskList.Core.Tests.VaultTests

open System.IO
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Xunit

let private tempRoot () =
    let dir = Path.Combine(Path.GetTempPath(), "ntl-vault-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

[<Fact>]
let ``write then read round-trips and creates parent dirs`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("tasks/pending/x.md", "hello")
        Assert.True(vault.Exists("tasks/pending/x.md"))
        Assert.Equal("hello", vault.Read("tasks/pending/x.md"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``ListFiles returns files directly under a directory`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("contexts/family.md", "a")
        vault.Write("contexts/medical.md", "b")
        let files = vault.ListFiles("contexts") |> List.sort
        Assert.Equal<string list>([ "contexts/family.md"; "contexts/medical.md" ], files)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``Exists is false for missing files`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        Assert.False(vault.Exists("nope.md"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``ListFilesRecursive returns files from nested directories`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        vault.Write("events/2026/06/a.md", "x")
        vault.Write("events/2026/07/b.md", "y")
        vault.Write("events/index.md", "i")
        let files = vault.ListFilesRecursive("events") |> List.sort
        Assert.Equal<string list>([ "events/2026/06/a.md"; "events/2026/07/b.md"; "events/index.md" ], files)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``ListFilesRecursive returns empty for a missing directory`` () =
    let root = tempRoot ()
    try
        let vault = FileSystemVault(root) :> IVault
        Assert.Empty(vault.ListFilesRecursive("nope"))
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``FakeVault ListFilesRecursive returns nested keys under the prefix`` () =
    let vault = Nameless.TaskList.Core.Tests.Fakes.FakeVault()
    vault.Seed("people/medical/a.md", "x")
    vault.Seed("people/school/b.md", "y")
    vault.Seed("tasks/pending/c.md", "z")
    let files = (vault :> IVault).ListFilesRecursive("people") |> List.sort
    Assert.Equal<string list>([ "people/medical/a.md"; "people/school/b.md" ], files)
