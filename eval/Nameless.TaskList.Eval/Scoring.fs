namespace Nameless.TaskList.Eval

open System.Text.Json
open System.Text.RegularExpressions
open Nameless.TaskList.Core
open Nameless.TaskList.Core.KnowledgeBase
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
            let precision = min 1.0 (tp / float (List.length actual))
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

    /// The `expected.frontmatter` object, when present.
    let private frontmatterObj (case: Dataset.Case) : JsonElement option =
        match case.Expected.TryGetProperty "frontmatter" with
        | true, v when v.ValueKind = JsonValueKind.Object -> Some v
        | _ -> None

    /// Render a JSON scalar (string/bool/number) to the string form the record field compares as.
    let private jsonScalar (v: JsonElement) : string =
        match v.ValueKind with
        | JsonValueKind.String -> v.GetString()
        | JsonValueKind.True -> "true"
        | JsonValueKind.False -> "false"
        | _ -> v.GetRawText()

    /// Score one scalar frontmatter field (exact, normalised) when the case asserts it.
    let private scoreScalar (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v when (v.ValueKind = JsonValueKind.String
                        || v.ValueKind = JsonValueKind.True
                        || v.ValueKind = JsonValueKind.False
                        || v.ValueKind = JsonValueKind.Number) ->
            let exp = jsonScalar v
            let s = if norm exp = norm actual then 1.0 else 0.0
            fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
        | _ -> ()

    /// Score one array frontmatter field via set-F1 when the case asserts it.
    let private scoreArrayField (fm: JsonElement) (key: string) (actual: string array) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let exp = [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
            let act = if isNull actual then [] else List.ofArray actual
            fields.Add { Field = key; Score = setF1 exp act; Detail = sprintf "exp=%A act=%A" exp act }
        | _ -> ()

    /// Score a date/datetime field: equal instants (DateTimeOffset) when both parse, else exact string.
    let private scoreDateField (fm: JsonElement) (key: string) (actual: string) (fields: ResizeArray<FieldScore>) =
        match fm.TryGetProperty key with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let exp = v.GetString()
            let s =
                match System.DateTimeOffset.TryParse exp, System.DateTimeOffset.TryParse (if isNull actual then "" else actual) with
                | (true, a), (true, b) -> if a = b then 1.0 else 0.0
                | _ -> if norm exp = norm actual then 1.0 else 0.0
            fields.Add { Field = key; Score = s; Detail = sprintf "exp=%s act=%s" exp actual }
        | _ -> ()

    /// Score `titleMatches` (regex) and `bodyContains` (all substrings present) when asserted.
    let private scoreTitleBody (case: Dataset.Case) (title: string) (body: string) (fields: ResizeArray<FieldScore>) =
        match case.Expected.TryGetProperty "titleMatches" with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let ok = Regex.IsMatch((if isNull title then "" else title), v.GetString())
            fields.Add { Field = "titleMatches"; Score = (if ok then 1.0 else 0.0); Detail = sprintf "title=%s" title }
        | _ -> ()
        match case.Expected.TryGetProperty "bodyContains" with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let needles = [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
            let b = norm body
            let ok = needles |> List.forall (fun n -> b.Contains(norm n))
            fields.Add { Field = "bodyContains"; Score = (if ok then 1.0 else 0.0); Detail = sprintf "needles=%A" needles }
        | _ -> ()

    let private finish (case: Dataset.Case) (fields: ResizeArray<FieldScore>) : CaseResult =
        let fl = List.ofSeq fields
        { Id = case.Id; Step = case.Step; Tags = case.Tags
          Score = mean (fl |> List.map (fun f -> f.Score)); Fields = fl
          NoisePair = None; ParseError = None }

    let private genError (case: Dataset.Case) (e: string) : CaseResult =
        { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = 0.0; Fields = []
          NoisePair = None; ParseError = Some e }

    let scoreTask (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Task>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let t = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "status" t.Status fields
                scoreScalar fm "priority" t.Priority fields
                scoreArrayField fm "context" t.Context fields
                scoreDateField fm "due" t.Due fields)
            scoreTitleBody case t.Title o.Body fields
            finish case fields

    let scoreEvent (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Event>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let e = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "all_day" (string e.AllDay) fields
                scoreScalar fm "location" e.Location fields
                scoreScalar fm "reminder_days_before" (string e.ReminderDaysBefore) fields
                scoreArrayField fm "context" e.Context fields
                scoreDateField fm "when" e.When fields)
            scoreTitleBody case e.Title o.Body fields
            finish case fields

    let scoreCommitment (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Commitment>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let c = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "status" c.Status fields
                scoreScalar fm "priority" c.Priority fields
                scoreScalar fm "task_assigned" c.TaskAssigned fields
                scoreScalar fm "escalate_after_days" (string c.EscalateAfterDays) fields
                scoreArrayField fm "context" c.Context fields
                scoreDateField fm "due" c.Due fields)
            scoreTitleBody case c.Title o.Body fields
            finish case fields

    let scoreNote (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Note>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let n = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreArrayField fm "context" n.Context fields
                scoreArrayField fm "tags" n.Tags fields)
            scoreTitleBody case n.Title o.Body fields
            finish case fields

    /// Match decision: expected.decision "match" requires Ok (Some slug) with the right slug;
    /// "nomatch" requires Ok None; Error scores 0.
    let scoreMatch (case: Dataset.Case) (result: Result<string option, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok decision ->
            let want = expStr case.Expected "decision" |> Option.map norm |> Option.defaultValue ""
            let s =
                match want, decision with
                | "match", Some slug ->
                    let wantSlug = expStr case.Expected "slug" |> Option.map norm |> Option.defaultValue ""
                    if norm slug = wantSlug then 1.0 else 0.0
                | "nomatch", None -> 1.0
                | _ -> 0.0
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = s
              Fields = [ { Field = "decision"; Score = s; Detail = sprintf "want=%s got=%A" want decision } ]
              NoisePair = None; ParseError = None }

    /// Note-update: body-only assertions (bodyContains; titleMatches not applicable).
    let scoreNoteUpdate (case: Dataset.Case) (result: Result<string, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok body ->
            let fields = ResizeArray<FieldScore>()
            scoreTitleBody case "" body fields   // empty title -> titleMatches (if any) scores 0; cases use bodyContains
            finish case fields

    /// Person-stub: role (scalar); context/aliases/tags (arrays); title/body.
    let scorePerson (case: Dataset.Case) (result: Result<Steps.EntityOutcome<Person>, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok o ->
            let p = o.Record
            let fields = ResizeArray<FieldScore>()
            frontmatterObj case |> Option.iter (fun fm ->
                scoreScalar fm "role" p.Role fields
                scoreArrayField fm "context" p.Context fields
                scoreArrayField fm "aliases" p.Aliases fields
                scoreArrayField fm "tags" p.Tags fields)
            scoreTitleBody case p.Title o.Body fields
            finish case fields

    let private symmetricRelations = set [ "sibling"; "partner"; "colleague"; "friend"; "other" ]

    /// Canonicalise an edge to "relation|a|b": directed relations keep from->to; symmetric ones
    /// use the sorted pair, so order does not matter.
    let private edgeKey (fromSlug: string) (toSlug: string) (relation: string) : string =
        let r = norm relation
        let a, b = norm fromSlug, norm toSlug
        if Set.contains r symmetricRelations then
            let lo, hi = if a <= b then a, b else b, a
            sprintf "%s|%s|%s" r lo hi
        else sprintf "%s|%s|%s" r a b

    /// Exact set F1 (no glob/substring) over canonical edge keys.
    let private exactSetF1 (expected: string list) (actual: string list) : float =
        match expected, actual with
        | [], [] -> 1.0
        | [], _ -> 0.0
        | _, [] -> 0.0
        | _ ->
            let es, acts = Set.ofList expected, Set.ofList actual
            let tp = Set.intersect es acts |> Set.count |> float
            let precision = tp / float (Set.count acts)
            let recall = tp / float (Set.count es)
            if precision + recall = 0.0 then 0.0 else 2.0 * precision * recall / (precision + recall)

    /// Relationship extraction: F1 over the canonical edge set (descriptor/confidence ignored).
    let scoreRelationships (case: Dataset.Case) (result: Result<Prompts.RelationshipExtraction, string>) : CaseResult =
        match result with
        | Error e -> genError case e
        | Ok ex ->
            let actual =
                (if isNull ex.Relationships then [||] else ex.Relationships)
                |> Array.toList
                |> List.map (fun e -> edgeKey (Naming.slug e.From) (Naming.slug e.To) e.Relation)
            let expected =
                match case.Expected.TryGetProperty "relationships" with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    [ for x in v.EnumerateArray() ->
                        let g (k: string) = match x.TryGetProperty k with | true, s when s.ValueKind = JsonValueKind.String -> s.GetString() | _ -> ""
                        edgeKey (Naming.slug (g "from")) (Naming.slug (g "to")) (g "relation") ]
                | _ -> []
            let s = exactSetF1 expected actual
            { Id = case.Id; Step = case.Step; Tags = case.Tags; Score = s
              Fields = [ { Field = "relationships"; Score = s; Detail = sprintf "exp=%A act=%A" expected actual } ]
              NoisePair = None; ParseError = None }
