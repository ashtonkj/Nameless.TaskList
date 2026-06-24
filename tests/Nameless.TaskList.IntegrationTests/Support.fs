module Nameless.TaskList.IntegrationTests.Support

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters

module Config =
    // Walk up from the test assembly to the directory holding the solution file, then into the host.
    let rec private findRoot (dir: string) =
        if isNull dir then failwith "could not locate Nameless.TaskList.slnx above the test assembly"
        elif File.Exists(Path.Combine(dir, "Nameless.TaskList.slnx")) then dir
        else findRoot (Path.GetDirectoryName dir)

    let repoRoot = findRoot AppContext.BaseDirectory
    let private hostDir = Path.Combine(repoRoot, "src", "Nameless.TaskList")

    let private load (file: string) =
        let p = Path.Combine(hostDir, file)
        if File.Exists p then Some(JsonDocument.Parse(File.ReadAllText p)) else None

    let private baseDoc = load "appsettings.json"
    let private devDoc = load "appsettings.Development.json"

    let private get (doc: JsonDocument option) (section: string) (key: string) =
        match doc with
        | None -> null
        | Some d ->
            match d.RootElement.TryGetProperty section with
            | true, s ->
                match s.TryGetProperty key with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> null
            | _ -> null

    let private orDefault (d: string) (v: string) = if String.IsNullOrWhiteSpace v then d else v

    let connectionString =
        [ get devDoc "ConnectionStrings" "WhatsApp"
          get baseDoc "ConnectionStrings" "WhatsApp"
          Environment.GetEnvironmentVariable "WHATSAPP_CONNSTRING" ]
        |> List.tryFind (fun s -> not (String.IsNullOrWhiteSpace s))
        |> Option.defaultValue null

    let ollamaUrl    = get baseDoc "Ollama" "Url"         |> orDefault "http://localhost:11434"
    let chatModel    = get baseDoc "Ollama" "Model"       |> orDefault "gemma4:e4b"
    let embedModel   = get baseDoc "Ollama" "EmbedModel"  |> orDefault "nomic-embed-text"
    let visionModel  = get baseDoc "Ollama" "VisionModel" |> orDefault "gemma3:latest"
    let whisperCommand  = get baseDoc "Whisper" "Command"  |> orDefault "whisper"
    let whisperModel    = get baseDoc "Whisper" "Model"    |> orDefault "base"
    let whisperLanguage = (get baseDoc "Whisper" "Language" : string)   // "" allowed (auto-detect)
    let whisperTimeout  =
        match Int32.TryParse(get baseDoc "Whisper" "TimeoutSeconds") with
        | true, v -> v
        | _ -> 300

module ServiceProbes =
    let postgres : Lazy<bool> =
        lazy (
            match Config.connectionString with
            | cs when String.IsNullOrWhiteSpace cs -> false
            | cs ->
                try
                    let b = Npgsql.NpgsqlConnectionStringBuilder(cs)
                    b.Timeout <- 3
                    use c = new Npgsql.NpgsqlConnection(b.ConnectionString)
                    c.Open()
                    true
                with _ -> false)

    let ollama : Lazy<bool> =
        lazy (
            try
                use http = new HttpClient()
                http.Timeout <- TimeSpan.FromSeconds 3.0
                let r = http.GetAsync(Config.ollamaUrl.TrimEnd('/') + "/api/tags").Result
                r.IsSuccessStatusCode
            with _ -> false)

    let private canStart (cmd: string) (arg: string) =
        try
            let psi = Diagnostics.ProcessStartInfo(cmd, arg)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            use p = Diagnostics.Process.Start(psi)
            p.WaitForExit(15000) |> ignore
            true
        with _ -> false

    let whisper : Lazy<bool> =
        lazy (canStart Config.whisperCommand "--help" && canStart "ffmpeg" "-version")

module Helpers =
    let messages () : IMessageSource =
        PostgresMessageSource(Config.connectionString) :> IMessageSource

    let firstMessageWith (pred: ChatMessage -> bool) : ChatMessage option =
        (messages ()).GetMessagesSince(None, DateTime.MinValue) |> List.tryFind pred

    let firstWithMedia (mediaType: string) : (ChatMessage * byte array) option =
        let src = messages ()
        src.GetMessagesSince(None, DateTime.MinValue)
        |> List.filter (fun m -> m.MediaType = mediaType)
        |> List.tryPick (fun m ->
            match src.GetMediaBytes(m.Id, m.ChatJid) with
            | Some b -> Some(m, b)
            | None -> None)

    let withTempVault (f: string -> unit) =
        let root = Path.Combine(Path.GetTempPath(), "ntl-it-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        try f root
        finally (try Directory.Delete(root, true) with _ -> ())
