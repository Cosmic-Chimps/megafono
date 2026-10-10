/// Retry schedule shared by deliveries and webhooks (FR-051)
module Megafono.Domain.Backoff

open System

let private maxDelay = TimeSpan.FromHours 6.0
let private firstDelay = TimeSpan.FromMinutes 1.0
let private maxJitter = 0.1

/// How long a delivery keeps being retried
let deadline = TimeSpan.FromHours 48.0

/// Delay before the next attempt, `attempts` being the number of attempts already made (>= 1):
/// min(6 h, 1 min × 2^(attempts − 1)) plus up to 10 % jitter. `rand` returns a value in [0, 1).
let nextDelay (attempts: int) (rand: unit -> float) : TimeSpan =
    // 2^20 min is far past the cap; the clamp only keeps the shift from overflowing
    let exponent = attempts - 1 |> max 0 |> min 20
    let baseDelay = firstDelay * float (1 <<< exponent) |> min maxDelay
    baseDelay + baseDelay * (maxJitter * rand ())

/// True once the delivery is older than the 48 h deadline
let isPastDeadline (createdAt: DateTimeOffset) (now: DateTimeOffset) : bool = now - createdAt > deadline
