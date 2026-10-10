module Megafono.Tests.Unit.BackoffTests

open System
open Xunit
open Megafono.Domain

let private noJitter () = 0.0

[<Fact>]
let ``FR-051 the schedule doubles from one minute`` () =
    let minutes =
        [ 1..9 ] |> List.map (fun attempt -> (Backoff.nextDelay attempt noJitter).TotalMinutes)

    Assert.Equal<float list>([ 1.0; 2.0; 4.0; 8.0; 16.0; 32.0; 64.0; 128.0; 256.0 ], minutes)

[<Fact>]
let ``FR-051 the delay is capped at six hours`` () =
    for attempt in [ 10; 11; 25; 100; Int32.MaxValue ] do
        Assert.Equal(TimeSpan.FromHours 6.0, Backoff.nextDelay attempt noJitter)

[<Fact>]
let ``attempts below one are treated as the first`` () =
    Assert.Equal(TimeSpan.FromMinutes 1.0, Backoff.nextDelay 0 noJitter)
    Assert.Equal(TimeSpan.FromMinutes 1.0, Backoff.nextDelay -5 noJitter)

[<Fact>]
let ``jitter adds at most ten percent`` () =
    let nearOne () = 0.9999999
    let nearlyHalf () = 0.5

    for attempt in [ 1; 4; 9; 12 ] do
        let baseDelay = Backoff.nextDelay attempt noJitter
        let max = Backoff.nextDelay attempt nearOne
        Assert.True(max > baseDelay)
        Assert.True(max <= baseDelay * 1.1)
        Assert.Equal(baseDelay * 1.05, Backoff.nextDelay attempt nearlyHalf)

[<Fact>]
let ``random jitter always stays within the bound`` () =
    let random = Random 42

    for _ in 1..500 do
        let attempt = random.Next(1, 20)
        let baseDelay = Backoff.nextDelay attempt noJitter
        let delay = Backoff.nextDelay attempt random.NextDouble
        Assert.InRange(delay, baseDelay, baseDelay * 1.1)

[<Fact>]
let ``FR-051 the deadline is 48 hours after creation`` () =
    let created = DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero)
    Assert.False(Backoff.isPastDeadline created (created.AddHours 47.0))
    Assert.False(Backoff.isPastDeadline created (created.AddHours 48.0))
    Assert.True(Backoff.isPastDeadline created (created.AddHours 48.0 + TimeSpan.FromSeconds 1.0))
