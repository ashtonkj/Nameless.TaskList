module Nameless.TaskList.IntegrationTests.PipelineIntegrationTests

open System
open System.IO
open System.Net.Http
open Xunit
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.IntegrationTests.Support

[<SkippableFact>]
let ``end-to-end processMessage against live services writes a message file`` () =
    Skip.IfNot(ServiceProbes.postgres.Value, "Postgres not reachable")
    Skip.IfNot(ServiceProbes.ollama.Value, "Ollama not reachable")
    match Helpers.firstMessageWith (fun _ -> true) with
    | None -> Skip.If(true, "no messages in the store")
    | Some msg ->
        Helpers.withTempVault (fun root ->
            use http = new HttpClient()
            let deps =
                { Messages = Helpers.messages ()
                  Vault = FileSystemVault(root) :> IVault
                  Chat = OllamaChatClient(http, Config.ollamaUrl, Config.chatModel) :> IChatClient
                  Model = Config.chatModel
                  Embedder = OllamaEmbedder(http, Config.ollamaUrl, Config.embedModel) :> IEmbedder
                  TopK = 5
                  SimilarityFloor = 0.5
                  NoteTopK = 5
                  NoteSimilarityFloor = 0.35
                  Vision = OllamaVision(http, Config.ollamaUrl, Config.visionModel) :> IVision
                  Transcriber =
                    WhisperTranscriber(Config.whisperCommand, Config.whisperModel, Config.whisperLanguage, Config.whisperTimeout)
                    :> ITranscriber }
            match processMessage deps msg.Id msg.ChatJid with
            | LlmError e -> failwithf "pipeline returned LlmError: %s" e
            | NotFound -> failwith "pipeline returned NotFound for a real message id"
            | _ -> ()
            let wroteMessageFile =
                Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
                |> Array.exists (fun p -> p.Replace('\\', '/').Contains("/messages/"))
            Assert.True(wroteMessageFile, "expected a messages/ file to be written"))
