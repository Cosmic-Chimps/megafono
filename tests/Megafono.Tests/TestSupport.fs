/// Shared by the test files: valid startup configuration, a clock tests can move, and polling helpers
module Megafono.Tests.TestSupport

open System
open System.Threading.Tasks

let masterKeyBytes = Array.init 32 byte
let masterKey = Convert.ToBase64String masterKeyBytes

/// The smallest configuration that passes startup validation (the connection string is a placeholder:
/// nothing connects unless the app is started)
let validOverrides: (string * string) list =
    [ "Megafono:Domain", "megafono.test"
      "Megafono:MasterKey", masterKey
      "Megafono:App:BaseUrl", "http://127.0.0.1:9"
      "Megafono:App:Token", "test-token"
      "Megafono:App:WebhookSecret", "test-secret"
      "ConnectionStrings:Database", "Host=localhost;Database=megafono_unused" ]

type FakeTime(start: DateTimeOffset) =
    inherit TimeProvider()
    let mutable now = start
    new() = FakeTime(DateTimeOffset.UtcNow)
    override _.GetUtcNow() = now
    member _.Set(value: DateTimeOffset) = now <- value
    member _.Advance(by: TimeSpan) = now <- now + by

/// Polls `condition` until it holds; fails with `what` after `timeout`
let waitUntil (what: string) (timeout: TimeSpan) (condition: unit -> bool) : Task =
    task {
        let deadline = DateTime.UtcNow + timeout

        while not (condition ()) do
            if DateTime.UtcNow > deadline then
                failwith $"Timed out after {timeout.TotalSeconds} s waiting for: {what}"

            do! Task.Delay 25
    }

/// Like `waitUntil` for a condition that needs I/O
let waitUntilAsync (what: string) (timeout: TimeSpan) (condition: unit -> Task<bool>) : Task =
    task {
        let deadline = DateTime.UtcNow + timeout
        let mutable fin = false

        while not fin do
            let! ok = condition ()
            fin <- ok

            if not fin then
                if DateTime.UtcNow > deadline then
                    failwith $"Timed out after {timeout.TotalSeconds} s waiting for: {what}"

                do! Task.Delay 50
    }
