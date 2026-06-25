module Nameless.TaskList.Core.Tests.RelationshipsTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Prompts
open Xunit

let private resolve =
    function
    | "ethan" -> Some "people/family/ethan.md"
    | "dr-naidoo" -> Some "people/medical/dr-naidoo.md"
    | _ -> None

let private edge from' to' rel conf : RelationshipEdge =
    { From = from'; To = to'; Relation = rel; Descriptor = ""; Confidence = conf }

[<Fact>]
let ``buildEdge resolves endpoints and sets fields`` () =
    match Relationships.buildEdge resolve "messages/x.md" (edge "ethan" "dr-naidoo" "patient-doctor" "high") with
    | Some r ->
        Assert.Equal("Relationship", r.Type)
        Assert.Equal("people/family/ethan.md", r.From)
        Assert.Equal("people/medical/dr-naidoo.md", r.To)
        Assert.Equal("patient-doctor", r.Relation)
        Assert.Equal<string[]>([| "ethan"; "dr-naidoo" |], r.People)
        Assert.Equal("messages/x.md", r.Source)
    | None -> failwith "expected Some"

[<Fact>]
let ``buildEdge rejects unknown endpoint`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "stranger" "friend" "high")).IsNone)

[<Fact>]
let ``buildEdge rejects relation not in enum`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "nemesis" "high")).IsNone)

[<Fact>]
let ``buildEdge rejects self edge`` () =
    Assert.True((Relationships.buildEdge resolve "m" (edge "ethan" "ethan" "sibling" "high")).IsNone)

[<Fact>]
let ``reconcile writes when none exists`` () =
    let inc = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "medium")).Value
    Assert.True((Relationships.reconcile None inc).IsSome)

[<Fact>]
let ``reconcile upgrades on higher confidence only`` () =
    let lower = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "medium")).Value
    let higher = (Relationships.buildEdge resolve "m" (edge "ethan" "dr-naidoo" "patient-doctor" "high")).Value
    Assert.True((Relationships.reconcile (Some lower) higher).IsSome)   // medium -> high upgrades
    Assert.True((Relationships.reconcile (Some higher) lower).IsNone)   // high -> medium skips
