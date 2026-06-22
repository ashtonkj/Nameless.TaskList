module Nameless.TaskList.Tests.EndpointTests

open Nameless.TaskList
open Nameless.TaskList.Core.Pipeline
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit

// Verifies the result-to-HTTP mapping (spec §8.2) without a live host.
// ASP.NET 10's IResult.ExecuteAsync requires RequestServices with logging + routing.
let private statusOf (result: PipelineResult) : int =
    let httpResult = ProcessMessageHandler.toHttp result
    let ctx = DefaultHttpContext()
    let services = ServiceCollection()
    services.AddLogging() |> ignore
    services.AddRouting() |> ignore
    ctx.RequestServices <- services.BuildServiceProvider()
    (httpResult.ExecuteAsync(ctx)).Wait()
    ctx.Response.StatusCode

[<Fact>]
let ``NotFound maps to 404`` () =
    Assert.Equal(404, statusOf NotFound)

[<Fact>]
let ``Skipped maps to 200`` () =
    Assert.Equal(200, statusOf Skipped)

[<Fact>]
let ``LlmError maps to 502`` () =
    Assert.Equal(502, statusOf (LlmError "bad json"))

[<Fact>]
let ``Processed maps to 200`` () =
    Assert.Equal(200, statusOf (Processed("topics/active/x.md", [ "tasks/pending/y.md" ])))
