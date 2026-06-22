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

    // ---- Chat client over Ollama ----
    type private OllamaRequest =
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
                let response = httpClient.PostAsync(Uri(endpoint), content).Result
                response.EnsureSuccessStatusCode() |> ignore
                let json = response.Content.ReadAsStringAsync().Result
                Response.parseResponse json

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
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                if reader.Read() then Some(mapChat reader) else None
            member _.GetRecent(chatJid, before, excludingId) =
                use conn = openConnection ()
                use cmd = new NpgsqlCommand(Queries.GetPreviousMessagesByChatIdAndJid, conn)
                cmd.Parameters.AddWithValue("Id", excludingId) |> ignore
                cmd.Parameters.AddWithValue("ChatJid", chatJid) |> ignore
                cmd.Parameters.AddWithValue("Timestamp", before) |> ignore
                use reader = cmd.ExecuteReader() :?> NpgsqlDataReader
                [ while reader.Read() do yield mapChat reader ]
