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

    let private inputStrArr (case: Dataset.Case) (name: string) : string array =
        match case.Input.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [| for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() |]
        | _ -> [||]

    /// Normalise a case referenceDate (a bare date or datetime) to a SAST (+02:00) noon ISO
    /// timestamp string, the form the create prompts and the event date-inference expect.
    /// The +02:00 is fixed deliberately: the gold dataset's expected dates are themselves SAST
    /// (e.g. the event-create fixtures), so this is not a missed configurability.
    let private isoRef (s: string) : string =
        match System.DateTimeOffset.TryParse s with
        | true, dto -> System.DateTimeOffset(dto.Year, dto.Month, dto.Day, 12, 0, 0, System.TimeSpan.FromHours 2.0).ToString("yyyy-MM-ddTHH:mm:sszzz")
        | _ -> s

    let private personGenInput (case: Dataset.Case) : Steps.GenInput =
        { Intent = inputStr case "name" ""
          Raw = ""; ReferenceDate = ""
          Contexts = inputStrArr case "contexts"
          Urgency = ""; TopicPath = ""; MessagePath = ""
          PeopleSlugs = [||]; TaskPaths = [] }

    let private inputSlugList (case: Dataset.Case) : string list =
        inputStrArr case "slugs" |> Array.toList

    /// Build the generation input from a case, with neutral stub linkage (the eval scores only
    /// model-generated fields, never linkage).
    let private genInput (case: Dataset.Case) : Steps.GenInput =
        { Intent = inputStr case "intent" (inputStr case "message" "")
          Raw = inputStr case "message" ""
          ReferenceDate = isoRef (inputStr case "referenceDate" "")
          Contexts = inputStrArr case "contexts"
          Urgency = inputStr case "urgency" "medium"
          TopicPath = ""; MessagePath = ""; PeopleSlugs = [||]; TaskPaths = [] }

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
        | "task-create" ->
            let r = try Ok (Steps.createTask chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreTask case r
        | "event-create" ->
            let r = try Ok (Steps.createEvent chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreEvent case r
        | "commitment-create" ->
            let r = try Ok (Steps.createCommitment chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreCommitment case r
        | "note-create" ->
            let r = try Ok (Steps.createNote chat (genInput case)) with ex -> Error ex.Message
            Scoring.scoreNote case r
        | "task-match" ->
            let r = try Ok (Steps.matchTask chat embedder vault (inputStr case "intent" "") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "note-match" ->
            let r = try Ok (Steps.matchNote chat embedder vault (inputStr case "intent" "") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "person-match" ->
            let r = try Ok (Steps.matchPerson chat embedder vault (inputStr case "name" "") (inputStrArr case "contexts") floor topK) with ex -> Error ex.Message
            Scoring.scoreMatch case r
        | "task-update" ->
            let r = try Steps.updateTask chat (inputStr case "existingFile" "") (inputStr case "intent" "") (inputStr case "message" "") with ex -> Error ex.Message
            Scoring.scoreTask case r
        | "note-update" ->
            let r = try Ok (Steps.updateNote chat (inputStr case "existingBody" "") (inputStr case "intent" "") (inputStr case "message" "")) with ex -> Error ex.Message
            Scoring.scoreNoteUpdate case r
        | "person-stub-create" ->
            let r = try Ok (Steps.createPersonStub chat (personGenInput case)) with ex -> Error ex.Message
            Scoring.scorePerson case r
        | "relationship-extract" ->
            let r = try Steps.extractRelationships chat (inputSlugList case) (inputStr case "message" "") with ex -> Error ex.Message
            Scoring.scoreRelationships case r
        | other ->
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0
              Fields = [ { Field = "step"; Score = 0.0; Detail = sprintf "unknown step '%s'" other } ]
              NoisePair = None; ParseError = Some(sprintf "unknown step '%s'" other) }

    let runAll (chat: IChatClient) (embedder: IEmbedder) (datasetRoot: string)
               (topK: int) (floor: float) (cases: Dataset.Case list) : Scoring.CaseResult list =
        cases |> List.map (runCase chat embedder datasetRoot topK floor)
