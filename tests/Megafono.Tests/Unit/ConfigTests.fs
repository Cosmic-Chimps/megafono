module Megafono.Tests.Unit.ConfigTests

open System
open Microsoft.Extensions.Configuration
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport

let private load (environment: string) (pairs: (string * string) list) =
    let config =
        ConfigurationBuilder()
            .AddInMemoryCollection(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)))
            .Build()

    Config.load environment config

let private errorsOf result =
    match result with
    | Error errors -> errors
    | Ok _ -> failwith "expected configuration errors"

let private optionsOf result =
    match result with
    | Ok options -> options
    | Error errors -> failwith (Config.describeErrors errors)

let private mentions (fragment: string) (errors: string list) = errors |> List.exists (fun e -> e.Contains fragment)

[<Fact>]
let ``T009 defaults: ports, concurrency and rate limits`` () =
    let o = load "Production" validOverrides |> optionsOf
    Assert.Equal("megafono.test", o.Domain)
    Assert.Equal(8080, o.Ports.Federation)
    Assert.Equal(8081, o.Ports.Internal)
    Assert.Equal(2, o.Delivery.PerHost)
    Assert.Equal(32, o.Delivery.Global)
    Assert.Equal(600, o.RateLimit.PerAddress)
    Assert.Equal(600, o.RateLimit.PerHost)
    Assert.False(o.Federation.AllowPrivateAddresses)
    Assert.Equal("megafono", o.Software.Name)
    Assert.Equal(None, o.MasterKeyPrevious)
    Assert.Empty o.TrustedProxies

[<Fact>]
let ``T009 every documented key is bound`` () =
    let o =
        load
            "Development"
            (validOverrides
             @ [ "Megafono:App:BaseUrl", "http://app:3000"
                 "Megafono:App:Token", "tok"
                 "Megafono:App:WebhookSecret", "sec"
                 "Megafono:Software:Name", "my-app"
                 "Megafono:Software:Version", "2.0"
                 "Megafono:Ports:Federation", "9000"
                 "Megafono:Ports:Internal", "9001"
                 "Megafono:Delivery:PerHost", "4"
                 "Megafono:Delivery:Global", "64"
                 "Megafono:RateLimit:PerAddress", "10"
                 "Megafono:RateLimit:PerHost", "20"
                 "Megafono:TrustedProxies", "10.0.0.1, 10.0.0.2"
                 "Megafono:Federation:AllowPrivateAddresses", "true"
                 "Megafono:MasterKeyPrevious", Convert.ToBase64String(Array.zeroCreate 32) ])
        |> optionsOf

    Assert.Equal("http://app:3000", o.App.BaseUrl)
    Assert.Equal("tok", o.App.Token)
    Assert.Equal("sec", o.App.WebhookSecret)
    Assert.Equal("my-app", o.Software.Name)
    Assert.Equal("2.0", o.Software.Version)
    Assert.Equal(9000, o.Ports.Federation)
    Assert.Equal(9001, o.Ports.Internal)
    Assert.Equal(4, o.Delivery.PerHost)
    Assert.Equal(64, o.Delivery.Global)
    Assert.Equal(10, o.RateLimit.PerAddress)
    Assert.Equal(20, o.RateLimit.PerHost)
    Assert.Equal<string list>([ "10.0.0.1"; "10.0.0.2" ], o.TrustedProxies)
    Assert.True o.Federation.AllowPrivateAddresses
    Assert.True o.MasterKeyPrevious.IsSome

[<Fact>]
let ``T009 the environment variable form Megafono__X__Y binds to the same keys`` () =
    // The environment provider turns `__` into `:`; the in-memory provider stands in for it
    let config =
        ConfigurationBuilder().AddInMemoryCollection(dict [ "Megafono__Domain", "x.test" ]).Build()

    Assert.Equal("x.test", config["Megafono__Domain"])
    Assert.Equal(Environment.GetEnvironmentVariable "NOPE", null)

    let envConfig =
        let key = "Megafono__Ports__Federation"
        Environment.SetEnvironmentVariable(key, "7777")

        try
            ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(validOverrides |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v))).Build()
        finally
            Environment.SetEnvironmentVariable(key, null)

    let o = Config.load "Production" envConfig |> optionsOf
    Assert.Equal(7777, o.Ports.Federation)

[<Fact>]
let ``T009 indexed trusted proxies work too`` () =
    let o = load "Production" (validOverrides @ [ "Megafono:TrustedProxies:0", "10.0.0.1"; "Megafono:TrustedProxies:1", "10.0.0.2" ]) |> optionsOf
    Assert.Equal<string list>([ "10.0.0.1"; "10.0.0.2" ], o.TrustedProxies)

