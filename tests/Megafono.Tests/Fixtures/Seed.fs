/// Rows tests need before the code under test runs
module Megafono.Tests.Fixtures.Seed

open System
open System.Text
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Tests.TestSupport

let domain = "megafono.test"
let keys = KeyVault.masterKeys masterKeyBytes None

/// A local actor with a fresh key pair; returns its public key PEM
let localActor (dataSource: NpgsqlDataSource) (id: string) (handle: string) : Task<string> =
    task {
        let key = KeyVault.createActorKey keys (Ids.actorId domain id)

        use command =
            dataSource.CreateCommand
                """INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key)
                   VALUES (@id, @handle, @handle, 'https://app.example/u', @pem, @enc)"""

        command.Parameters.AddWithValue("id", id) |> ignore
        command.Parameters.AddWithValue("handle", handle) |> ignore
        command.Parameters.AddWithValue("pem", key.PublicKeyPem) |> ignore
        command.Parameters.AddWithValue("enc", key.EncryptedPrivateKey) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        return key.PublicKeyPem
    }

/// A Create activity by `author`; returns its id and the exact stored bytes
let activity (dataSource: NpgsqlDataSource) (author: string) : Task<Guid * byte[]> =
    task {
        let id = Guid.NewGuid()

        let body =
            Encoding.UTF8.GetBytes
                $"""{{"id":"{Ids.activityId domain id}","type":"Create","actor":"{Ids.actorId domain author}","object":{{"type":"Note","content":"hi"}}}}"""

        use command =
            dataSource.CreateCommand
                "INSERT INTO activities (id, local_actor, activity_type, audience, body) VALUES (@id, @author, 'Create', 'public', @body)"

        command.Parameters.AddWithValue("id", id) |> ignore
        command.Parameters.AddWithValue("author", author) |> ignore
        command.Parameters.AddWithValue("body", body) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        return id, body
    }

/// Queues deliveries the way a publish does: in a transaction of their own
let enqueueDeliveries (dataSource: NpgsqlDataSource) (time: TimeProvider) (activityId: Guid) (inboxes: string list) : Task<int> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! inserted = Outbox.enqueueDeliveries tx time activityId inboxes
        do! tx.CommitAsync()
        return inserted
    }

let enqueueEvent (dataSource: NpgsqlDataSource) (time: TimeProvider) (userId: string) (body: Events.EventBody) : Task<string> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! id = Outbox.enqueueEvent tx time userId body
        do! tx.CommitAsync()
        return id
    }

let sampleActor (name: string) : Events.ActorSummary =
    { Id = $"https://remote.example/users/{name}"
      Handle = $"@{name}@remote.example"
      Name = name
      IconUrl = None
      ProfileUrl = Some $"https://remote.example/@{name}" }

let followRequested (name: string) : Events.EventBody =
    { Type = Events.FollowRequested
      Actor = Some(sampleActor name)
      Follow = None
      Extra = [] }

/// An accepted (or other status) inbound follow of `localUser` by a remote actor, with its inboxes
let remoteFollower
    (dataSource: NpgsqlDataSource)
    (localUser: string)
    (actorId: string)
    (inbox: string)
    (sharedInbox: string option)
    (status: string)
    : Task =
    task {
        let host = Uri(actorId).Authority

        use command =
            dataSource.CreateCommand
                """INSERT INTO remote_actors (id, host, actor_type, inbox, shared_inbox, key_id, public_key_pem)
                   VALUES (@id, @host, 'Person', @inbox, @shared, @keyid, 'pem')
                   ON CONFLICT (id) DO NOTHING;
                   INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
                   VALUES (gen_random_uuid(), 'inbound', @local, @id, @follow, @status)"""

        command.Parameters.AddWithValue("id", actorId) |> ignore
        command.Parameters.AddWithValue("host", host) |> ignore
        command.Parameters.AddWithValue("inbox", inbox) |> ignore
        command.Parameters.AddWithValue("shared", (match sharedInbox with Some s -> box s | None -> box DBNull.Value)) |> ignore
        command.Parameters.AddWithValue("keyid", actorId + "#main-key") |> ignore
        command.Parameters.AddWithValue("local", localUser) |> ignore
        command.Parameters.AddWithValue("follow", actorId + "/follows/1") |> ignore
        command.Parameters.AddWithValue("status", status) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }
