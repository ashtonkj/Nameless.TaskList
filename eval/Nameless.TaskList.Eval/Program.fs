module Nameless.TaskList.Eval.Program

open System.IO
open System.Net.Http
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Eval

let private datasetRoot = Path.Combine(Config.repoRoot, "eval", "dataset")

// TopK / SimilarityFloor for topic matching — matching the host appsettings TopicMatch defaults.
let private topK = 5
let private floor = 0.35

[<EntryPoint>]
let main argv =
    match Args.parse argv with
    | Error e ->
        eprintfn "arg error: %s" e
        eprintfn "usage: --model M --steps a,b --report out.md --baseline base.json --threshold 0.85 --ollama-url URL"
        2
    | Ok opts ->
        let cfg = Config.loadOllama ()
        let url = opts.OllamaUrl |> Option.defaultValue cfg.Url
        let model = opts.Model |> Option.defaultValue cfg.Model
        let cases = Dataset.load datasetRoot opts.Steps
        if List.isEmpty cases then
            eprintfn "no cases found under %s for steps %A" datasetRoot opts.Steps
            2
        else
            use http = new HttpClient()
            http.Timeout <- System.TimeSpan.FromMinutes 10.0
            let chat = OllamaChatClient(http, url, model, cfg.NumCtx, cfg.Temperature) :> IChatClient
            let embedder = OllamaEmbedder(http, url, cfg.EmbedModel) :> IEmbedder
            let results =
                try Ok (Runner.runAll chat embedder datasetRoot topK floor cases)
                with ex -> Error ex.Message
            match results with
            | Error e ->
                eprintfn "run failed (is Ollama reachable at %s?): %s" url e
                2
            | Ok rs ->
                let scorecard = Report.aggregate model rs
                let baseline = opts.Baseline |> Option.map (fun p -> Report.fromJson (File.ReadAllText p))
                let md = Report.toMarkdown scorecard rs baseline
                printfn "%s" md
                match opts.Report with
                | Some path ->
                    File.WriteAllText(path, md)
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), Report.toJson scorecard)
                | None -> ()
                if scorecard.Overall < opts.Threshold then
                    eprintfn "FAIL: overall %.3f < threshold %.3f" scorecard.Overall opts.Threshold
                    1
                else
                    0
