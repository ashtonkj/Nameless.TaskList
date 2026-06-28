namespace Nameless.TaskList.Eval

open System
open System.Text
open System.Text.Json
open Nameless.TaskList.Eval

module Report =

    type Scorecard =
        { Model: string
          Date: string
          Overall: float
          Steps: (string * float * int) list      // name, mean score, case count
          Cases: (string * string * float) list }  // id, step, score

    let private meanBy f xs = if List.isEmpty xs then 0.0 else xs |> List.averageBy f

    let aggregate (model: string) (results: Scoring.CaseResult list) : Scorecard =
        let steps =
            results
            |> List.groupBy (fun r -> r.Step)
            |> List.map (fun (step, rs) -> step, meanBy (fun (r: Scoring.CaseResult) -> r.Score) rs, List.length rs)
            |> List.sortBy (fun (s, _, _) -> s)
        // Overall = mean over step means (each step weighted equally), per the spec.
        let overall = if List.isEmpty steps then 0.0 else steps |> List.averageBy (fun (_, s, _) -> s)
        { Model = model
          Date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
          Overall = overall
          Steps = steps
          Cases = results |> List.map (fun r -> r.Id, r.Step, r.Score) }

    /// noise precision/recall over classify cases that carry a NoisePair. "Positive" = noise=true.
    let noiseSummary (results: Scoring.CaseResult list) =
        let pairs = results |> List.choose (fun r -> r.NoisePair |> Option.map (fun p -> r.Id, p))
        let tp = pairs |> List.filter (fun (_, (e, a)) -> e && a)
        let fp = pairs |> List.filter (fun (_, (e, a)) -> not e && a) |> List.map fst
        let fn = pairs |> List.filter (fun (_, (e, a)) -> e && not a) |> List.map fst
        let precision = let d = List.length tp + List.length fp in if d = 0 then 1.0 else float (List.length tp) / float d
        let recall    = let d = List.length tp + List.length fn in if d = 0 then 1.0 else float (List.length tp) / float d
        precision, recall, fp, fn

    let toJson (sc: Scorecard) : string =
        let obj =
            {| model = sc.Model; date = sc.Date; overall = sc.Overall
               steps = [ for (n, s, c) in sc.Steps -> {| name = n; score = s; count = c |} ]
               cases = [ for (id, st, s) in sc.Cases -> {| id = id; step = st; score = s |} ] |}
        JsonSerializer.Serialize(obj, JsonSerializerOptions(JsonSerializerDefaults.Web, WriteIndented = true))

    let fromJson (json: string) : Scorecard =
        let d = JsonDocument.Parse(json)
        let r = d.RootElement
        { Model = r.GetProperty("model").GetString()
          Date = r.GetProperty("date").GetString()
          Overall = r.GetProperty("overall").GetDouble()
          Steps = [ for s in r.GetProperty("steps").EnumerateArray() ->
                        s.GetProperty("name").GetString(), s.GetProperty("score").GetDouble(), s.GetProperty("count").GetInt32() ]
          Cases = [ for c in r.GetProperty("cases").EnumerateArray() ->
                        c.GetProperty("id").GetString(), c.GetProperty("step").GetString(), c.GetProperty("score").GetDouble() ] }

    let private fmt (x: float) = x.ToString("0.000")
    let private delta (cur: float) (baseOpt: float option) =
        match baseOpt with
        | Some b -> let d = cur - b in sprintf " (%s%s)" (if d >= 0.0 then "+" else "") (d.ToString("0.000"))
        | None -> ""

    let toMarkdown (sc: Scorecard) (results: Scoring.CaseResult list) (baseline: Scorecard option) : string =
        let sb = StringBuilder()
        let baseOverall = baseline |> Option.map (fun b -> b.Overall)
        let baseStep name = baseline |> Option.bind (fun b -> b.Steps |> List.tryPick (fun (n, s, _) -> if n = name then Some s else None))
        sb.AppendLine(sprintf "# Eval scorecard — %s" sc.Model) |> ignore
        sb.AppendLine(sprintf "_%s · %d cases · overall **%s**%s_" sc.Date (List.length sc.Cases) (fmt sc.Overall) (delta sc.Overall baseOverall)) |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("| Step | Cases | Score |") |> ignore
        sb.AppendLine("|------|------:|------:|") |> ignore
        for (name, score, count) in sc.Steps do
            sb.AppendLine(sprintf "| %s | %d | %s%s |" name count (fmt score) (delta score (baseStep name))) |> ignore
        sb.AppendLine() |> ignore
        let p, r, fp, fn = noiseSummary results
        if results |> List.exists (fun x -> x.NoisePair.IsSome) then
            sb.AppendLine(sprintf "**Noise** — precision %s · recall %s" (fmt p) (fmt r)) |> ignore
            if not (List.isEmpty fp) then sb.AppendLine(sprintf "- false positives: %s" (String.concat ", " fp)) |> ignore
            if not (List.isEmpty fn) then sb.AppendLine(sprintf "- false negatives: %s" (String.concat ", " fn)) |> ignore
            sb.AppendLine() |> ignore
        let failures = results |> List.filter (fun x -> x.Score < 1.0) |> List.sortBy (fun x -> x.Score)
        if not (List.isEmpty failures) then
            sb.AppendLine("## Below-par cases") |> ignore
            for f in failures do
                sb.AppendLine(sprintf "### %s (%s) — %s" f.Id f.Step (fmt f.Score)) |> ignore
                match f.ParseError with Some e -> sb.AppendLine(sprintf "- parse error: %s" e) |> ignore | None -> ()
                for fld in f.Fields do
                    if fld.Score < 1.0 then sb.AppendLine(sprintf "- %s: %s — %s" fld.Field (fmt fld.Score) fld.Detail) |> ignore
        sb.ToString()
