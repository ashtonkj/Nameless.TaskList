namespace Nameless.TaskList.Core

open System
open System.Text.RegularExpressions

module Email =

    // KB timestamps are SAST wall-clock (+02:00) per DESIGN §4/§8.
    let private sastOffset = TimeSpan.FromHours 2.0

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
    let toChatMessage (email: RawEmail) : ChatMessage =
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
          Content = extractText email
          MediaType = null
          FileName = null
          AlbumId = null
          AlbumIndex = None
          Timestamp = email.Date.ToOffset(sastOffset).DateTime }
