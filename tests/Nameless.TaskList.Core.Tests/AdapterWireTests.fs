module Nameless.TaskList.Core.Tests.AdapterWireTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Collections.Generic
open System.Threading
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Adapters
open Xunit

// Regression test for the bug a live run caught: OllamaRequest was declared `private`,
// so System.Text.Json serialized the whole request body to `{}` (only public types'
// members are serialized), sending an empty body that Ollama rejects with
// 400 "missing request body". This drives the REAL OllamaChatClient through an
// in-process listener and asserts the body it actually sends is the full request.
[<Fact>]
let ``OllamaChatClient sends a non-empty request body with model and messages`` () =
    let listener = new HttpListener()
    listener.Prefixes.Add("http://localhost:11691/")
    listener.Start()
    let captured = ref ""
    let worker =
        Thread(fun () ->
            let ctx = listener.GetContext()
            use reader = new StreamReader(ctx.Request.InputStream)
            captured.Value <- reader.ReadToEnd()
            let payload = Text.Encoding.UTF8.GetBytes("""{"model":"m","message":{"role":"assistant","content":"ok"},"done":true}""")
            ctx.Response.StatusCode <- 200
            ctx.Response.OutputStream.Write(payload, 0, payload.Length)
            ctx.Response.OutputStream.Close())
    worker.IsBackground <- true
    worker.Start()

    try
        use http = new HttpClient()
        let client = OllamaChatClient(http, "http://localhost:11691", "test-model") :> Ports.IChatClient
        let messages : Message list = [ SystemMessage { Content = "sys" }; UserMessage { Content = "hi" } ]
        let payload = messages |> List.map (fun m -> m.Value) |> Array.ofList
        let tool : ToolDefinition =
            { Function = { Name = "get_contexts"; Description = "d"
                           Parameters = { Type = "object"; Required = [||]; Properties = Dictionary<string, ToolProperty>() } } }
        client.Chat(payload, [| box tool |]) |> ignore
        worker.Join(TimeSpan.FromSeconds 5.0) |> ignore

        // The whole bug was the body collapsing to "{}". Assert it carries real content.
        Assert.NotEqual<string>("{}", captured.Value.Trim())
        Assert.Contains("\"model\"", captured.Value)
        Assert.Contains("\"messages\"", captured.Value)
        Assert.Contains("test-model", captured.Value)
        Assert.Contains("get_contexts", captured.Value)
    finally
        listener.Stop()

[<Fact>]
let ``OllamaEmbedder posts model+input to /api/embed and parses embeddings[0]`` () =
    let listener = new HttpListener()
    listener.Prefixes.Add("http://localhost:11692/")
    listener.Start()
    let captured = ref ""
    let capturedPath = ref ""
    let worker =
        Thread(fun () ->
            let ctx = listener.GetContext()
            capturedPath.Value <- ctx.Request.Url.AbsolutePath
            use reader = new StreamReader(ctx.Request.InputStream)
            captured.Value <- reader.ReadToEnd()
            let body = Text.Encoding.UTF8.GetBytes("""{"model":"m","embeddings":[[0.1,0.2,0.3]]}""")
            ctx.Response.StatusCode <- 200
            ctx.Response.OutputStream.Write(body, 0, body.Length)
            ctx.Response.OutputStream.Close())
    worker.IsBackground <- true
    worker.Start()
    try
        use http = new HttpClient()
        let embedder = OllamaEmbedder(http, "http://localhost:11692", "nomic-embed-text") :> Ports.IEmbedder
        let vec = embedder.Embed("hello")
        worker.Join(TimeSpan.FromSeconds 5.0) |> ignore
        Assert.Equal("/api/embed", capturedPath.Value)
        Assert.NotEqual<string>("{}", captured.Value.Trim())     // public-envelope regression
        Assert.Contains("\"model\"", captured.Value)
        Assert.Contains("\"input\"", captured.Value)
        Assert.Contains("nomic-embed-text", captured.Value)
        Assert.Equal<float array>([| 0.1; 0.2; 0.3 |], vec)
    finally
        listener.Stop()

[<Fact>]
let ``OllamaEmbedder throws with clear message when embeddings is empty`` () =
    let listener = new HttpListener()
    listener.Prefixes.Add("http://localhost:11693/")
    listener.Start()
    let worker =
        Thread(fun () ->
            let ctx = listener.GetContext()
            let body = Text.Encoding.UTF8.GetBytes("""{"model":"m","embeddings":[]}""")
            ctx.Response.StatusCode <- 200
            ctx.Response.OutputStream.Write(body, 0, body.Length)
            ctx.Response.OutputStream.Close())
    worker.IsBackground <- true
    worker.Start()
    try
        use http = new HttpClient()
        let embedder = OllamaEmbedder(http, "http://localhost:11693", "nomic-embed-text") :> Ports.IEmbedder
        Assert.ThrowsAny<System.Exception>(fun () -> embedder.Embed("x") |> ignore)
    finally
        listener.Stop()