[<Theory>]
[<InlineData("")>]
[<InlineData("not base64 !!")>]
[<InlineData("AAAA")>] // 3 bytes
[<InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")>] // 34 bytes
let ``T009 a master key that is not base64 of 32 bytes stops startup`` (key: string) =
    let errors = load "Production" [ "Megafono:Domain", "megafono.test"; "Megafono:MasterKey", key ] |> errorsOf
    Assert.True(mentions "MasterKey" errors)

[<Fact>]
let ``T009 exactly 32 bytes passes, 31 and 33 do not`` () =
    let ofLength n = Convert.ToBase64String(Array.zeroCreate<byte> n)
    Assert.True((load "Production" [ "Megafono:Domain", "d.test"; "Megafono:MasterKey", ofLength 32 ]).IsOk)

    for n in [ 31; 33; 16; 64 ] do
        Assert.True((load "Production" [ "Megafono:Domain", "d.test"; "Megafono:MasterKey", ofLength n ]).IsError)

[<Fact>]
let ``T009 a missing master key is reported`` () =
    Assert.True(mentions "MasterKey" (load "Production" [ "Megafono:Domain", "d.test" ] |> errorsOf))

[<Fact>]
let ``T009 a bad previous master key is reported`` () =
    let errors = load "Production" (validOverrides @ [ "Megafono:MasterKeyPrevious", "short" ]) |> errorsOf
    Assert.True(mentions "MasterKeyPrevious" errors)

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("https://megafono.test")>]
[<InlineData("megafono.test/path")>]
[<InlineData("mega fono.test")>]
let ``T009 an empty or non-host Domain stops startup`` (domain: string) =
    let errors = load "Production" [ "Megafono:Domain", domain; "Megafono:MasterKey", masterKey ] |> errorsOf
    Assert.True(mentions "Domain" errors)

[<Theory>]
[<InlineData("Megafono")>]
[<InlineData("my app")>]
[<InlineData("my_app")>]
[<InlineData("")>]
let ``T009 a software name outside ^[a-z0-9-]+$ stops startup`` (name: string) =
    let errors = load "Production" (validOverrides @ [ "Megafono:Software:Name", name ]) |> errorsOf
    Assert.True(mentions "Software:Name" errors)

[<Fact>]
let ``R-13 AllowPrivateAddresses is refused in Production and accepted elsewhere`` () =
    let pairs = validOverrides @ [ "Megafono:Federation:AllowPrivateAddresses", "true" ]
    Assert.True(mentions "AllowPrivateAddresses" (load "Production" pairs |> errorsOf))
    Assert.True(mentions "AllowPrivateAddresses" (load "production" pairs |> errorsOf))
    Assert.True((load "Development" pairs).IsOk)
    Assert.True((load "Test" pairs).IsOk)

[<Fact>]
let ``T009 every problem is listed, not just the first`` () =
    let errors =
        load
            "Production"
            [ "Megafono:MasterKey", "bad"
              "Megafono:Software:Name", "Bad Name"
              "Megafono:Federation:AllowPrivateAddresses", "true"
              "Megafono:Ports:Federation", "abc" ]
        |> errorsOf

    for fragment in [ "Domain"; "MasterKey"; "Software:Name"; "AllowPrivateAddresses"; "Ports:Federation" ] do
        Assert.True(mentions fragment errors, $"missing a message about {fragment}")

    let message = Config.describeErrors errors
    Assert.StartsWith("Invalid Megafono configuration:", message)

[<Fact>]
let ``T009 numbers that do not parse, ports out of range and non-positive limits are errors`` () =
    Assert.True(mentions "Delivery:PerHost" (load "Production" (validOverrides @ [ "Megafono:Delivery:PerHost", "two" ]) |> errorsOf))
    Assert.True(mentions "Ports:Internal" (load "Production" (validOverrides @ [ "Megafono:Ports:Internal", "70000" ]) |> errorsOf))
    Assert.True(mentions "Delivery:Global" (load "Production" (validOverrides @ [ "Megafono:Delivery:Global", "0" ]) |> errorsOf))
    Assert.True(mentions "differ" (load "Production" (validOverrides @ [ "Megafono:Ports:Internal", "8080" ]) |> errorsOf))

[<Fact>]
let ``T009 buildApp refuses to start on invalid configuration, naming the problems`` () =
    let error = Assert.Throws<InvalidOperationException>(fun () -> Megafono.Program.buildApp [||] [ "Megafono:MasterKey", "bad" ] |> ignore)
    Assert.Contains("Megafono:Domain", error.Message)
    Assert.Contains("Megafono:MasterKey", error.Message)

[<Fact>]
let ``R-13 buildApp refuses the private switch under the Production environment`` () =
    let overrides = validOverrides @ [ "Megafono:Federation:AllowPrivateAddresses", "true" ]
    let error = Assert.Throws<InvalidOperationException>(fun () -> Megafono.Program.buildApp [| "--environment"; "Production" |] overrides |> ignore)
    Assert.Contains("AllowPrivateAddresses", error.Message)
    // Not Production: starts
    (Megafono.Program.buildApp [| "--environment"; "Development" |] overrides :> IDisposable).Dispose()
