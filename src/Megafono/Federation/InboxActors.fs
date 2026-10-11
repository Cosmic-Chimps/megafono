/// Update{Person} from a remote actor (FR-013, T050): the profile is fetched again from its origin, and every
/// local user related to that actor hears `actor.updated`
module Megafono.Federation.InboxActors

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let handleUpdate (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let activityId = str activity "id"
        let actor = idOf activity["actor"]
        // Only the signer's own profile: an Update whose object is someone else is not ours to apply
        let objectId = idOf activity["object"]

        match activityId, actor, objectId with
        | Some updateId, Some actorId, Some subject when actorId = signer.Id && subject = signer.Id && isOnSignerHost signer updateId ->
            // The embedded copy is not trusted: what the origin serves is what we store
            match! RemoteActors.refreshActor env.Remote signer.Id with
            | Error _ -> return Refused 502
            | Ok refreshed ->
                use! connection = env.DataSource.OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()
                let! fresh = markProcessed tx env.Time updateId

                if not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "already processed"
                else
                    use related =
                        new NpgsqlCommand("SELECT DISTINCT local_actor FROM follows WHERE remote_actor = @remote AND status = 'accepted'", connection, tx)

                    related.Parameters.AddWithValue("remote", refreshed.Id) |> ignore
                    use! reader = related.ExecuteReaderAsync()
                    let users = ResizeArray<string>()

                    while reader.Read() do
                        users.Add(reader.GetString 0)

                    do! reader.CloseAsync()

                    for user in users do
                        let body: Events.EventBody =
                            { Type = Events.ActorUpdated
                              Actor = Some(actorSummary refreshed)
                              Follow = None
                              Extra = [] }

                        let! _ = Outbox.enqueueEvent tx env.Time user body
                        ()

                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Update is not the signer's own profile"
    }
