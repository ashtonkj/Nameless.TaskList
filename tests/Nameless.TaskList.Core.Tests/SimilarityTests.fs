module Nameless.TaskList.Core.Tests.SimilarityTests

open Nameless.TaskList.Core
open Xunit

[<Fact>]
let ``identical vectors have cosine 1`` () =
    Assert.Equal(1.0, Similarity.cosine [| 1.0; 2.0; 3.0 |] [| 1.0; 2.0; 3.0 |], 6)

[<Fact>]
let ``orthogonal vectors have cosine 0`` () =
    Assert.Equal(0.0, Similarity.cosine [| 1.0; 0.0 |] [| 0.0; 1.0 |], 6)

[<Fact>]
let ``opposite vectors have cosine -1`` () =
    Assert.Equal(-1.0, Similarity.cosine [| 1.0; 0.0 |] [| -1.0; 0.0 |], 6)

[<Fact>]
let ``zero norm or mismatched length yields 0`` () =
    Assert.Equal(0.0, Similarity.cosine [| 0.0; 0.0 |] [| 1.0; 1.0 |], 6)
    Assert.Equal(0.0, Similarity.cosine [| 1.0 |] [| 1.0; 2.0 |], 6)
    Assert.Equal(0.0, Similarity.cosine [||] [||], 6)
