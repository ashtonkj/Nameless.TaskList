module Nameless.TaskList.Core.Tests.EmbeddingCacheTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``TryGet returns the stored vector`` () =
    let c = LruCache(3)
    c.Set("a", [| 1.0; 2.0 |])
    Assert.Equal<float[]>([| 1.0; 2.0 |], (c.TryGet "a").Value)
    Assert.Equal(None, c.TryGet "missing")

[<Fact>]
let ``Set beyond capacity evicts the least-recently-used entry`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    c.Set("c", [| 3.0 |])          // evicts "a" (LRU)
    Assert.Equal(2, c.Count)
    Assert.Equal(None, c.TryGet "a")
    Assert.True((c.TryGet "b").IsSome)
    Assert.True((c.TryGet "c").IsSome)

[<Fact>]
let ``TryGet refreshes recency so the untouched entry is evicted`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    c.TryGet "a" |> ignore         // "a" is now most-recent; "b" is LRU
    c.Set("c", [| 3.0 |])          // evicts "b"
    Assert.True((c.TryGet "a").IsSome)
    Assert.Equal(None, c.TryGet "b")
    Assert.True((c.TryGet "c").IsSome)

[<Fact>]
let ``Set on an existing key updates the value without growing`` () =
    let c = LruCache(2)
    c.Set("a", [| 1.0 |])
    c.Set("a", [| 9.0 |])
    Assert.Equal(1, c.Count)
    Assert.Equal<float[]>([| 9.0 |], (c.TryGet "a").Value)

[<Fact>]
let ``Snapshot is most-recently-used first and Seed round-trips it`` () =
    let c = LruCache(3)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])          // order: b, a
    Assert.Equal<string list>([ "b"; "a" ], c.Snapshot() |> List.map fst)
    let restored = LruCache(3)
    restored.Seed(c.Snapshot())
    Assert.Equal<string list>([ "b"; "a" ], restored.Snapshot() |> List.map fst)

[<Fact>]
let ``capacity is clamped to at least 1`` () =
    let c = LruCache(0)
    Assert.Equal(1, c.Capacity)
    c.Set("a", [| 1.0 |])
    c.Set("b", [| 2.0 |])
    Assert.Equal(1, c.Count)
