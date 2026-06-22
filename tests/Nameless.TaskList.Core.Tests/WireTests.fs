module Nameless.TaskList.Core.Tests.WireTests

open System.Collections.Generic
open System.Text.Json
open Nameless.TaskList.Core.Conversation
open Xunit

[<Fact>]
let ``parseResponse reads content and tool calls from Ollama shape`` () =
    let json =
        """{"model":"gemma4:e4b","message":{"role":"assistant","content":"",
        "tool_calls":[{"function":{"name":"get_contexts","arguments":{"q":"medical"}}}]},"done":true}"""
    let resp = Response.parseResponse json
    Assert.Equal(1, resp.Message.ToolCalls.Length)
    Assert.Equal("get_contexts", resp.Message.ToolCalls.[0].Function.Name)
    Assert.Equal("medical", string resp.Message.ToolCalls.[0].Function.Arguments.["q"])

[<Fact>]
let ``parseResponse handles final message with no tool calls`` () =
    let json = """{"model":"m","message":{"role":"assistant","content":"hello"},"done":true}"""
    let resp = Response.parseResponse json
    Assert.Equal("hello", resp.Message.Content)
    Assert.True(isNull (box resp.Message.ToolCalls) || resp.Message.ToolCalls.Length = 0)

[<Fact>]
let ``AssistantMessage with ToolCall serializes role, tool_calls, and index`` () =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    let am : AssistantMessage =
        { Content = ""
          ToolCalls =
            [| { Function = { Index = 0; Name = "get_contexts"; Arguments = Dictionary<string, obj>() } } |] }
    let json = JsonSerializer.Serialize(am, opts)
    Assert.Contains("role", json)
    Assert.Contains("assistant", json)
    Assert.Contains("tool_calls", json)
    Assert.Contains("index", json)

[<Fact>]
let ``ToolMessage serializes role tool and tool_name`` () =
    let opts = JsonSerializerOptions(JsonSerializerDefaults.Web)
    let tm : ToolMessage = { Content = "22"; ToolName = "get_contexts" }
    let json = JsonSerializer.Serialize(tm, opts)
    Assert.Contains("tool_name", json)
    Assert.Contains("role", json)
    Assert.Contains("tool", json)
