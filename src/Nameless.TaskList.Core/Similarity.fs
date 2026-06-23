namespace Nameless.TaskList.Core

module Similarity =

    /// Cosine similarity of two equal-length vectors. Returns 0.0 for null,
    /// length mismatch, empty, or a zero-norm input.
    let cosine (a: float array) (b: float array) : float =
        if isNull a || isNull b || a.Length <> b.Length || a.Length = 0 then 0.0
        else
            let mutable dot = 0.0
            let mutable na = 0.0
            let mutable nb = 0.0
            for i in 0 .. a.Length - 1 do
                dot <- dot + a.[i] * b.[i]
                na <- na + a.[i] * a.[i]
                nb <- nb + b.[i] * b.[i]
            if na = 0.0 || nb = 0.0 then 0.0 else dot / (sqrt na * sqrt nb)
