namespace Nameless.TaskList
#nowarn "20"

open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Pipeline

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

        let app = builder.Build()

        app.MapPost("/messages/process", System.Func<ProcessMessageRequest, IMessageSource, IVault, IChatClient, Microsoft.AspNetCore.Http.IResult>(
            fun (req: ProcessMessageRequest) (messages: IMessageSource) (vault: IVault) (chat: IChatClient) ->
                let deps =
                    { Messages = messages; Vault = vault; Chat = chat
                      Model = cfg.["Ollama:Model"] }
                processMessage deps req.Id req.ChatJid
                |> ProcessMessageHandler.toHttp)) |> ignore

        app.Run()
        0
