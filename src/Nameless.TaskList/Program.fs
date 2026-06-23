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

        let app = builder.Build()

        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, IEmbedder, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) (embedder: IEmbedder) ->
                try
                    let topK = match System.Int32.TryParse(cfg.["TopicMatch:TopK"]) with | true, v -> v | _ -> 5
                    let floor = match System.Double.TryParse(cfg.["TopicMatch:SimilarityFloor"]) with | true, v -> v | _ -> 0.5
                    let deps =
                        { Messages = messages; Vault = vault; Chat = chat
                          Model = cfg.["Ollama:Model"]; Embedder = embedder; TopK = topK; SimilarityFloor = floor }
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

        app.Run()
        0
