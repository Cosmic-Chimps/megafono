/// `result { let! ... }` for Result values
[<AutoOpen>]
module Megafono.Domain.ResultBuilder

type ResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return x = Ok x
    member _.ReturnFrom(r: Result<_, _>) = r
    member _.Zero() = Ok()

let result = ResultBuilder()

module Option =
    /// `Some x` -> `Ok x`; `None` -> `Error error`
    let toResult (error: 'e) (value: 'a option) : Result<'a, 'e> =
        match value with
        | Some v -> Ok v
        | None -> Error error
