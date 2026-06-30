module Nameless.TaskList.Core.Tests.RefilerTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

// --- decideRefile (pure) ---

[<Fact>]
let ``decideRefile NoChange when target equals current`` () =
    Assert.Equal(Refiler.NoChange, Refiler.decideRefile "medical" (Some "medical") false)

[<Fact>]
let ``decideRefile Refile when target differs and free`` () =
    Assert.Equal(Refiler.Refile "medical", Refiler.decideRefile "family" (Some "medical") false)

[<Fact>]
let ``decideRefile SkipCollision when target occupied`` () =
    Assert.Equal(Refiler.SkipCollision, Refiler.decideRefile "family" (Some "medical") true)

[<Fact>]
let ``decideRefile SkipUnknown when target is None`` () =
    Assert.Equal(Refiler.SkipUnknown, Refiler.decideRefile "family" None false)

// --- run over a FakeVault ---

let private personMd (role: string) (ctx: string) =
    // Triple-quoted so the YAML keeps real newlines (no \n-escaping ambiguity).
    sprintf """---
type: Person
title: Dr Naidoo
role: %s
context:
  - %s
channel: ''
phone: ''
email: ''
tags: []
aliases: []
---
Stub.""" role ctx

[<Fact>]
let ``run moves a misfiled doctor to medical and puts medical first in Context`` () =
    let vault = FakeVault()
    vault.Seed("people/family/dr-naidoo.md", personMd "doctor" "family")
    let chat = FakeChatClient([ Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.False((vault :> IVault).Exists "people/family/dr-naidoo.md")
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    let moved = KnowledgeBase.MarkdownFile.FromString ((vault :> IVault).Read "people/medical/dr-naidoo.md")
    let p = KnowledgeBase.Frontmatter.deserialize<KnowledgeBase.Person> moved.FrontMatter.Value
    Assert.Equal("medical", p.Context.[0])
    Assert.Equal(1, summary.Refiled)
    Assert.Equal(0, summary.Skipped)

[<Fact>]
let ``run leaves an already-correct person untouched`` () =
    let vault = FakeVault()
    vault.Seed("people/medical/dr-naidoo.md", personMd "doctor" "medical")
    let chat = FakeChatClient([ Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    Assert.Equal(0, summary.Refiled)
    Assert.Equal(0, summary.Skipped)

[<Fact>]
let ``run skips when the target folder already holds the slug`` () =
    let vault = FakeVault()
    vault.Seed("people/family/dr-naidoo.md", personMd "doctor" "family")
    vault.Seed("people/medical/dr-naidoo.md", personMd "doctor" "medical")
    let chat = FakeChatClient([ Responses.final "medical"; Responses.final "medical" ])
    let summary = Refiler.run (vault :> IVault) chat
    Assert.True((vault :> IVault).Exists "people/family/dr-naidoo.md")
    Assert.True((vault :> IVault).Exists "people/medical/dr-naidoo.md")
    Assert.Equal(0, summary.Refiled)
    Assert.Equal(1, summary.Skipped)
