module Nameless.TaskList.Core.Tests.ParsingTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``parseClassification reads a noise verdict`` () =
    let json = """{"noise":true,"noise_reason":"emoji only","contexts":[],"intent":null,
                   "action_required":false,"urgency":"none","people_mentioned":[],
                   "entities":{"tasks":[],"events":[],"commitments":[],"notes":[]}}"""
    match Prompts.parseClassification json with
    | Ok c -> Assert.True(c.Noise)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification extracts tasks and tolerates code fences`` () =
    let json = "```json\n" +
               """{"noise":false,"noise_reason":null,"contexts":["family"],""" +
               """"intent":"call venue","action_required":true,"urgency":"high",""" +
               """"people_mentioned":["wife"],"entities":{"tasks":["call Acrobranch"],""" +
               """"events":[],"commitments":[],"notes":[]}}""" + "\n```"
    match Prompts.parseClassification json with
    | Ok c ->
        Assert.False(c.Noise)
        Assert.Equal<string array>([| "family" |], c.Contexts)
        Assert.Equal<string array>([| "call Acrobranch" |], c.Entities.Tasks)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification de-duplicates repeated entities the model emits`` () =
    // granite at times repeats the same task verbatim (and with case/space wobble) several
    // times in one reply; the parse boundary must collapse these so they don't spawn
    // duplicate entity refs in the topic and response.
    let json =
        """{"noise":false,"noise_reason":null,"contexts":["family"],""" +
        """"intent":"avoid area","action_required":true,"urgency":"high","people_mentioned":[],""" +
        """"entities":{"tasks":["Avoid Nicolway mall area","avoid nicolway mall area "," Avoid Nicolway Mall Area"],""" +
        """"events":[],"commitments":[],"notes":[]}}"""
    match Prompts.parseClassification json with
    | Ok c -> Assert.Equal<string array>([| "Avoid Nicolway mall area" |], c.Entities.Tasks)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification returns Error on garbage`` () =
    match Prompts.parseClassification "I cannot help with that" with
    | Ok _ -> failwith "expected Error"
    | Error _ -> ()

[<Fact>]
let ``parseClassification flattens a nested contexts array`` () =
    // gemma4:e4b intermittently emits contexts as [["professional"]] instead of ["professional"].
    let json = """{"noise":false,"noise_reason":null,"contexts":[["professional"]],
                   "intent":"vehicle at the gate","action_required":true,"urgency":"high",
                   "people_mentioned":[],"entities":{"tasks":[["investigate the vehicle"]],
                   "events":[],"commitments":[],"notes":[]}}"""
    match Prompts.parseClassification json with
    | Ok c ->
        Assert.Equal<string array>([| "professional" |], c.Contexts)
        Assert.Equal<string array>([| "investigate the vehicle" |], c.Entities.Tasks)
    | Error e -> failwith e

[<Fact>]
let ``parseClassification yields non-null arrays when the model omits keys`` () =
    // gemma4:e4b sometimes omits keys entirely (here: people_mentioned, contexts, notes,
    // and even the whole entities object). Omitted keys leave CLIMutable fields null, which
    // the pipeline then dereferences with Array.toList -> NullReferenceException. The parser
    // must normalise every array (and Entities) to non-null.
    let json = """{"noise":false,"intent":"sunglasses to dr robbins","action_required":true,"urgency":"medium"}"""
    match Prompts.parseClassification json with
    | Ok c ->
        Assert.NotNull(box c.Contexts)
        Assert.NotNull(box c.PeopleMentioned)
        Assert.NotNull(box c.Entities)
        Assert.NotNull(box c.Entities.Tasks)
        Assert.NotNull(box c.Entities.Notes)
        // and they must be safe to enumerate (this is what the pipeline does)
        Assert.Empty(c.PeopleMentioned |> Array.toList)
        Assert.Empty(c.Entities.Notes |> Array.toList)
    | Error e -> failwith e

[<Fact>]
let ``parseTopicMatch reads a match decision`` () =
    let json = """{"match":true,"topic_slug":"birthday","confidence":0.9,
                   "match_reason":"same subject","new_topic_title":null}"""
    match Prompts.parseTopicMatch json with
    | Ok m ->
        Assert.True(m.Match)
        Assert.Equal("birthday", m.TopicSlug)
    | Error e -> failwith e

[<Fact>]
let ``parseRelationships reads edges from snake_case json`` () =
    let raw = """{ "relationships": [ { "from": "ethan", "to": "dr-naidoo", "relation": "patient-doctor", "descriptor": "paediatrician since 2022", "confidence": "high" } ] }"""
    match Prompts.parseRelationships raw with
    | Ok x ->
        Assert.Equal(1, x.Relationships.Length)
        Assert.Equal("ethan", x.Relationships.[0].From)
        Assert.Equal("dr-naidoo", x.Relationships.[0].To)
        Assert.Equal("patient-doctor", x.Relationships.[0].Relation)
        Assert.Equal("high", x.Relationships.[0].Confidence)
    | Error e -> failwith e

[<Fact>]
let ``parseRelationships tolerates code fences`` () =
    let raw = "```json\n{ \"relationships\": [] }\n```"
    match Prompts.parseRelationships raw with
    | Ok x -> Assert.Equal(0, x.Relationships.Length)
    | Error e -> failwith e

[<Fact>]
let ``parseRelationships returns Error on garbage`` () =
    match Prompts.parseRelationships "not json at all" with
    | Ok _ -> failwith "expected Error"
    | Error _ -> ()
