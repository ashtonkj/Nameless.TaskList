module Nameless.TaskList.Core.Tests.AgentTests

open System.Collections.Generic
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Conversation
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

let private echoTool (name: string) (output: string) : Tool =
    { Definition = { Function = { Name = name; Description = "test"
                                  Parameters = { Type = "object"; Required = [||]
                                                 Properties = Dictionary<string, ToolProperty>() } } }
      Handler = fun _ -> output }

[<Fact>]
let ``runConversation returns final content when no tool calls`` () =
    let client = FakeChatClient([ Responses.final "the answer" ])
    let result = Agent.runConversation (client :> IChatClient) [] "sys" "user"
    Assert.Equal("the answer", result)
    Assert.Equal(1, client.Calls)

[<Fact>]
let ``runConversation dispatches a tool call then returns final content`` () =
    let client = FakeChatClient([ Responses.toolCall "get_contexts"; Responses.final "done" ])
    let tools = [ echoTool "get_contexts" "context dump" ]
    let result = Agent.runConversation (client :> IChatClient) tools "sys" "user"
    Assert.Equal("done", result)
    Assert.Equal(2, client.Calls)

[<Fact>]
let ``runConversation raises AgentError when it never stops calling tools`` () =
    // Six tool-call responses with no final: must trip the 5-iteration guard.
    let script = List.replicate 6 (Responses.toolCall "get_contexts")
    let client = FakeChatClient(script)
    let tools = [ echoTool "get_contexts" "x" ]
    Assert.Throws<Agent.AgentError>(fun () ->
        Agent.runConversation (client :> IChatClient) tools "sys" "user" |> ignore)
    |> ignore
