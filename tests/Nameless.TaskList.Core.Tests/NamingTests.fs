module Nameless.TaskList.Core.Tests.NamingTests

open System
open Nameless.TaskList.Core.KnowledgeBase
open Xunit

[<Fact>]
let ``slug lowercases and hyphenates`` () =
    Assert.Equal("book-ethans-flu-vaccine", Naming.slug "Book Ethan's flu vaccine")

[<Fact>]
let ``slug collapses non-alphanumerics and trims hyphens`` () =
    Assert.Equal("dr-naidoo", Naming.slug "  Dr. Naidoo!! ")

[<Fact>]
let ``messageFileName uses ISO date with hyphenated time`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("2026-06-15T14-17-45.md", Naming.messageFileName ts)

[<Fact>]
let ``messagePath nests under channel slug`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("messages/wife-direct/2026-06-15T14-17-45.md", Naming.messagePath "wife-direct" ts)

[<Fact>]
let ``taskPath slugs the title under pending`` () =
    Assert.Equal("tasks/pending/book-ethans-flu-vaccine.md", Naming.taskPath "Book Ethan's flu vaccine")

[<Fact>]
let ``topicPath nests under active`` () =
    Assert.Equal("topics/active/ethan-birthday-party-2026.md", Naming.topicPath "ethan-birthday-party-2026")

[<Fact>]
let ``eventPath is date-pathed by year and month`` () =
    let w = DateTime(2026, 7, 19, 14, 0, 0, DateTimeKind.Utc)
    Assert.Equal("events/2026/07/ethans-birthday-party-2026-07-19.md", Naming.eventPath w "Ethan's birthday party")

[<Fact>]
let ``commitmentPath slugs under commitments`` () =
    Assert.Equal("commitments/pay-school-fees.md", Naming.commitmentPath "Pay school fees")

[<Fact>]
let ``notePath slugs under notes`` () =
    Assert.Equal("notes/ethan-allergies.md", Naming.notePath "Ethan allergies")

[<Fact>]
let ``personPath nests under people and context`` () =
    Assert.Equal("people/medical/dr-naidoo.md", Naming.personPath "medical" "dr-naidoo")

[<Fact>]
let ``digestPath is dated and kinded`` () =
    let d = System.DateTime(2026, 6, 23)
    Assert.Equal("digests/2026-06-23-daily.md", Naming.digestPath d "daily")
    Assert.Equal("digests/2026-06-23-weekly.md", Naming.digestPath d "weekly")

[<Fact>]
let ``relationshipPath orders slugs alphabetically`` () =
    Assert.Equal("relationships/dr-naidoo-ethan.md", Naming.relationshipPath "dr-naidoo" "ethan")

[<Fact>]
let ``relationshipPath is order-independent`` () =
    Assert.Equal(Naming.relationshipPath "ethan" "dr-naidoo", Naming.relationshipPath "dr-naidoo" "ethan")

[<Fact>]
let ``channelPathFor email nests under channels-email`` () =
    Assert.Equal("channels/email/dr-naidoo-practice.md", Naming.channelPathFor "email" "dr-naidoo-practice")

[<Fact>]
let ``channelPathFor whatsapp keeps the whatsapp dir`` () =
    Assert.Equal("channels/whatsapp/wife.md", Naming.channelPathFor "whatsapp-direct" "wife")

[<Fact>]
let ``messagePathFor whatsapp matches the legacy path`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal("messages/wife/2026-06-15T14-17-45.md", Naming.messagePathFor "whatsapp-direct" "wife" ts "ignored")

[<Fact>]
let ``messagePathFor email namespaces the folder and hashes the message id`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    let p = Naming.messagePathFor "email" "dr-naidoo-practice" ts "<abc@mail>"
    Assert.StartsWith("messages/email-dr-naidoo-practice/2026-06-15T14-17-45-", p)
    Assert.EndsWith(".md", p)

[<Fact>]
let ``messagePathFor email is stable for the same message id`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.Equal<string>(
        Naming.messagePathFor "email" "c" ts "<id-1@mail>",
        Naming.messagePathFor "email" "c" ts "<id-1@mail>")

[<Fact>]
let ``messagePathFor email differs for different message ids in the same second`` () =
    let ts = DateTime(2026, 6, 15, 14, 17, 45, DateTimeKind.Utc)
    Assert.NotEqual<string>(
        Naming.messagePathFor "email" "c" ts "<id-1@mail>",
        Naming.messagePathFor "email" "c" ts "<id-2@mail>")
