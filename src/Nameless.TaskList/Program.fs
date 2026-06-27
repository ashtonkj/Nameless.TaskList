namespace Nameless.TaskList
#nowarn "20"

open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
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
            let numCtx = match System.Int32.TryParse(cfg.["Ollama:NumCtx"]) with | true, n -> n | _ -> 0
            let temperature =
                match System.Double.TryParse(cfg.["Ollama:Temperature"], System.Globalization.CultureInfo.InvariantCulture) with
                | true, t -> t
                | _ -> -1.0
            OllamaChatClient(http, cfg.["Ollama:Url"], cfg.["Ollama:Model"], numCtx, temperature) :> IChatClient) |> ignore
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

        let buildDeps (messages: IMessageSource) (vault: IVault) (chat: IChatClient)
                      (embedder: IEmbedder) (vision: IVision) (transcriber: ITranscriber) : PipelineDeps =
            let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
            let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let noteTopK = match System.Int32.TryParse(cfg.["NoteMatch:TopK"]) with | true, v -> v | _ -> 5
            let noteFloor = match System.Double.TryParse(cfg.["NoteMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let taskTopK = match System.Int32.TryParse(cfg.["TaskMatch:TopK"]) with | true, v -> v | _ -> 5
            let taskFloor = match System.Double.TryParse(cfg.["TaskMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            let peopleTopK = match System.Int32.TryParse(cfg.["PeopleMatch:TopK"]) with | true, v -> v | _ -> 5
            let peopleFloor = match System.Double.TryParse(cfg.["PeopleMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.35
            { Messages = messages; Vault = vault; Chat = chat; Model = cfg.["Ollama:Model"]
              Embedder = embedder; TopK = topK; SimilarityFloor = floor
              NoteTopK = noteTopK; NoteSimilarityFloor = noteFloor
              TaskTopK = taskTopK; TaskSimilarityFloor = taskFloor
              PeopleTopK = peopleTopK; PeopleSimilarityFloor = peopleFloor
              Vision = vision; Transcriber = transcriber }

        // Email channel: register the IMAP poller only when enabled (off by default + in tests).
        if cfg.["Imap:Enabled"] = "true" then
            builder.Services.AddHostedService<ImapPollerService>(fun sp ->
                let port = match System.Int32.TryParse(cfg.["Imap:Port"]) with | true, n -> n | _ -> 993
                let useSsl = cfg.["Imap:UseSsl"] <> "false"
                let folder = if System.String.IsNullOrWhiteSpace cfg.["Imap:Folder"] then "INBOX" else cfg.["Imap:Folder"]
                let pollSeconds = match System.Int32.TryParse(cfg.["Imap:PollSeconds"]) with | true, n -> n | _ -> 120
                // Newest N messages to process on first enable / UIDVALIDITY reset (0 = go-forward only).
                let initialBackfill = match System.UInt32.TryParse(cfg.["Imap:InitialBackfill"]) with | true, n -> n | _ -> 0u
                let mailbox =
                    MailKitMailbox(cfg.["Imap:Host"], port, useSsl, cfg.["Imap:User"], cfg.["Imap:Password"]) :> IMailbox
                let cursorPath = System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "email-cursor.json")
                let cursorStore = FileSystemEmailCursorStore(cursorPath) :> IEmailCursorStore
                let source = ImapMessageSource()
                let buildEmailDeps (s: ImapMessageSource) =
                    buildDeps
                        (s :> IMessageSource)
                        (sp.GetRequiredService<IVault>())
                        (sp.GetRequiredService<IChatClient>())
                        (sp.GetRequiredService<IEmbedder>())
                        (sp.GetRequiredService<IVision>())
                        (sp.GetRequiredService<ITranscriber>())
                let logger = sp.GetRequiredService<ILogger<ImapPollerService>>()
                new ImapPollerService(mailbox, cursorStore, source, buildEmailDeps, folder, pollSeconds, initialBackfill, logger)) |> ignore

        // WhatsApp channel: register the LISTEN/NOTIFY listener only when enabled.
        if cfg.["WhatsApp:Listen:Enabled"] = "true" then
            builder.Services.AddHostedService<WhatsAppListenerService>(fun sp ->
                let connStr = cfg.GetConnectionString("WhatsApp")
                let listener = new NpgsqlNotificationListener(connStr) :> INotificationListener
                let cursorPath = System.IO.Path.Combine(cfg.["Vault:Root"], ".taskmeister", "whatsapp-listen-cursor.json")
                let cursorStore = new FileSystemListenCursorStore(cursorPath) :> IListenCursorStore
                let messages = sp.GetRequiredService<IMessageSource>()
                let buildListenerDeps () =
                    buildDeps
                        messages
                        (sp.GetRequiredService<IVault>())
                        (sp.GetRequiredService<IChatClient>())
                        (sp.GetRequiredService<IEmbedder>())
                        (sp.GetRequiredService<IVision>())
                        (sp.GetRequiredService<ITranscriber>())
                let reconnectSeconds = match System.Int32.TryParse(cfg.["WhatsApp:Listen:ReconnectSeconds"]) with | true, n -> n | _ -> 10
                let logger = sp.GetRequiredService<ILogger<WhatsAppListenerService>>()
                new WhatsAppListenerService(listener, cursorStore, messages, buildListenerDeps, "whatsapp_new_message", reconnectSeconds, logger)) |> ignore

        let app = builder.Build()

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
                let topicCfg : Indexer.TopicSweepConfig =
                    { ResolvedArchiveAfterDays = (match System.Int32.TryParse(cfg.["Topics:ResolvedArchiveAfterDays"]) with | true, n -> n | _ -> 14)
                      DormantArchiveAfterDays = (match System.Int32.TryParse(cfg.["Topics:DormantArchiveAfterDays"]) with | true, n -> n | _ -> 90) }
                try Indexer.regenerate vault topicCfg System.DateTime.Now |> ReindexHandler.toHttp
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore

        app.MapGet("/relationships", System.Func<IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (vault: IVault) ->
                try RelationshipsHandler.allToHttp vault
                with ex -> Results.Json({| error = ex.Message |}, statusCode = 500))) |> ignore

        app.MapGet("/relationships/{slug}", System.Func<string, IVault, Microsoft.AspNetCore.Http.IResult>(
            fun (slug: string) (vault: IVault) ->
                try RelationshipsHandler.forPersonToHttp vault slug
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
