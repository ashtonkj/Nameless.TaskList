module Nameless.TaskList.Core.Tests.CachingEmbedderTests

open Nameless.TaskList.Core
open Nameless.TaskList.Core.Ports
open Nameless.TaskList.Core.Adapters
open Nameless.TaskList.Core.Tests.Fakes
open Xunit

/// In-memory store: records what was saved and serves a preset state on Load.
type private FakeCacheStore(initial: EmbeddingCacheState) =
    let mutable saved : EmbeddingCacheState option = None
    member _.Saved = saved
    interface IEmbeddingCacheStore with
        member _.Load() = initial
        member _.Save(state) = saved <- Some state

let private empty : EmbeddingCacheState = { Model = ""; Entries = [||] }

[<Fact>]
let ``identical text is embedded once (cache hit on the second call)`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(10), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "hello" |> ignore
    ce.Embed "hello" |> ignore
    Assert.Equal(1, calls)

[<Fact>]
let ``distinct text embeds each time`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(10), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "a" |> ignore
    ce.Embed "b" |> ignore
    Assert.Equal(2, calls)

[<Fact>]
let ``an evicted entry is re-embedded`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 1.0 |]) :> IEmbedder
    let ce = CachingEmbedder(inner, LruCache(1), FakeCacheStore(empty), "m", 1000) :> IEmbedder
    ce.Embed "a" |> ignore     // calls = 1
    ce.Embed "b" |> ignore     // calls = 2, evicts "a"
    ce.Embed "a" |> ignore     // calls = 3, "a" was evicted
    Assert.Equal(3, calls)

[<Fact>]
let ``the cache is persisted after SaveEveryN misses`` () =
    let inner = FakeEmbedder(fun _ -> [| 1.0 |]) :> IEmbedder
    let store = FakeCacheStore(empty)
    let ce = CachingEmbedder(inner, LruCache(10), store, "m", 2) :> IEmbedder
    ce.Embed "a" |> ignore     // miss 1 — no save yet
    Assert.True(store.Saved.IsNone)
    ce.Embed "b" |> ignore     // miss 2 — triggers a save
    Assert.True(store.Saved.IsSome)
    Assert.Equal(2, store.Saved.Value.Entries.Length)
    Assert.Equal("m", store.Saved.Value.Model)

[<Fact>]
let ``a persisted cache for the same model is loaded and serves hits`` () =
    // Warm a decorator so the store captures a real (hashed) key for "shared", then reload it.
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 7.0 |]) :> IEmbedder
    let store1 = FakeCacheStore(empty)
    let warm = CachingEmbedder(inner, LruCache(10), store1, "m", 1) :> IEmbedder
    warm.Embed "shared" |> ignore                     // calls = 1, store1 saved
    let saved = store1.Saved.Value
    // New decorator loads the saved state -> "shared" must be a hit (no new inner call).
    let cold = CachingEmbedder(inner, LruCache(10), FakeCacheStore(saved), "m", 1000) :> IEmbedder
    cold.Embed "shared" |> ignore
    Assert.Equal(1, calls)

[<Fact>]
let ``a persisted cache for a different model is discarded`` () =
    let mutable calls = 0
    let inner = FakeEmbedder(fun _ -> calls <- calls + 1; [| 7.0 |]) :> IEmbedder
    let store1 = FakeCacheStore(empty)
    let warm = CachingEmbedder(inner, LruCache(10), store1, "old-model", 1) :> IEmbedder
    warm.Embed "shared" |> ignore                     // calls = 1
    let saved = store1.Saved.Value
    let cold = CachingEmbedder(inner, LruCache(10), FakeCacheStore(saved), "new-model", 1000) :> IEmbedder
    cold.Embed "shared" |> ignore                     // model differs -> not loaded -> re-embed
    Assert.Equal(2, calls)
