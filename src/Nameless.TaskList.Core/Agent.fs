namespace Nameless.TaskList.Core

open System.Collections.Generic
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Ports

module Agent =

    exception AgentError of string

    let private maxIterations = 5

    let runConversation (client: IChatClient) (tools: Tool list) (system: string) (user: string) : string =
        let handlers =
            tools |> List.map (fun t -> t.Definition.Function.Name, t.Handler) |> Map.ofList
        let toolDefs = tools |> List.map (fun t -> box t.Definition) |> Array.ofList

        let messages = ResizeArray<Message>()
        messages.Add(SystemMessage { Content = system })
        messages.Add(UserMessage { Content = user })

        let mutable answer : string option = None
        let mutable iterations = 0

        while answer.IsNone do
            iterations <- iterations + 1
            if iterations > maxIterations then
                raise (AgentError(sprintf "Exceeded %d tool-calling iterations" maxIterations))

            let payload = messages |> Seq.map (fun m -> m.Value) |> Array.ofSeq
            let response = client.Chat(payload, toolDefs)
            let calls = response.Message.ToolCalls

            if isNull (box calls) || calls.Length = 0 then
                answer <- Some response.Message.Content
            else
                // Record the assistant turn, then execute each requested tool.
                let assistantToolCalls =
                    calls
                    |> Array.mapi (fun i c ->
                        { Function = { Index = i; Name = c.Function.Name; Arguments = c.Function.Arguments } }
                        : ToolCall)
                messages.Add(AssistantMessage { Content = response.Message.Content; ToolCalls = assistantToolCalls })

                for c in calls do
                    let name = c.Function.Name
                    let output =
                        match Map.tryFind name handlers with
                        | Some handler -> handler c.Function.Arguments
                        | None -> sprintf "Error: unknown tool '%s'" name
                    messages.Add(ToolMessage { ToolName = name; Content = output })

        answer.Value
