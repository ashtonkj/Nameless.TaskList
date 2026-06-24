namespace Nameless.TaskList
#nowarn "20"

open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline
open Nameless.TaskList.Core.BulkProcessor
open Nameless.TaskList.Core

module Program =

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        let cfg = builder.Configuration

        // Adapters as singletons behind their ports.
        builder.Services.AddSingleton<IVault>(fun _ ->
            FileSystemVault(cfg.["Vault:Root"]) :> IVault) |> ignore
        builder.Services.AddSingleton<IMessageSource>(fun _ ->
            PostgresMessageSource(cfg.GetConnectionString("WhatsApp")) :> IMessageSource) |> ignore
        builder.Services.AddSingleton<HttpClient>(fun _ -> new HttpClient()) |> ignore
        builder.Services.AddSingleton<IChatClient>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            OllamaChatClient(http, cfg.["Ollama:Url"], cfg.["Ollama:Model"]) :> IChatClient) |> ignore
        builder.Services.AddSingleton<IEmbedder>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let embedModel = if isNull cfg.["Ollama:EmbedModel"] then "nomic-embed-text" else cfg.["Ollama:EmbedModel"]
            OllamaEmbedder(http, cfg.["Ollama:Url"], embedModel) :> IEmbedder) |> ignore
        builder.Services.AddSingleton<IVision>(fun sp ->
            let http = sp.GetRequiredService<HttpClient>()
            let visionModel = if isNull cfg.["Ollama:VisionModel"] then "gemma3:latest" else cfg.["Ollama:VisionModel"]
            OllamaVision(http, cfg.["Ollama:Url"], visionModel) :> IVision) |> ignore
        builder.Services.AddSingleton<ITranscriber>(fun _ ->
            let command = if isNull cfg.["Whisper:Command"] then "whisper" else cfg.["Whisper:Command"]
            let model = if isNull cfg.["Whisper:Model"] then "base" else cfg.["Whisper:Model"]
            let language = if isNull cfg.["Whisper:Language"] then "" else cfg.["Whisper:Language"]
            let timeout = match System.Int32.TryParse(cfg.["Whisper:TimeoutSeconds"]) with | true, v -> v | _ -> 300
            WhisperTranscriber(command, model, language, timeout) :> ITranscriber) |> ignore
        builder.Services.AddSingleton<IJobStore>(fun _ ->
            let configured = cfg.["BulkJobs:StatePath"]
            let path =
                if System.String.IsNullOrWhiteSpace configured
                then System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "bulk-jobs.json")
                else configured
            FileSystemJobStore(path) :> IJobStore) |> ignore
        builder.Services.AddSingleton<BulkJobs.BulkJobRegistry>(fun sp ->
            let store = sp.GetRequiredService<IJobStore>()
            let retain = match System.Int32.TryParse(cfg.["BulkJobs:Retain"]) with | true, v -> v | _ -> 20
            BulkJobs.BulkJobRegistry(store, retain)) |> ignore

        let app = builder.Build()

        let buildDeps (messages: IMessageSource) (vault: IVault) (chat: IChatClient)
                      (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) : PipelineDeps =
            let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
            let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let noteTopK = match System.Int32.TryParse(cfg.["NoteMatch:TopK"]) with | true, v -> v | _ -> 5
            let noteFloor = match System.Double.TryParse(cfg.["NoteMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            { Messages = messages; Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
              Embedder = embedder; TopK = topK; SimilarityFloor = floor
              NoteTopK = noteTopK; NoteSimilarityFloor = noteFloor
              Vision = vision; Transcriber = transcriber }

        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, ITranscriber, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) ->
                try
                    let deps = buildDeps messages vault chat embedder vision transcriber
                    processMessage deps req.Id req.ChatJid
                    |> ProcessMessageHandler.toHttp
                with ex ->
                    Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore

        app.MapPost("/reindex", System.Func<IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) ->
                try Indexer.regenerate vault |> ReindexHandler.toHttp
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore

        let runDigest (vault: IVault) (chat: IChatClient) (p: Digest.DigestParams) : Microsoft.AspNetCore.Http.IResult =
            try
                let deps : Digest.DigestDeps =
                    { Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]; Today = System.DateTime.Now }
                Digest.generate deps p |> DigestHandler.toHttp
            with ex -> Results.Json({| error = ex.Message |}, statusCode = 500)

        app.MapPost("/digest/daily", System.Func<IVault, IChatClient, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) (chat: IChatClient) -> runDigest vault chat Digest.DigestParams.daily)) |> ignore

        app.MapPost("/digest/weekly", System.Func<IVault, IChatClient, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) (chat: IChatClient) -> runDigest vault chat Digest.DigestParams.weekly)) |> ignore

        app.MapPost("/messages/process-since", System.Func<ProcessSinceRequest, IMessageSource, IVault, IChatClient, IEmbedder, IVision, ITranscriber, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessSinceRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) (registry: BulkJobs.BulkJobRegistry) ->
                match System.DateTime.TryParse(req.Since) with
                | false, _ -> Results.Json({| error = "invalid or missing 'since' date" |}, statusCode = 400)
                | true, since ->
                    let deps = buildDeps messages vault chat embedder vision transcriber
                    let processOne id jid = processMessage deps id jid
                    let chatJid = if System.String.IsNullOrWhiteSpace req.ChatJid then None else Some req.ChatJid
                    registry.TryStart messages processOne since chatJid |> BulkHandler.startToHttp)) |> ignore

        app.MapGet("/messages/process-since/{jobId}", System.Func<string, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (jobId: string) (registry: BulkJobs.BulkJobRegistry) -> registry.Get jobId |> BulkHandler.progressToHttp)) |> ignore

        app.MapPost("/messages/process-since/{jobId}/cancel", System.Func<string, BulkJobs.BulkJobRegistry, Microsoft.AspNetCore.Http.IResult>(
            fun (jobId: string) (registry: BulkJobs.BulkJobRegistry) -> registry.Cancel jobId |> BulkHandler.cancelToHttp)) |> ignore

        // Resume any job left "running" by a previous host (interrupted by restart).
        let registry = app.Services.GetRequiredService<BulkJobs.BulkJobRegistry>()
        let resumeDeps =
            buildDeps
                (app.Services.GetRequiredService<IMessageSource>())
                (app.Services.GetRequiredService<IVault>())
                (app.Services.GetRequiredService<IChatClient>())
                (app.Services.GetRequiredService<IEmbedder>())
                (app.Services.GetRequiredService<IVision>())
                (app.Services.GetRequiredService<ITranscriber>())
        registry.Resume resumeDeps.Messages (fun id jid -> processMessage resumeDeps id jid)

        app.Run()
        0
