module Megafono.Tests.SmokeTests

open System.Net
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit

[<Fact>]
let ``health answers 200 on real Kestrel`` () : Task =
    task {
        let app = Megafono.Program.buildApp [||] (("urls", "http://127.0.0.1:0") :: TestSupport.validOverrides)
        do! app.StartAsync()

        try
            let address =
                app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                |> Seq.head

            use http = new HttpClient()
            let! response = http.GetAsync $"{address}/health"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        finally
            app.StopAsync().GetAwaiter().GetResult()
    }
