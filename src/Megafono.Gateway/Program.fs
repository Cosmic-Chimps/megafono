/// The reference gateway (FR-063, research R-14): the only public service. It splits traffic between Megáfono and
/// the app by path and touches nothing else: the original `Host`, path, query and body reach Megáfono unchanged,
/// because HTTP signatures cover them. Behind a TLS-terminating proxy, `Gateway:TrustedProxies` makes it believe that
/// proxy's `X-Forwarded-For` and `X-Forwarded-Proto` (see `trustedProxies`). Routes and destinations live in the
/// `ReverseProxy` section of appsettings.json.
module Megafono.Gateway.Program

open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.HttpOverrides
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

// Both System.Net and Microsoft.AspNetCore.HttpOverrides define an IPNetwork; the latter is obsolete
type private Network = System.Net.IPNetwork

/// An address or CIDR range; a single address is a /32 or /128 network
let private parseNetwork (entry: string) : Network =
    match Network.TryParse entry with
    | true, network -> network
    | _ ->
        match IPAddress.TryParse entry with
        | true, address ->
            Network(address, (if address.AddressFamily = Sockets.AddressFamily.InterNetwork then 32 else 128))
        | _ -> failwith $"Gateway:TrustedProxies has an entry that is neither an IP address nor a CIDR range: '{entry}'"

/// `Gateway:TrustedProxies`: addresses or CIDR ranges, comma separated (`Gateway__TrustedProxies=a,b`) or indexed
/// (`Gateway__TrustedProxies__0=a`), the same forms as `Megafono:TrustedProxies`
let private trustedProxies (config: IConfiguration) : Network list =
    let section = config.GetSection "Gateway"
    let own = defaultArg (Option.ofObj section["TrustedProxies"]) ""
    let indexed = section.GetSection("TrustedProxies").GetChildren() |> Seq.choose (fun c -> Option.ofObj c.Value)

    Seq.append (own.Split ',') indexed
    |> Seq.map (fun s -> s.Trim())
    |> Seq.filter (fun s -> s <> "")
    |> Seq.distinct
    |> Seq.map parseNetwork
    |> List.ofSeq

/// Builds the gateway without running it, so tests can host it. Overrides win over every other configuration source.
let buildGateway (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    builder.Configuration.AddInMemoryCollection(overrides |> List.map (fun (k, v) -> System.Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    // With a certificate configured the gateway terminates TLS on 443; otherwise the listen addresses come from
    // the usual `urls` setting (tests use plain http on port 0)
    match builder.Configuration["Kestrel:Certificates:Default:Path"] with
    | null
    | "" -> ()
    | _ ->
        builder.WebHost.ConfigureKestrel(fun (kestrel: KestrelServerOptions) ->
            kestrel.Listen(IPAddress.Any, 443, fun listen -> listen.UseHttps() |> ignore))
        |> ignore

    builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection "ReverseProxy") |> ignore

    // Behind a TLS-terminating proxy YARP would replace `X-Forwarded-For` with the proxy's address, and every client
    // would share one rate limit bucket at Megáfono (FR-016, R-12). With trusted proxies the client address and the
    // scheme are taken from the proxy's headers instead, and YARP's own X-Forwarded-* transform then passes them on.
    // Not `X-Forwarded-Host`: the original Host stays untouched, signatures cover it. No forward limit: a chain of
    // trusted proxies is walked and the right-most address that is not a trusted proxy wins, as in Megáfono.
    let proxies = trustedProxies builder.Configuration

    if not proxies.IsEmpty then
        builder.Services.Configure<ForwardedHeadersOptions>(fun (options: ForwardedHeadersOptions) ->
            options.ForwardedHeaders <- ForwardedHeaders.XForwardedFor ||| ForwardedHeaders.XForwardedProto
            options.ForwardLimit <- System.Nullable()
            options.KnownProxies.Clear()
            options.KnownIPNetworks.Clear()

            for network in proxies do
                options.KnownIPNetworks.Add network)
        |> ignore

    let app = builder.Build()
    if not proxies.IsEmpty then app.UseForwardedHeaders() |> ignore
    app.MapReverseProxy() |> ignore
    app

[<EntryPoint>]
let main argv =
    let app = buildGateway argv []
    app.Run()
    0
