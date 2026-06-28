namespace Nameless.TaskList.Core

open System
open System.Text.RegularExpressions

module Email =

    /// Crude HTML→text: drop tags, collapse whitespace, decode the few common entities.
    let private htmlToText (html: string) : string =
        if String.IsNullOrWhiteSpace html then ""
        else
            let noTags = Regex.Replace(html, "<[^>]+>", " ")
            let decoded =
                noTags.Replace("&nbsp;", " ").Replace("&amp;", "&")
                      .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            Regex.Replace(decoded, @"[ \t]+", " ").Trim()

    /// Strip quoted reply chains and signature blocks from a plain-text body.
    let private stripQuotedAndSignature (text: string) : string =
        if String.IsNullOrWhiteSpace text then ""
        else
            let lines = text.Replace("\r\n", "\n").Split('\n')
            // Cut at the first "On ... wrote:" attribution line (start of a reply chain).
            let attribution = Regex(@"^On .+ wrote:\s*$", RegexOptions.IgnoreCase)
            // Cut at a signature delimiter line ("-- ").
            let isSig (l: string) = l.TrimEnd() = "--" || l = "-- "
            let kept = ResizeArray<string>()
            let mutable stop = false
            for l in lines do
                if not stop then
                    if attribution.IsMatch l || isSig l then stop <- true
                    elif (l.TrimStart().StartsWith ">") then ()   // drop quoted lines
                    else kept.Add l
            String.Join("\n", kept).Trim()

    /// Subject + body as the message content. The subject often carries the actionable
    /// signal in email, so it must reach the classifier; blank subject falls back to body only.
    let private withSubject (subject: string) (body: string) : string =
        if String.IsNullOrWhiteSpace subject then body
        elif String.IsNullOrWhiteSpace body then subject.Trim()
        else sprintf "%s\n\n%s" (subject.Trim()) body

    /// The clean message body: prefer text/plain, fall back to stripped HTML.
    let extractText (email: RawEmail) : string =
        let primary =
            if not (String.IsNullOrWhiteSpace email.TextBody) then email.TextBody
            else htmlToText email.HtmlBody
        stripQuotedAndSignature primary

    /// One-to-many / list mail — routed to log-only, like a WhatsApp newsletter.
    let isBulk (email: RawEmail) : bool =
        email.ListUnsubscribe
        || (match (if isNull email.Precedence then "" else email.Precedence).Trim().ToLowerInvariant() with
            | "bulk" | "list" | "junk" -> true
            | _ -> false)

    /// Map a fetched email onto the pipeline's ChatMessage shape.
    let toChatMessage (utcOffset: TimeSpan) (email: RawEmail) : ChatMessage =
        let display =
            if String.IsNullOrWhiteSpace email.FromDisplay then email.FromAddress else email.FromDisplay
        { Id = email.MessageId
          ChatJid = email.FromAddress
          ChatName = display
          NormalizedChatName = display
          IsGroup = false
          SenderId = email.FromAddress
          SenderName = display
          SenderPushName = null
          SenderSavedName = null
          SenderBusinessName = null
          IsFromMe = false
          Platform = "email"
          IsBroadcast = isBulk email
          Content = withSubject email.Subject (extractText email)
          MediaType = null
          FileName = null
          AlbumId = null
          AlbumIndex = None
          Timestamp = email.Date.ToOffset(utcOffset).DateTime }

open Nameless.TaskList.Core.Ports

module EmailPoller =

    /// Fetch new mail since the stored cursor, mapped to ChatMessages, and the cursor to
    /// persist once they are processed. The cursor is returned, NOT saved, so the caller can
    /// persist it only after processing succeeds (a crash before save re-processes idempotently).
    ///
    /// On the first run (uninitialised cursor) or a UIDVALIDITY change, the cursor is seeded
    /// FORWARD rather than scanning the whole mailbox: only the newest `initialBackfill` messages
    /// are processed (0 = go-forward only). This avoids flooding the pipeline on first enable.
    let fetch (mailbox: IMailbox) (stored: EmailCursor) (folder: string) (initialBackfill: uint32) (utcOffset: System.TimeSpan)
        : ChatMessage list * EmailCursor =
        let validity = mailbox.UidValidity folder
        let resume = stored.Initialized && validity = stored.UidValidity
        let since =
            if resume then stored.LastUid
            else
                let highest = mailbox.HighestUid folder
                if highest > initialBackfill then highest - initialBackfill else 0u
        let raws = mailbox.FetchSince(folder, since)
        let highest = raws |> List.fold (fun acc r -> max acc r.Uid) since
        let mails = raws |> List.map (Email.toChatMessage utcOffset)
        mails, { UidValidity = validity; LastUid = highest; Initialized = true }
