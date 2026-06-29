module Nameless.TaskList.Core.Tests.PromptsTests

open System
open Nameless.TaskList.Core
open Xunit

// Minimal ChatMessage builder for history rendering (only Content/MediaType/SenderName matter).
let private histMsg (sender: string) (content: string) (mediaType: string) : ChatMessage =
    { Id = "x"; ChatJid = "jid"; ChatName = "c"; NormalizedChatName = "c"; IsGroup = false
      SenderId = "s"; SenderName = sender; SenderPushName = null; SenderSavedName = null
      SenderBusinessName = null; IsFromMe = false; Platform = "whatsapp-direct"; IsBroadcast = false
      Content = content; MediaType = mediaType
      FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = DateTime(2026, 6, 24) }

[<Fact>]
let ``renderHistory of empty list is empty string`` () =
    Assert.Equal("", Prompts.renderHistory [])

[<Fact>]
let ``renderHistory reverses newest-first to oldest-first transcript`` () =
    // GetRecent returns newest-first; the transcript must read oldest->newest.
    let recent = [ histMsg "Me" "newest" null; histMsg "Wife" "oldest" null ]
    Assert.Equal("Wife: oldest\nMe: newest", Prompts.renderHistory recent)

[<Fact>]
let ``renderHistory renders media-only turns as placeholders`` () =
    let recent =
        [ histMsg "Wife" "" "document"
          histMsg "Wife" "" "video"
          histMsg "Wife" "" "audio"
          histMsg "Wife" "" "image" ]
    // reversed: image, audio, video, document
    Assert.Equal("Wife: [image]\nWife: [voice note]\nWife: [video]\nWife: [document]",
                 Prompts.renderHistory recent)

[<Fact>]
let ``renderHistory renders empty content with unknown media as no-text`` () =
    Assert.Equal("Wife: [no text]", Prompts.renderHistory [ histMsg "Wife" "" null ])

[<Fact>]
let ``classifyUser with empty history returns content verbatim`` () =
    Assert.Equal("hello there", Prompts.classifyUser "" "hello there")

[<Fact>]
let ``classifyUser with whitespace history returns content verbatim`` () =
    Assert.Equal("hello there", Prompts.classifyUser "   " "hello there")

[<Fact>]
let ``classifyUser with history wraps content with markers`` () =
    let payload = Prompts.classifyUser "Wife: oldest\nMe: newest" "yes the 19th works"
    Assert.Contains("Wife: oldest", payload)
    Assert.Contains("Me: newest", payload)
    Assert.Contains("Message to classify:", payload)
    Assert.Contains("yes the 19th works", payload)

[<Fact>]
let ``topicUpdateUser with empty history omits the Recent conversation heading`` () =
    let result = Prompts.topicUpdateUser "" "BODY" "MSG" "INTENT"
    Assert.DoesNotContain("Recent conversation", result)
    Assert.Equal("Current topic body:\nBODY\n\nNew message content:\nMSG\n\nExtracted intent:\nINTENT", result)

[<Fact>]
let ``topicUpdateUser with history includes the conversation section`` () =
    let result = Prompts.topicUpdateUser "Wife: hi" "BODY" "MSG" "INTENT"
    Assert.Contains("Recent conversation (oldest to newest, for context):", result)
    Assert.Contains("Wife: hi", result)
    Assert.Contains("BODY", result)
    Assert.Contains("MSG", result)
    Assert.Contains("INTENT", result)

[<Fact>]
let ``renderHistory renders null sender as Unknown`` () =
    Assert.Equal("Unknown: hello", Prompts.renderHistory [ histMsg null "hello" null ])
