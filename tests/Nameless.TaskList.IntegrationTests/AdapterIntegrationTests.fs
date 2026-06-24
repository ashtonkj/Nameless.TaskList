module Nameless.TaskList.IntegrationTests.AdapterIntegrationTests

open System
open System.Net.Http
open Xunit
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.IntegrationTests.Support

// No live service needed — proves the harness executes under -p:Integration=true.
[<Fact>]
let ``FileSystemVault round-trips write, read, exists and list`` () =
    Helpers.withTempVault (fun root ->
        let vault = FileSystemVault(root) :> IVault
        vault.Write("topics/active/sample.md", "hello body")
        Assert.True(vault.Exists "topics/active/sample.md")
        Assert.Equal("hello body", vault.Read "topics/active/sample.md")
        Assert.Contains("topics/active/sample.md", vault.ListFilesRecursive "topics"))

[<SkippableFact>]
let ``Postgres returns at least one well-formed message`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not reachable")
    let rows = (Helpers.messages ()).GetMessagesSince(None, DateTime.MinValue)
    Assert.NotEmpty(rows)
    let first = List.head rows
    Assert.False(String.IsNullOrWhiteSpace first.Id)
    Assert.False(String.IsNullOrWhiteSpace first.ChatJid)

[<SkippableFact>]
let ``Ollama chat returns a non-empty reply`` () =
    Skip.IfNot(ServiceProbes.ollama.Value, "Ollama not reachable")
    use http = new HttpClient()
    let chat = OllamaChatClient(http, Config.ollamaUrl, Config.chatModel) :> IChatClient
    let reply = Agent.runConversation chat [] "You are a test." "Reply with the single word OK."
    Assert.False(String.IsNullOrWhiteSpace reply)

[<SkippableFact>]
let ``Ollama embed returns a 768-dim finite vector`` () =
    Skip.IfNot(ServiceProbes.ollama.Value, "Ollama not reachable")
    use http = new HttpClient()
    let embedder = OllamaEmbedder(http, Config.ollamaUrl, Config.embedModel) :> IEmbedder
    let v = embedder.Embed "integration test sentence"
    Assert.Equal(768, v.Length)
    Assert.All(v, fun x -> Assert.True(Double.IsFinite x))

[<SkippableFact>]
let ``Ollama vision describes a real image message`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not reachable")
    Skip.IfNot(ServiceProbes.ollama.Value, "Ollama not reachable")
    match Helpers.firstWithMedia "image" with
    | None -> Skip.If(true, "no image message with stored bytes")
    | Some(_, bytes) ->
        use http = new HttpClient()
        let vision = OllamaVision(http, Config.ollamaUrl, Config.visionModel) :> IVision
        let text = vision.Describe bytes
        Assert.False(String.IsNullOrWhiteSpace text)

[<SkippableFact>]
let ``Whisper transcribes a real audio message`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not reachable")
    Skip.IfNot(ServiceProbes.whisper.Value, "whisper/ffmpeg not available")
    match Helpers.firstWithMedia "audio" with
    | None -> Skip.If(true, "no audio message with stored bytes")
    | Some(_, bytes) ->
        let t =
            WhisperTranscriber(Config.whisperCommand, Config.whisperModel, Config.whisperLanguage, Config.whisperTimeout)
            :> ITranscriber
        let text = t.Transcribe bytes
        Assert.False(String.IsNullOrWhiteSpace text)
