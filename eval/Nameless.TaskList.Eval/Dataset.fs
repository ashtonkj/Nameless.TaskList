namespace Nameless.TaskList.Eval

open System.IO
open System.Text.Json

module Dataset =

    type HistoryTurn = { Sender: string; Content: string; MediaType: string }

    /// A gold case. Input/Expected are kept as raw JSON elements so each step's scorer reads the
    /// fields it cares about without a shared schema for every step.
    type Case =
        { Id: string
          Step: string
          Tags: string list
          World: string
          Input: JsonElement
          Expected: JsonElement
          SourcePath: string }

    let private str (e: JsonElement) (name: string) (d: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> d

    let private strList (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [ for x in v.EnumerateArray() do if x.ValueKind = JsonValueKind.String then yield x.GetString() ]
        | _ -> []

    let private parseCase (path: string) : Case =
        let doc = JsonDocument.Parse(File.ReadAllText path)
        let root = doc.RootElement.Clone()   // Clone so it outlives the JsonDocument 'use' scope
        let input = match root.TryGetProperty "input" with | true, v -> v | _ -> root
        let expected = match root.TryGetProperty "expected" with | true, v -> v | _ -> root
        { Id = str root "id" (Path.GetFileNameWithoutExtension path)
          Step = str root "step" ""
          Tags = strList root "tags"
          World = str root "world" "_base"
          Input = input
          Expected = expected
          SourcePath = path }

    /// Load cases for the requested steps (empty list = every step directory present).
    let load (datasetRoot: string) (steps: string list) : Case list =
        let stepDirs =
            if not (List.isEmpty steps) then steps
            else
                Directory.GetDirectories(datasetRoot)
                |> Array.map Path.GetFileName
                |> Array.filter (fun n -> not (n.StartsWith "_"))   // skip _worlds
                |> Array.toList
        stepDirs
        |> List.collect (fun step ->
            let dir = Path.Combine(datasetRoot, step)
            if not (Directory.Exists dir) then []
            else
                Directory.GetFiles(dir, "*.json")
                |> Array.sort
                |> Array.map parseCase
                |> Array.toList)
