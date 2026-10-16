/// A remote actor blocking one of our users (specs/004-blocks User Story 4, FR-011, FR-012, research R-49): the `Block`
/// ends the follows between the two, both ways, and the app hears of each. Nothing is sent back and nothing is kept.
module Megafono.Tests.Integration.InboundBlockTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private sharedInbox (rig: Rig) = rig.Host.FederationUrl + "/inbox"
let private personalInbox (rig: Rig) (user: string) = $"{rig.Host.FederationUrl}/ap/actors/{user}/inbox"

let private send (rig: Rig) (name: string) (url: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name url body
        return response.StatusCode
    }

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private context = "\"@context\":\"https://www.w3.org/ns/activitystreams\""

/// A Block by `name` of `target`; `suffix` makes the activity id unique
let private blockBody (rig: Rig) (name: string) (suffix: string) (target: string) : string =
    $"""{{{context},"id":"{rig.Stub.ActorId name}/blocks/{suffix}","type":"Block","actor":"{rig.Stub.ActorId name}","object":"{target}"}}"""

let private undoBlockBody (rig: Rig) (name: string) (suffix: string) (target: string) : string =
    $"""{{{context},"id":"{rig.Stub.ActorId name}/undo/{suffix}","type":"Undo","actor":"{rig.Stub.ActorId name}","object":{{"id":"{rig.Stub.ActorId name}/blocks/{suffix}","type":"Block","actor":"{rig.Stub.ActorId name}","object":"{target}"}}}}"""

let private outboundFollow (rig: Rig) (user: string) (name: string) (status: string) : Task =
    Db.execute
        rig.Db
        $"""INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
            VALUES (gen_random_uuid(), 'outbound', '{user}', '{rig.Stub.ActorId name}', 'https://megafono.test/ap/activities/{Guid.NewGuid()}', '{status}')"""

/// "direction:user:actor:status" of every follow, sorted: one string to compare before and after
let private follows (rig: Rig) : Task<string list> =
    task {
        let! rows =
            Publishing.strings rig.Db "SELECT direction || ':' || local_actor || ':' || remote_actor || ':' || status FROM follows"

        return List.sort rows
    }

/// alex (u-1) and bob follow each other, bea (u-2) and bob too, carol follows alex
let private seed (rig: Rig) : Task =
    task {
        do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
        do! outboundFollow rig "u-1" "bob" "accepted"
        do! addFollower rig.Db "u-2" rig.Stub "bob" "accepted" None
        do! outboundFollow rig "u-2" "bob" "pending"
        do! addFollower rig.Db "u-1" rig.Stub "carol" "accepted" None
    }

