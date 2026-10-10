/// Shared by the test files: valid startup configuration and a clock tests can move
module Megafono.Tests.TestSupport

open System

let masterKeyBytes = Array.init 32 byte
let masterKey = Convert.ToBase64String masterKeyBytes

/// The smallest configuration that passes startup validation
let validOverrides: (string * string) list =
    [ "Megafono:Domain", "megafono.test"; "Megafono:MasterKey", masterKey ]

type FakeTime(start: DateTimeOffset) =
    inherit TimeProvider()
    let mutable now = start
    override _.GetUtcNow() = now
    member _.Set(value: DateTimeOffset) = now <- value
