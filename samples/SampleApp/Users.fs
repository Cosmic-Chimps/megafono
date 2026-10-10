/// The sample app's own data: users in memory. The ids are fixed and immutable, as Megáfono requires (FR-004).
module SampleApp.Users

type User =
    { Id: string
      Handle: string
      Name: string
      Summary: string
      Locked: bool }

let all =
    [ { Id = "3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10"; Handle = "test"; Name = "Test User"; Summary = "<p>The sample app's test user</p>"; Locked = false }
      { Id = "8d2e5b1c-4a6f-4e3d-9c7b-1f0a2b3c4d5e"; Handle = "alice"; Name = "Alice"; Summary = "<p>A locked account: follows wait for approval</p>"; Locked = true } ]

let byHandle (handle: string) =
    all |> List.tryFind (fun u -> u.Handle = handle.ToLowerInvariant())

let byId (id: string) = all |> List.tryFind (fun u -> u.Id = id)
