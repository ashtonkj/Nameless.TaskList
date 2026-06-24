namespace Nameless.TaskList.Core

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json
open Npgsql
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.BulkProcessor

module Adapters =

    // ---- Vault over the local filesystem ----
    type FileSystemVault(root: string) =
        let full (relPath: string) = Path.Combine(root, relPath)
        interface IVault with
            member _.Exists(relPath) = File.Exists(full relPath)
            member _.Read(relPath) = File.ReadAllText(full relPath)
            member _.Write(relPath, content) =
                let path = full relPath
                Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
                File.WriteAllText(path, content)
            member _.ListFiles(relDir) =
                let dir = full relDir
                if Directory.Exists(dir) then
                    Directory.GetFiles(dir)
                    |> Array.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/'))
                    |> List.ofArray
                else []
            member _.ListFilesRecursive(relDir) =
                let dir = full relDir
                if Directory.Exists(dir) then
                    Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    |> Array.map (fun p -> Path.GetRelativePath(root, p).Replace('\\', '/'))
                    |> List.ofArray
                else []

    // ---- Bulk-job store over a single JSON file on the local filesystem ----
    type FileSystemJobStore(path: string) =
        interface IJobStore with
            member _.Save(jobs) =
                let dir = Path.GetDirectoryName(path)
                if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore
                // F# list isn't natively STJ-serializable: persist as an array.
                File.WriteAllText(path, JsonSerializer.Serialize(List.toArray jobs))
            member _.Load() =
                try
                    if File.Exists path then
                        match JsonSerializer.Deserialize<BulkJob[]>(File.ReadAllText path) with
                        | null -> []
                        | arr -> List.ofArray arr
                    else []
                with _ -> []

    // ---- Chat client over Ollama ----
    // NOTE: must NOT be `private`. System.Text.Json only serializes public types'
    // members, so a private record serializes to `{}` — sending an empty body that
    // Ollama rejects with 400 "missing request body". (Caught only by a live run.)
    type OllamaRequest =
        { model: string
          messages: obj array
          tools: obj array
          stream: bool }

    type OllamaChatClient(httpClient: HttpClient, url: string, model: string) =
        interface IChatClient with
            member _.Chat(messages, tools) =
                let body = { model = model; messages = messages; tools = tools; stream = false }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                let content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let endpoint = url.TrimEnd('/') + "/api/chat"
                // Deliberate sync block: IChatClient.Chat is synchronous by design (keeps the Agent loop simple); safe because ASP.NET Core has no synchronization context.
                let response = httpClient.PostAsync(Uri(endpoint), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                Response.parseResponse json

    // ---- Embedder over Ollama ----
    // NOTE: must NOT be `private` — a private record serializes to `{}` (System.Text.Json
    // only serializes public types' members), which would send an empty body.
    type OllamaEmbedRequest = { model: string; input: string }

    type OllamaEmbedder(httpClient: HttpClient, url: string, model: string) =
        interface IEmbedder with
            member _.Embed(text) =
                let body = { model = model; input = text }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                use content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let endpoint = url.TrimEnd('/') + "/api/embed"
                let response = httpClient.PostAsync(Uri(endpoint), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                use doc = JsonDocument.Parse(json)
                match doc.RootElement.TryGetProperty("embeddings") with
                | true, embeddings when embeddings.GetArrayLength() > 0 ->
                    [| for el in embeddings.[0].EnumerateArray() -> el.GetDouble() |]
                | _ -> failwith "Ollama /api/embed returned no embeddings"

    // ---- Vision over Ollama ----
    // Public (serialized) — a private record would serialize to `{}`.
    type VisionMessage = { role: string; content: string; images: string array }
    type OllamaVisionRequest = { model: string; messages: VisionMessage array; stream: bool }

    type OllamaVision(httpClient: HttpClient, url: string, model: string) =
        let prompt =
            "Describe this image. If it contains text (an invitation, flyer, schedule, notice, or screenshot), transcribe the text verbatim."
        interface IVision with
            member _.Describe(imageBytes) =
                let b64 = System.Convert.ToBase64String(imageBytes)
                let body =
                    { model = model; stream = false
                      messages = [| { role = "user"; content = prompt; images = [| b64 |] } |] }
                let mediaType = MediaTypeHeaderValue.Parse("application/json")
                use content = JsonContent.Create(body, mediaType, JsonSerializerOptions(JsonSerializerDefaults.Web))
                let response = httpClient.PostAsync(Uri(url.TrimEnd('/') + "/api/chat"), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                (Response.parseResponse json).Message.Content

    // ---- Transcription over the local Whisper CLI ----
    // `--language` is included only when set, so an empty value lets Whisper
    // auto-detect each note's language. Pure so it can be unit-tested without the binary.
    module WhisperArgs =
        let build (model: string) (language: string) (inputName: string) (outputDir: string) : string list =
            [ inputName
              "--model"; model
              "--output_format"; "txt"
              "--output_dir"; outputDir
              "--fp16"; "False" ]
            @ (if System.String.IsNullOrWhiteSpace language then [] else [ "--language"; language ])

    type WhisperTranscriber(command: string, model: string, language: string, timeoutSeconds: int) =
        interface ITranscriber with
            member _.Transcribe(audioBytes) =
                let workDir =
                    Path.Combine(Path.GetTempPath(), "whisper-" + System.Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(workDir) |> ignore
                try
                    let inputName = "audio.ogg"
                    File.WriteAllBytes(Path.Combine(workDir, inputName), audioBytes)
                    let psi = System.Diagnostics.ProcessStartInfo(command)
                    WhisperArgs.build model language inputName workDir |> List.iter psi.ArgumentList.Add
                    psi.WorkingDirectory <- workDir
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    use proc = System.Diagnostics.Process.Start(psi)
                    // Read both streams async to avoid a full-pipe deadlock while we wait.
                    let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                    let stderrTask = proc.StandardError.ReadToEndAsync()
                    if not (proc.WaitForExit(timeoutSeconds * 1000)) then
                        (try proc.Kill(true) with _ -> ())
                        failwithf "whisper timed out after %d s" timeoutSeconds
                    stdoutTask.Result |> ignore
                    let stderr = stderrTask.Result
                    if proc.ExitCode <> 0 then
                        failwithf "whisper exited %d: %s" proc.ExitCode stderr
                    File.ReadAllText(Path.Combine(workDir, "audio.txt")).Trim()
                finally
                    (try Directory.Delete(workDir, true) with _ -> ())

    // ---- Message source over Postgres ----
    let private getStringOrNull (reader: NpgsqlDataReader) (col: string) =
        let ord = reader.GetOrdinal(col)
        if reader.IsDBNull(ord) then null else reader.GetString(ord)

    let private getIntOrNone (reader: NpgsqlDataReader) (col: string) =
        let ord = reader.GetOrdinal(col)
        if reader.IsDBNull(ord) then None else Some(reader.GetInt32(ord))

    let private mapChat (reader: NpgsqlDataReader) : ChatMessage =
        { Id = reader.GetString(reader.GetOrdinal("id"))
          ChatJid = reader.GetString(reader.GetOrdinal("chat_jid"))
          ChatName = getStringOrNull reader "chat_name"
          NormalizedChatName = getStringOrNull reader "normalized_chat_name"
          IsGroup = reader.GetBoolean(reader.GetOrdinal("is_group"))
          SenderId = getStringOrNull reader "sender"
          SenderName = getStringOrNull reader "sender_name"
          SenderPushName = getStringOrNull reader "sender_push_name"
          SenderSavedName = getStringOrNull reader "sender_saved_name"
          SenderBusinessName = getStringOrNull reader "sender_business_name"
          IsFromMe = reader.GetBoolean(reader.GetOrdinal("is_from_me"))
          Content = getStringOrNull reader "content"
          MediaType = getStringOrNull reader "media_type"
          FileName = getStringOrNull reader "filename"
          AlbumId = getStringOrNull reader "album_id"
          AlbumIndex = getIntOrNone reader "album_index"
          Timestamp = reader.GetDateTime(reader.GetOrdinal("timestamp")) }

    type PostgresMessageSource(connectionString: string) =
        let openConnection () =
            let c = new NpgsqlConnection(connectionString)
            c.Open()
            c
        interface IMessageSource with
            member _.GetMessage(id, chatJid) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetMessageByIdAndChatJid, conn)
                cmd.Parameters.AddWithValue("Id", id) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                // F# resolves ExecuteReader() to the inherited DbDataReader overload, so an explicit downcast to NpgsqlDataReader is required
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                if reader.Read() then Some(mapChat reader) else None
            member _.GetRecent(chatJid, before, excludingId) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetPreviousMessagesByChatIdAndJid, conn)
                cmd.Parameters.AddWithValue("Id", excludingId) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                cmd.Parameters.AddWithValue("Timestamp", before) |> ignore
                // F# resolves ExecuteReader() to the inherited DbDataReader overload, so an explicit downcast to NpgsqlDataReader is required
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                [ while reader.Read() do yield mapChat reader ]
            member _.GetMessagesSince(chatJid, since) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetMessagesSince, conn)
                cmd.Parameters.AddWithValue("Since", since) |> ignore
                let p = cmd.Parameters.Add("ChatJid", NpgsqlTypes.NpgsqlDbType.Text)
                p.Value <- (match chatJid with Some j -> box j | None -> box System.DBNull.Value)
                // F# resolves ExecuteReader() to the inherited DbDataReader overload, so the downcast is required.
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                [ while reader.Read() do yield mapChat reader ]
            member _.GetMediaBytes(id, chatJid) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetMediaBytes, conn)
                cmd.Parameters.AddWithValue("Id", id) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                if reader.Read() && not (reader.IsDBNull 0) then Some(reader.GetFieldValue<byte array>(0)) else None
