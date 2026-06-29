namespace Nameless.TaskList.Core

open System.Collections.Generic

/// Thread-safe bounded LRU cache: content key -> embedding vector.
/// The match-site document embeds are a small, hot set (touched every message) so they never
/// evict; one-shot query embeds churn through the LRU tail. Pure of I/O so the eviction policy
/// is unit-testable in isolation.
type LruCache(capacity: int) =
    let cap = max 1 capacity
    let sync = obj ()
    // Most-recently-used at the front (First); least-recently-used at the back (Last).
    let order = LinkedList<string * float[]>()
    let index = Dictionary<string, LinkedListNode<string * float[]>>()

    let touch (node: LinkedListNode<string * float[]>) =
        order.Remove node
        order.AddFirst node

    member _.Capacity = cap

    member _.Count = lock sync (fun () -> index.Count)

    /// Returns the cached vector by reference (no copy) — callers must treat it as immutable.
    member _.TryGet(key: string) : float[] option =
        lock sync (fun () ->
            match index.TryGetValue key with
            | true, node ->
                touch node
                Some(snd node.Value)
            | _ -> None)

    member _.Set(key: string, vector: float[]) : unit =
        lock sync (fun () ->
            match index.TryGetValue key with
            | true, node ->
                node.Value <- (key, vector)
                touch node
            | _ ->
                let node = order.AddFirst((key, vector))
                index.[key] <- node
                if index.Count > cap then
                    let lru = order.Last
                    order.RemoveLast()
                    index.Remove(fst lru.Value) |> ignore)

    /// Entries most-recently-used first.
    member _.Snapshot() : (string * float[]) list =
        lock sync (fun () -> [ for kv in order -> kv ])

    /// Seed from a most-recently-used-first list (used on load).
    member this.Seed(entriesMruFirst: (string * float[]) list) : unit =
        for (k, v) in List.rev entriesMruFirst do
            this.Set(k, v)
