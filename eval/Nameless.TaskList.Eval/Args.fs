namespace Nameless.TaskList.Eval

module Args =

    type Options =
        { Model: string option
          Steps: string list
          Report: string option
          Baseline: string option
          Threshold: float
          OllamaUrl: string option }

    let private defaults =
        { Model = None; Steps = []; Report = None; Baseline = None; Threshold = 0.85; OllamaUrl = None }

    /// Parse `--flag value` pairs. `--steps` takes a comma-separated list. Unknown flags error.
    let parse (argv: string array) : Result<Options, string> =
        let rec go (acc: Options) args =
            match args with
            | [] -> Ok acc
            | "--model" :: v :: rest -> go { acc with Model = Some v } rest
            | "--steps" :: v :: rest ->
                let steps = v.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "") |> Array.toList
                go { acc with Steps = steps } rest
            | "--report" :: v :: rest -> go { acc with Report = Some v } rest
            | "--baseline" :: v :: rest -> go { acc with Baseline = Some v } rest
            | "--ollama-url" :: v :: rest -> go { acc with OllamaUrl = Some v } rest
            | "--threshold" :: v :: rest ->
                match System.Double.TryParse v with
                | true, t -> go { acc with Threshold = t } rest
                | _ -> Error(sprintf "invalid --threshold '%s'" v)
            | flag :: _ -> Error(sprintf "unknown or incomplete argument '%s'" flag)
        go defaults (List.ofArray argv)
