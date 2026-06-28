namespace Nameless.TaskList.Eval

open System.Text.Json
open System.Text.RegularExpressions
open Nameless.TaskList.Core
open Nameless.TaskList.Eval

module Scoring =

    type FieldScore = { Field: string; Score: float; Detail: string }

    type CaseResult =
        { Id: string
          Step: string
          Tags: string list
          Score: float
          Fields: FieldScore list
          NoisePair: (bool * bool) option   // (expected, actual) for the noise-confusion summary
          ParseError: string option }

    let private norm (s: string) = (if isNull s then "" else s).Trim().ToLowerInvariant()

    /// An expected entry may be a glob (`*picnic*`); an actual entry matches it on exact
    /// normalised equality OR glob match. Globless expected entries also match as a substring,
    /// so benign wording variance ("pay school fees" vs "pay the school fees") does not false-fail.
    let private matches (expectedPat: string) (actual: string) : bool =
        let e, a = norm expectedPat, norm actual
        if e = a then true
        elif e.Contains "*" then
            let rx = "^" + Regex.Escape(e).Replace("\\*", ".*") + "$"
            Regex.IsMatch(a, rx)
        else a.Contains e || e.Contains a

    /// Set F1 of actual against expected patterns. Empty-expected + empty-actual = 1.0
    /// (correctly produced nothing); empty-expected + non-empty-actual = 0.0 (spurious output).
    let setF1 (expected: string list) (actual: string list) : float =
        match expected, actual with
        | [], [] -> 1.0
        | [], _ -> 0.0
        | _, [] -> 0.0
        | _ ->
            let tp = expected |> List.filter (fun e -> actual |> List.exists (matches e)) |> List.length |> float
            let precision = tp / float (List.length actual)
            let recall = tp / float (List.length expected)
            if precision + recall = 0.0 then 0.0 else 2.0 * precision * recall / (precision + recall)

    // ---- expected-field readers over the raw JSON ----
    let private expStrList (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
        | _ -> []
    let private expBool (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when (v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False) -> Some(v.GetBoolean())
        | _ -> None
    let private expStr (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let private mean (xs: float list) = if List.isEmpty xs then 0.0 else List.average xs

    /// Score a classify result. Present expected fields are scored; absent ones are skipped.
    /// noise = exact; contexts/tasks/events/people = set-F1.
    let scoreClassify (case: Dataset.Case) (result: Result<Prompts.Classification, string>) : CaseResult =
        match result with
        | Error e ->
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0; Fields = []
              NoisePair = (expBool case.Expected "noise" |> Option.map (fun ex -> ex, false))
              ParseError = Some e }
        | Ok c ->
            let exp = case.Expected
            let fields = ResizeArray<FieldScore>()
            let arr (a: string array) = if isNull a then [] else List.ofArray a
            match expBool exp "noise" with
            | Some ex ->
                let s = if ex = c.Noise then 1.0 else 0.0
                fields.Add { Field = "noise"; Score = s; Detail = sprintf "exp=%b act=%b" ex c.Noise }
            | None -> ()
            let scoreArr name (expected: string list) (actual: string list) =
                if exp.TryGetProperty(name: string) |> fst then
                    let s = setF1 expected actual
                    fields.Add { Field = name; Score = s; Detail = sprintf "exp=%A act=%A" expected actual }
            scoreArr "contexts" (expStrList exp "contexts") (arr c.Contexts)
            scoreArr "tasks"    (expStrList exp "tasks")    (arr c.Entities.Tasks)
            scoreArr "events"   (expStrList exp "events")   (arr c.Entities.Events)
            scoreArr "people"   (expStrList exp "people")   (arr c.PeopleMentioned)
            let fieldList = List.ofSeq fields
            { Id = case.Id; Step = case.Step; Tags = case.Tags
              Score = mean (fieldList |> List.map (fun f -> f.Score))
              Fields = fieldList
              NoisePair = (expBool exp "noise" |> Option.map (fun ex -> ex, c.Noise))
              ParseError = None }

    /// Score a topic-match decision. expected.decision = "match" | "create".
    /// match => MatchExisting with the right slug; create => CreateTopic (+ optional titleContains).
    let scoreTopic (case: Dataset.Case) (result: Result<Steps.TopicDecision, string>) : CaseResult =
        let mk score detail parseErr =
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = score
              Fields = [ { Field = "decision"; Score = score; Detail = detail } ]
              NoisePair = None; ParseError = parseErr }
        match result with
        | Error e -> mk 0.0 (sprintf "error: %s" e) (Some e)
        | Ok decision ->
            let wantKind = expStr case.Expected "decision" |> Option.map norm |> Option.defaultValue ""
            match wantKind, decision with
            | "match", Steps.MatchExisting slug ->
                let wantSlug = expStr case.Expected "slug" |> Option.map norm |> Option.defaultValue ""
                if norm slug = wantSlug then mk 1.0 (sprintf "matched %s" slug) None
                else mk 0.0 (sprintf "matched %s, wanted %s" slug wantSlug) None
            | "create", Steps.CreateTopic title ->
                let needles = expStrList case.Expected "titleContains"
                let ok = needles |> List.forall (fun n -> norm(title).Contains(norm n))
                if ok then mk 1.0 (sprintf "created '%s'" title) None
                else mk 0.0 (sprintf "created '%s', missing %A" title needles) None
            | want, got -> mk 0.0 (sprintf "wanted %s, got %A" want got) None
