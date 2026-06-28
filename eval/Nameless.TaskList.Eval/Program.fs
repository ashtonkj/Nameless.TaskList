module Nameless.TaskList.Eval.Program

open Nameless.TaskList.Eval

[<EntryPoint>]
let main _argv =
    let cfg = Config.loadOllama ()
    printfn "Nameless.TaskList eval — model=%s url=%s (scaffold)" cfg.Model cfg.Url
    0