type InboundBlockTests(pg: PostgresFixture) =

    [<Theory>]
    [<InlineData("shared")>]
    [<InlineData("personal")>]
    member _.``FR-011 FR-012 a Block of the user by a remote actor ends the follows both ways, one follow.removed each, nothing sent``(inbox: string) : Task =
        task {
            let! rig = start pg [ "bob"; "carol" ]
            use _ = disposeRig rig
            do! seed rig
            let url = if inbox = "shared" then sharedInbox rig else personalInbox rig "u-1"

            let! status = send rig "bob" url (blockBody rig "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)

            let bob, carol = rig.Stub.ActorId "bob", rig.Stub.ActorId "carol"

            // alex and bob: both rows removed. bea's rows with bob and carol's follow of alex are not alex-and-bob's
            let! after = follows rig

            Assert.Equal<string list>(
                List.sort
                    [ $"inbound:u-1:{bob}:removed"
                      $"outbound:u-1:{bob}:removed"
                      $"inbound:u-2:{bob}:accepted"
                      $"outbound:u-2:{bob}:pending"
                      $"inbound:u-1:{carol}:accepted" ],
                after
            )

            do! waitForEvents rig "follow.removed" 2
            do! settle ()
            let removed = events rig "follow.removed"
            Assert.Equal(2, removed.Length)

            for e in removed do
                Assert.Equal("u-1", jstr (at e.Json "user.id"))
                Assert.Equal(bob, jstr (at e.Json "actor.id"))
                Assert.Equal("removed", jstr (at e.Json "follow.status"))

            Assert.Equal<string list>([ "inbound"; "outbound" ], removed |> List.map (fun e -> jstr (at e.Json "follow.direction")) |> List.sort)
            let! ids = Publishing.strings rig.Db $"SELECT id::text FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{bob}'"
            Assert.Equal<string list>(List.sort ids, removed |> List.map (fun e -> jstr (at e.Json "follow.id")) |> List.sort)

            // Nothing goes back to bob: no Reject, no Undo, no delivery at all, and no record of his block is kept
            let! deliveries = Db.count rig.Db "SELECT count(*) FROM deliveries"
            Assert.Equal(0L, deliveries)
            let! blocks = Db.count rig.Db "SELECT count(*) FROM blocks"
            Assert.Equal(0L, blocks)
            Assert.Empty rig.Stub.Posts
        }

    [<Fact>]
    member _.``FR-011 a Block when there is a follow in one direction only ends that one; with none it changes nothing``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "pending" None

            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "follow.removed" 1
            let! after = follows rig
            let bob = rig.Stub.ActorId "bob"
            Assert.Equal<string list>([ $"inbound:u-1:{bob}:removed" ], after)

            // bea has no relationship with bob: a Block of her is accepted and changes nothing
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "2" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Equal(1, (events rig "follow.removed").Length)
        }

    [<Fact>]
    member _.``FR-011 a later Follow from the blocker is not refused by Megáfono, which keeps no record of the remote block``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! seed rig

            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "follow.removed" 2

            // His own server refuses it while his block lasts; if it reaches us it is an ordinary Follow
            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "2" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let bob = rig.Stub.ActorId "bob"
            let! status = Db.scalar rig.Db $"SELECT status FROM follows WHERE direction = 'inbound' AND local_actor = 'u-1' AND remote_actor = '{bob}'"
            Assert.NotEqual<string>("removed", string status)
        }

    [<Fact>]
    member _.``FR-012 the same Block again changes nothing and the app hears nothing more``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! seed rig
            let block = blockBody rig "bob" "1" (ourActor "u-1")

            let! status = send rig "bob" (sharedInbox rig) block
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "follow.removed" 2

            // The follow comes back (an accepted row again), and the activity is replayed: its id was processed
            let bob = rig.Stub.ActorId "bob"
            do! Db.execute rig.Db $"UPDATE follows SET status = 'accepted' WHERE local_actor = 'u-1' AND remote_actor = '{bob}'"
            let! status = send rig "bob" (sharedInbox rig) block
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Equal(2, (events rig "follow.removed").Length)
            let! v = Db.count rig.Db $"SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{bob}' AND status = 'accepted'"
            Assert.Equal(2L, v)
        }

    [<Fact>]
    member _.``FR-011 a Block that is not by its signer, of another server's actor, of an unknown or a deleted actor is 202 and changes nothing``() : Task =
        task {
            let! rig = start pg [ "bob"; "carol" ]
            use _ = disposeRig rig
            do! seed rig
            do! addFollower rig.Db "u-2" rig.Stub "carol" "accepted" None
            let! before = follows rig

            // bob's request, but the Block says carol blocks alex
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "carol" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            // an object that is not one of ours, then one id we never had
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "2" "https://other.example/users/x")
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "3" (ourActor "nobody"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            // the activity id is on someone else's host
            let bob, alex = rig.Stub.ActorId "bob", ourActor "u-1"
            let foreign = $"""{{{context},"id":"https://other.example/blocks/1","type":"Block","actor":"{bob}","object":"{alex}"}}"""
            let! status = send rig "bob" (sharedInbox rig) foreign
            Assert.Equal(HttpStatusCode.Accepted, status)
            // no id at all, and an object that is neither an id nor an object
            let! status = send rig "bob" (sharedInbox rig) $"""{{{context},"type":"Block","actor":"{bob}","object":"{alex}"}}"""
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = send rig "bob" (sharedInbox rig) $"""{{{context},"id":"{bob}/blocks/4","type":"Block","actor":"{bob}","object":42}}"""
            Assert.Equal(HttpStatusCode.Accepted, status)

            // bea's account is deleted: the Block of it is dropped, and her rows are not touched by it
            let! status, _ = sendInternal rig HttpMethod.Delete "/internal/actors/u-2" None
            Assert.Equal(202, status)
            let! afterDelete = follows rig
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "5" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = send rig "bob" (personalInbox rig "u-2") (blockBody rig "bob" "6" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Gone, status)

            do! settle ()
            let! after = follows rig
            Assert.Equal<string list>(afterDelete, after)
            // Only deleting bea's account may have changed anything, and the Blocks of alex did not
            let alexs (rows: string list) = rows |> List.filter (fun r -> r.Contains ":u-1:")
            Assert.Equal<string list>(alexs before, alexs after)
            Assert.Empty(events rig "follow.removed" |> List.filter (fun e -> jstr (at e.Json "user.id") = "u-1"))
        }

    [<Fact>]
    member _.``FR-011 an Undo of a Block is 202 and restores nothing``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! seed rig

            // Undo with no Block before it: accepted follows stay accepted
            let! before = follows rig
            let! status = send rig "bob" (sharedInbox rig) (undoBlockBody rig "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = send rig "bob" (personalInbox rig "u-1") (undoBlockBody rig "bob" "2" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! after = follows rig
            Assert.Equal<string list>(before, after)

            // After a Block ended the follows, its Undo does not bring them back
            let! status = send rig "bob" (sharedInbox rig) (blockBody rig "bob" "3" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "follow.removed" 2
            let! blocked = follows rig
            let! status = send rig "bob" (sharedInbox rig) (undoBlockBody rig "bob" "3" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            let! lifted = follows rig
            Assert.Equal<string list>(blocked, lifted)
            Assert.Equal(2, (events rig "follow.removed").Length)
            Assert.Empty(events rig "follow.accepted")
            Assert.Empty rig.Stub.Posts
        }
