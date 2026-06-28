namespace Nameless.TaskList.Eval

open System
open System.IO
open System.Text.Json

module Config =

    let rec private findRoot (dir: string) =
        if isNull dir then failwith "could not locate Nameless.TaskList.slnx above the eval assembly"
        elif File.Exists(Path.Combine(dir, "Nameless.TaskList.slnx")) then dir
        else findRoot (Path.GetDirectoryName dir)

    let repoRoot = findRoot AppContext.BaseDirectory
    let private hostSettings = Path.Combine(repoRoot, "src", "Nameless.TaskList", "appsettings.json")

    type OllamaConfig =
        { Url: string; Model: string; EmbedModel: string; NumCtx: int; Temperature: float }

    let loadOllama () : OllamaConfig =
        use doc = JsonDocument.Parse(File.ReadAllText hostSettings)
        let ollama = doc.RootElement.GetProperty("Ollama")
        let str (k: string) (d: string) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> d
        let intOf (k: string) (d: int) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> d
        let floatOf (k: string) (d: float) =
            match ollama.TryGetProperty k with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
            | _ -> d
        { Url = str "Url" "http://localhost:11434"
          Model = str "Model" "granite4.1:8b"
          EmbedModel = str "EmbedModel" "nomic-embed-text"
          NumCtx = intOf "NumCtx" 16384
          Temperature = floatOf "Temperature" 0.0 }
