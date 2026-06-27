module Nameless.TaskList.IntegrationTests.ImapTests

open System
open Xunit
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters

// Reads IMAP config from environment; skips when not configured.
let private env k = Environment.GetEnvironmentVariable k

[<SkippableFact>]
let ``MailKitMailbox connects and reports a uidvalidity`` () =
    let host = env "IMAP_HOST"
    let user = env "IMAP_USER"
    let password = env "IMAP_PASSWORD"
    Skip.If(String.IsNullOrWhiteSpace host || String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace password,
            "IMAP_HOST/IMAP_USER/IMAP_PASSWORD not set")
    let port = match Int32.TryParse(env "IMAP_PORT") with | true, n -> n | _ -> 993
    let mailbox = MailKitMailbox(host, port, true, user, password) :> IMailbox
    let validity = mailbox.UidValidity "INBOX"
    Assert.True(validity > 0u)

[<SkippableFact>]
let ``MailKitMailbox fetches recent mail and maps it`` () =
    let host = env "IMAP_HOST"
    let user = env "IMAP_USER"
    let password = env "IMAP_PASSWORD"
    Skip.If(String.IsNullOrWhiteSpace host || String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace password,
            "IMAP_HOST/IMAP_USER/IMAP_PASSWORD not set")
    let port = match Int32.TryParse(env "IMAP_PORT") with | true, n -> n | _ -> 993
    let mailbox = MailKitMailbox(host, port, true, user, password) :> IMailbox
    // Fetch from 0 = everything; just assert it returns without error and the shape is sane.
    let mails = mailbox.FetchSince("INBOX", 0u)
    mails |> List.truncate 1 |> List.iter (fun m -> Assert.False(String.IsNullOrWhiteSpace m.FromAddress))
