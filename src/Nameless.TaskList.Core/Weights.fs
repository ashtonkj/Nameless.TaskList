namespace Nameless.TaskList.Core

open System.Text.RegularExpressions

module Weights =

    type ScoringWeights =
        { ContextWeights: Map<string, int>
          DueWithin7: int
          DueWithin2: int
          UnassignedCommitmentDueWithin7: int
          Blocked: int }

    let defaults : ScoringWeights =
        { ContextWeights =
            Map.ofList
                [ "medical", 10; "finance", 10; "school", 9
                  "family", 7; "professional", 5; "personal-kb", 2 ]
          DueWithin7 = 3
          DueWithin2 = 5
          UnassignedCommitmentDueWithin7 = 5
          Blocked = -2 }

    /// The leading signed integer of the first line containing `fragment`, or None.
    let private modifierFor (text: string) (fragment: string) : int option =
        text.Split('\n')
        |> Array.tryPick (fun line ->
            if line.Contains(fragment) then
                let m = Regex.Match(line, @"-?\d+")
                if m.Success then Some(int m.Value) else None
            else None)

    /// Parse `_meta/priority-weights.md` body; any value not found falls back to defaults. Never throws.
    let parse (markdown: string) : ScoringWeights =
        let md = if isNull markdown then "" else markdown
        // Context weights: lines like `medical:      10`
        let ctxOverrides =
            md.Split('\n')
            |> Array.choose (fun line ->
                let m = Regex.Match(line.Trim(), @"^([a-z][a-z0-9\-]*):\s*(-?\d+)\s*$")
                if m.Success then Some(m.Groups.[1].Value, int m.Groups.[2].Value) else None)
            |> Array.fold (fun (acc: Map<string,int>) (k, v) -> acc.Add(k, v)) defaults.ContextWeights
        let pick fragment fallback = modifierFor md fragment |> Option.defaultValue fallback
        { ContextWeights = ctxOverrides
          DueWithin7 = pick "within 7 days" defaults.DueWithin7
          DueWithin2 = pick "within 2 days" defaults.DueWithin2
          UnassignedCommitmentDueWithin7 = pick "commitment with no task_assigned" defaults.UnassignedCommitmentDueWithin7
          Blocked = pick "status == \"blocked\"" defaults.Blocked }
