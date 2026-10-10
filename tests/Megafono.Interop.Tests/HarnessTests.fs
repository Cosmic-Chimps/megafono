/// Smoke tests for the interop harness (R-15, T006/T008): they prove the TLS network, the GoToSocial
/// users and tokens, the host exposure and the `FediClient` work before any federation test relies on them.
module Megafono.Interop.Tests.HarnessTests

open System
open System.Linq
open DotNet.Testcontainers.Builders
open Xunit
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

/// The `FediClient` surface, exercised between two local users of one server (both servers pass it):
/// locked account, follow request, approve, reject, home timeline, status fetch, unfollow.
let localFollowFlow (server: IFediServer) =
    task {
        use! alice = server.CreateUser "alice"
        use! bob = server.CreateUser "bob"
        use! carol = server.CreateUser "carol"
        let! aliceAccount = alice.Me()
        let! bobAccount = bob.Me()
        let! carolAccount = carol.Me()
        Assert.Equal(alice.Username, aliceAccount.Username)
        Assert.Equal($"https://{server.Domain}/@{alice.Username}", aliceAccount.Url)

        let! locked = alice.LockAccount()
        Assert.True locked.Locked

        // Search by handle finds the local account; ids are the same for everyone on the server.
        let! found = bob.SearchAccount(alice.Handle, true)
        let found = Assert.IsType<Account>(found.Value)
        Assert.Equal(aliceAccount.Id, found.Id)

        // A follow of a locked account waits for approval.
        let! pending = bob.Follow aliceAccount.Id
        Assert.True(pending.Requested && not pending.Following)
        let! _ = carol.Follow aliceAccount.Id
        let! requests =
            pollFor "follow requests from bob and carol" (fun () ->
                task {
                    let! rs = alice.FollowRequests()
                    return (if rs.Length >= 2 then Some rs else None)
                })
        Assert.Contains(requests, fun a -> a.Id = bobAccount.Id)

        let! approved = alice.AuthorizeFollowRequest bobAccount.Id
        Assert.True approved.FollowedBy
        let! _ = alice.RejectFollowRequest carolAccount.Id
        let! bobSees =
            pollFor "bob follows alice" (fun () ->
                task {
                    let! r = bob.Relationship aliceAccount.Id
                    return (if r.Following then Some r else None)
                })
        Assert.False bobSees.Requested
        let! carolSees = carol.Relationship aliceAccount.Id
        Assert.False(carolSees.Following || carolSees.Requested)

        // A public post reaches the follower's home timeline and can be fetched by id.
        let text = $"hello from the harness {Guid.NewGuid():N}"
        let! posted = alice.PostStatus("public", text)
        Assert.Equal("public", posted.Visibility)
        let! seen =
            pollFor "alice's post in bob's home timeline" (fun () ->
                task {
                    let! timeline = bob.HomeTimeline()
                    return timeline |> List.tryFind (fun s -> s.Uri = posted.Uri)
                })
        Assert.Contains(text, seen.Content)
        let! fetched = bob.GetStatus seen.Id
        Assert.Equal(posted.Uri, fetched.Uri)

        let! unfollowed = bob.Unfollow aliceAccount.Id
        Assert.False unfollowed.Following
        let! rels = bob.Relationships [ aliceAccount.Id; carolAccount.Id ]
        Assert.True(rels |> List.forall (fun r -> not r.Following))
    }

type HarnessTests(net: FederationNetwork) =

    [<Fact>]
    member _.``the user token is accepted by verify_credentials`` () =
        task {
            let! gts = net.GoToSocial()
            use! alice = gts.CreateUser "alice"
            let! me = alice.Me()
            Assert.Equal(alice.Username, me.Username)
            Assert.Equal($"https://{GtsDomain}/@{alice.Username}", me.Url)
        }

    [<Fact>]
    member _.``a container on the network reaches the in-process host over verified TLS`` () =
        task {
            // A plain curl container that trusts the test CA stands in for "a peer on the network".
            let curl =
                ContainerBuilder(CurlImage)
                    .WithNetwork(net.Network)
                    .WithEntrypoint("sleep", "infinity")
                    .WithResourceMapping(bytes net.Pki.CaPem, "/certs/ca.pem")
                    .Build()
            do! curl.StartAsync()
            try
                use! probe = net.AttachProbe()
                let! result =
                    curl.ExecAsync [| "curl"; "-sS"; "--fail"; "--cacert"; "/certs/ca.pem"; $"https://{MegafonoDomain}/probe.json" |]
                Assert.True((result.ExitCode = Nullable 0L), $"curl failed: {result.Stderr}")
                Assert.Equal("""{"ok":true}""", result.Stdout.Trim())
                let seen = probe.Seen.Single(fun r -> r.PathAndQuery = "/probe.json")
                Assert.Equal(MegafonoDomain, seen.Host)

                // And it does not trust anything else: without the CA the handshake must fail.
                let! untrusted = curl.ExecAsync [| "curl"; "-sS"; "--fail"; $"https://{MegafonoDomain}/probe.json" |]
                Assert.NotEqual(Nullable 0L, untrusted.ExitCode)
            finally
                curl.DisposeAsync().AsTask().Wait()
        }

    [<Fact>]
    member _.``GoToSocial dials the in-process host through the megafono.test front`` () =
        task {
            let! gts = net.GoToSocial()
            use! alice = gts.CreateUser "alice"
            use! probe = net.AttachProbe()
            let! _ = alice.SearchAccount($"nobody@{MegafonoDomain}", true)
            let! seen =
                pollUntil (TimeSpan.FromSeconds 30.0) "GoToSocial to request webfinger on the host" (fun () ->
                    task {
                        return
                            probe.Seen
                            |> Seq.tryFind (fun r -> r.PathAndQuery.StartsWith "/.well-known/webfinger" && r.Host = MegafonoDomain)
                    })
            Assert.Contains($"acct:nobody@{MegafonoDomain}", Uri.UnescapeDataString seen.PathAndQuery)
            Assert.Contains("gotosocial", seen.UserAgent, StringComparison.OrdinalIgnoreCase)
        }

    [<Fact>]
    member _.``the FediClient flows work between two local GoToSocial users`` () =
        task {
            let! gts = net.GoToSocial()
            do! localFollowFlow gts
        }
