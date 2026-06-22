namespace Nameless.TaskList.Core.Conversation

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text.Json

type UserMessage =
    {
        Content: string
    }
    member this.Role = "user"

type SystemMessage =
    {
        Content: string
    }
    member this.Role = "system"

type ToolCallFunctionRequest =
    {
        Index: int
        Name: string
        Arguments: Dictionary<string, obj>
    }

type ToolCall =
    {
        Function: ToolCallFunctionRequest
    }
    member this.Type = "function"
    
type AssistantMessage =
    {
        Content: string
        [<System.Text.Json.Serialization.JsonPropertyName("tool_calls")>]
        ToolCalls: ToolCall array
    }
    member this.Role = "assistant"

type ToolMessage =
    {
        Content: string
        [<System.Text.Json.Serialization.JsonPropertyName("tool_name")>]
        ToolName: string
    }
    member this.Role = "tool"

type Message =
    | UserMessage of UserMessage
    | SystemMessage of SystemMessage
    | AssistantMessage of AssistantMessage
    | ToolMessage of ToolMessage
    member this.Value : obj =
        match this with
        | UserMessage um -> box um
        | SystemMessage sm -> box sm
        | AssistantMessage am -> box am
        | ToolMessage tm -> box tm
        
        
(*
{
      "type": "function",
      "function": {
        "name": "get_temperature",
        "description": "Get the current temperature for a city",
        "parameters": {
          "type": "object",
          "required": ["city"],
          "properties": {
            "city": {"type": "string", "description": "The name of the city"}
          }
        }
      }
    }
*)
type ToolProperty =
    {
        Type: string
        Description: string
    }

type ToolParameter =
    {
        Type: string
        Required: string array
        Properties: Dictionary<string, ToolProperty>
    }

type ToolFunction =
    {
        Name: string
        Description: string
        Parameters: ToolParameter
    }

type ToolDefinition =
    {
        Function: ToolFunction
    }
    member this.Type = "function"

(*
curl -s http://localhost:11434/api/chat -H "Content-Type: application/json" -d '{
  "model": "qwen3",
  "messages": [
    {"role": "user", "content": "What is the temperature in New York?"},
    {
      "role": "assistant",
      "tool_calls": [
        {
          "type": "function",
          "function": {
            "index": 0,
            "name": "get_temperature",
            "arguments": {"city": "New York"}
          }
        }
      ]
    },
    {"role": "tool", "tool_name": "get_temperature", "content": "22°C"}
  ],
  "stream": false
}'
*)
type ChatCall =
    {
        Model: string
        Messages: obj array
    }
    member this.Stream = false

type Conversation() =
    member val Model = "gemma4:e4b"
    member val Messages = new ResizeArray<Message>() with get
    member this.Call() =
        let url = "http://localhost:11434/api/chat"
        let handler = new System.Net.Http.HttpClientHandler()
        let client = new HttpClient(handler)
        let call =
            {
                Model = this.Model
                Messages = this.Messages |> Seq.map (fun m -> m.Value) |> Array.ofSeq
            }
        let mediaType = MediaTypeHeaderValue.Parse("application/json")
        let content = JsonContent.Create(call, mediaType, JsonSerializerOptions.Web)
        let txt = content.ReadAsStringAsync().Result
        printfn $"Text = {txt}"
        let result = client.PostAsync(Uri(url), content)
        result

type ResponseToolCallFunction =
    {
        Name: string
        Arguments: System.Collections.Generic.Dictionary<string, obj>
    }

type ResponseToolCall =
    {
        Function: ResponseToolCallFunction
    }

type ChatResponseMessage =
    {
        Content: string
        [<System.Text.Json.Serialization.JsonPropertyName("tool_calls")>]
        ToolCalls: ResponseToolCall array
    }

type ChatResponse =
    {
        Message: ChatResponseMessage
    }

module Response =
    let parseResponse (json: string) : ChatResponse =
        let options = System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        System.Text.Json.JsonSerializer.Deserialize<ChatResponse>(json, options)
    