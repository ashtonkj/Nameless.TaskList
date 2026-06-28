namespace Nameless.TaskList.Eval

open System.Text.Json
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Eval

module Runner =

    let private inputStr (case: Dataset.Case) (name: string) (d: string) =
        match case.Input.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> d

    /// Render an optional history array on the case input into the oldest->newest transcript the
    /// pipeline builds, reusing Prompts.renderHistory via lightweight ChatMessage stand-ins.
    let private historyText (case: Dataset.Case) : string =
        match case.Input.TryGetProperty "history" with
        | true, arr when arr.ValueKind = JsonValueKind.Array ->
            // The case authors history oldest->newest; renderHistory expects newest-first, so reverse.
            let turns =
                [ for t in arr.EnumerateArray() ->
                    let s (f: string) = match t.TryGetProperty f with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() | _ -> null
                    s "sender", s "content", s "mediaType" ]
            turns
            |> List.rev
            |> List.map (fun (sender, content, media) ->
                { Id = ""; ChatJid = ""; ChatName = ""; NormalizedChatName = ""; IsGroup = false
                  SenderId = ""; SenderName = (if isNull sender then "Unknown" else sender)
                  SenderPushName = null; SenderSavedName = null; SenderBusinessName = null
                  IsFromMe = false; Platform = "whatsapp-direct"; IsBroadcast = false
                  Content = content; MediaType = media
                  FileName = null; AlbumId = null; AlbumIndex = None; Timestamp = System.DateTime.MinValue } : ChatMessage)
            |> Prompts.renderHistory
        | _ -> ""

    let runCase (chat: IChatClient) (embedder: IEmbedder) (datasetRoot: string)
                (topK: int) (floor: float) (case: Dataset.Case) : Scoring.CaseResult =
        let vault = Worlds.load datasetRoot case.World
        match case.Step with
        | "classify" ->
            let content = inputStr case "message" ""
            let result =
                Steps.classify chat vault (historyText case) content
                |> Result.mapError (fun (e: Steps.ClassifyError) -> e.Message)
            Scoring.scoreClassify case result
        | "topic-match" ->
            let intent = inputStr case "intent" (inputStr case "message" "")
            let result = Steps.matchTopic chat embedder vault topK floor intent
            Scoring.scoreTopic case result
        | other ->
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0
              Fields = [ { Field = "step"; Score = 0.0; Detail = sprintf "unknown step '%s'" other } ]
              NoisePair = None; ParseError = Some(sprintf "unknown step '%s'" other) }

    let runAll (chat: IChatClient) (embedder: IEmbedder) (datasetRoot: string)
               (topK: int) (floor: float) (cases: Dataset.Case list) : Scoring.CaseResult list =
        cases |> List.map (runCase chat embedder datasetRoot topK floor)
