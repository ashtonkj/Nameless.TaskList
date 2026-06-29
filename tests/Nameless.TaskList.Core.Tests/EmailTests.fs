module Nameless.TaskList.Core.Tests.EmailTests

open System
open Nameless.TaskList.Core
open Xunit

let raw () : RawEmail =
    { MessageId = "<m1@example.com>"; FromAddress = "practice@example.com"
      FromDisplay = "Dr Naidoo Practice"
      Date = DateTimeOffset(2026, 6, 15, 12, 17, 45, TimeSpan.Zero)  // 12:17 UTC = 14:17 SAST
      Subject = "Flu vaccine reminder"; TextBody = "Please book Ethan's flu vaccine."
      HtmlBody = ""; ListUnsubscribe = false; Precedence = ""; Uid = 42u }

[<Fact>]
let ``extractText prefers the plain-text body`` () =
    let r = { raw () with TextBody = "plain wins"; HtmlBody = "<p>html</p>" }
    Assert.Equal("plain wins", (Email.extractText r).Trim())

[<Fact>]
let ``extractText falls back to stripped html`` () =
    let r = { raw () with TextBody = ""; HtmlBody = "<p>Hello <b>there</b></p>" }
    Assert.Equal("Hello there", (Email.extractText r).Trim())

[<Fact>]
let ``extractText drops quoted reply chains`` () =
    let body = "My answer is yes.\n\nOn Mon, 14 Jun 2026, Dr Naidoo wrote:\n> original question\n> more quote"
    let r = { raw () with TextBody = body }
    let out = Email.extractText r
    Assert.Contains("My answer is yes.", out)
    Assert.DoesNotContain("original question", out)

[<Fact>]
let ``extractText drops a signature block`` () =
    let r = { raw () with TextBody = "See you then.\n-- \nDr Naidoo\nPaediatrician" }
    let out = Email.extractText r
    Assert.Contains("See you then.", out)
    Assert.DoesNotContain("Paediatrician", out)

[<Fact>]
let ``extractText drops a multi-line On … wrote: attribution`` () =
    let body = "My answer is yes.\n\nOn Mon, 14 Jun 2026 at 09:00, Dr Naidoo\n<naidoo@example.com> wrote:\n> original question"
    let r = { raw () with TextBody = body }
    let out = Email.extractText r
    Assert.Contains("My answer is yes.", out)
    Assert.DoesNotContain("original question", out)
    Assert.DoesNotContain("naidoo@example.com", out)   // the wrapped attribution line is gone too

[<Fact>]
let ``extractText does not cut a body line that merely starts with On`` () =
    // No line ends with "wrote:", so nothing is an attribution — both lines stay.
    let r = { raw () with TextBody = "On Friday we met; I wrote the notes.\nLet's confirm Tuesday." }
    let out = Email.extractText r
    Assert.Contains("On Friday we met", out)
    Assert.Contains("confirm Tuesday", out)

[<Fact>]
let ``isBulk is true when list-unsubscribe is present`` () =
    Assert.True(Email.isBulk { raw () with ListUnsubscribe = true })

[<Fact>]
let ``isBulk is true for precedence bulk`` () =
    Assert.True(Email.isBulk { raw () with Precedence = "bulk" })

[<Fact>]
let ``isBulk is true for precedence list`` () =
    Assert.True(Email.isBulk { raw () with Precedence = "list" })

[<Fact>]
let ``isBulk is true for precedence junk`` () =
    Assert.True(Email.isBulk { raw () with Precedence = "junk" })

[<Fact>]
let ``isBulk is false for an ordinary personal mail`` () =
    Assert.False(Email.isBulk (raw ()))

[<Fact>]
let ``toChatMessage maps identity, platform and SAST timestamp`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 2.0) (raw ())
    Assert.Equal("<m1@example.com>", m.Id)
    Assert.Equal("practice@example.com", m.ChatJid)
    Assert.Equal("Dr Naidoo Practice", m.NormalizedChatName)
    Assert.Equal("email", m.Platform)
    Assert.False(m.IsGroup)
    Assert.False(m.IsBroadcast)
    Assert.Contains("Flu vaccine reminder", m.Content)
    Assert.Contains("Please book Ethan's flu vaccine.", m.Content)
    // 12:17 UTC shifted to +02:00 SAST
    Assert.Equal(14, m.Timestamp.Hour)

[<Fact>]
let ``toChatMessage prepends the subject to the body`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 2.0) { raw () with Subject = "Parent meeting"; TextBody = "It is on Friday." }
    Assert.Equal("Parent meeting\n\nIt is on Friday.", m.Content)

[<Fact>]
let ``toChatMessage with a blank subject is body only`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 2.0) { raw () with Subject = "   "; TextBody = "Just the body." }
    Assert.Equal("Just the body.", m.Content.Trim())

[<Fact>]
let ``toChatMessage marks bulk mail as broadcast`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 2.0) { raw () with ListUnsubscribe = true }
    Assert.True(m.IsBroadcast)

[<Fact>]
let ``toChatMessage falls back to the from address when display is blank`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 2.0) { raw () with FromDisplay = "" }
    Assert.Equal("practice@example.com", m.NormalizedChatName)

[<Fact>]
let ``toChatMessage applies the supplied offset to the timestamp`` () =
    let m = Email.toChatMessage (System.TimeSpan.FromHours 5.5) (raw ())
    Assert.Equal(System.DateTime(2026, 6, 15, 17, 47, 45), m.Timestamp)
