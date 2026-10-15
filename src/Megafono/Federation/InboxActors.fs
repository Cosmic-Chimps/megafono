/// Update{Person} from a remote actor (FR-013, T050): the profile it carries is stored, and every local user related
/// to that actor hears `actor.updated`.
///
/// The embedded actor is used when it passes the same checks as a fetched one (its id is the signer, its key belongs
/// to it, it is on the signer's host): it is the actor's own statement under its own key, and FR-015 trusts an object
/// on the signing key's host. Fetching the origin instead proved wrong in practice: Mastodon serves its cached,
/// pre-update actor document for a few minutes after sending the Update (verified in US4, T085). The origin is only
/// the fallback, for an embedded copy that does not validate.
module Megafono.Federation.InboxActors

open System
open System.Text.Json
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
            let! stored =
                task {
                    let embedded =
                        match activity["object"] with
                        | :? JsonObject as o ->
                            try
                                use document = JsonDocument.Parse(o.ToJsonString())
                                RemoteActors.parseActor env.Remote.AllowPrivateAddresses (env.Time.GetUtcNow()) (Uri signer.Id) (Some signer.KeyId) document.RootElement
                                |> Result.toOption
                            with _ ->
                                None
                        | _ -> None

                    match embedded with
                    | Some actor when actor.Id = signer.Id ->
                        match! RemoteActors.storeActor env.Remote actor with
                        | Ok saved -> return Ok saved
                        | Error _ -> return! RemoteActors.refreshActor env.Remote signer.Id
                    | _ -> return! RemoteActors.refreshActor env.Remote signer.Id
                }

            match stored with
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
