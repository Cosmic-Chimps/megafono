/// What every inbox handler shares: the environment, the outcome type, JSON helpers, the "processed" marker and
/// the queueing of Accept/Reject replies
module Megafono.Federation.InboxCommon

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors

type Env =
    { DataSource: NpgsqlDataSource
      Time: TimeProvider
      Domain: string
      App: AppClient.AppClient
      Remote: RemoteActors.Env
      Log: ILogger }

type Outcome =
    /// Acted on it (or it was a harmless repeat); answer 202
    | Handled
    /// Verified but not for us or not acceptable: answer 202 and do nothing, so a sender is not told what we know
    | Ignored of reason: string
    /// A real refusal with this status
    | Refused of status: int

let str (node: JsonNode) (name: string) : string option =
    match node with
    | :? JsonObject as o ->
        match o[name] with
        | :? JsonValue as v ->
            match v.TryGetValue<string>() with
            | true, s -> Some s
            | _ -> None
        | _ -> None
    | _ -> None

/// A reference that is either an IRI string or an object with an `id`
let idOf (node: JsonNode) : string option =
    match node with
    | :? JsonValue as v ->
        match v.TryGetValue<string>() with
        | true, s -> Some s
        | _ -> None
    | :? JsonObject as o -> str o "id"
    | _ -> None

/// `https://{domain}/ap/actors/{id}` -> the app user id; anything else is not one of our actors
let localActorId (domain: string) (iri: string) : string option =
    let prefix = Ids.actorId domain ""

    if iri.StartsWith(prefix, StringComparison.Ordinal) then
        let id = iri.Substring prefix.Length
        if Ids.isValidAppUserId id then Some id else None
    else
        None

/// An activity id is only trusted when it is on the signer's own host (FR-015): otherwise a signer could use
/// someone else's ids to poison the 7-day duplicate table
let isOnSignerHost (signer: RemoteActor) (iri: string) : bool =
    match Uri.TryCreate(iri, UriKind.Absolute) with
    | true, uri -> RemoteActors.authority uri = signer.Host
    | _ -> false

let actorSummary (actor: RemoteActor) : Events.ActorSummary =
    { Id = actor.Id
      Handle = actor.Handle |> Option.map (fun h -> "@" + h) |> Option.defaultValue actor.Id
      Name = actor.Name |> Option.defaultValue (actor.Handle |> Option.defaultValue actor.Id)
      IconUrl = actor.IconUrl
      ProfileUrl = actor.ProfileUrl }

/// Records the inbound activity id inside the caller's transaction: false when it was already processed in the
/// last 7 days (FR-014). The row commits with whatever the activity changed, so a crash re-processes it.
let markProcessed (tx: NpgsqlTransaction) (time: TimeProvider) (activityId: string) : Task<bool> =
    task {
        use command =
            new NpgsqlCommand(
                "INSERT INTO processed_activities (activity_id, received_at) VALUES (@id, @now) ON CONFLICT (activity_id) DO NOTHING",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", activityId) |> ignore
        command.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
        let! inserted = command.ExecuteNonQueryAsync()
        return inserted = 1
    }

type Reply =
    | AcceptReply
    | RejectReply

/// Stores an Accept or Reject of an inbound Follow (own id, the full Follow embedded) and queues it for the
/// follower's inbox, in the caller's transaction
let queueReply (tx: NpgsqlTransaction) (time: TimeProvider) (domain: string) (reply: Reply) (localUser: string) (remote: RemoteActor) (followId: string) : Task =
    task {
        let activityGuid = Guid.NewGuid()
        let localActor = Ids.actorId domain localUser

        let followed: ActivityStreams.FollowRef =
            { Id = followId
              Actor = remote.Id
              Object = localActor }

        let activityId = Ids.activityId domain activityGuid

        let document, kind =
            match reply with
            | AcceptReply -> ActivityStreams.accept activityId localActor followed, "Accept"
            | RejectReply -> ActivityStreams.reject activityId localActor followed, "Reject"

        use insert =
            new NpgsqlCommand(
                $"""INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                    VALUES (@id, @actor, '{kind}', 'direct', @body, @now)""",
                tx.Connection,
                tx
            )

        insert.Parameters.AddWithValue("id", activityGuid) |> ignore
        insert.Parameters.AddWithValue("actor", localUser) |> ignore
        insert.Parameters.AddWithValue("body", ActivityStreams.toBytes document) |> ignore
        insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
        let! _ = insert.ExecuteNonQueryAsync()
        // The follower's own inbox: the reply is for one actor, not for a server's worth of followers
        let! _ = Outbox.enqueueDeliveries tx time activityGuid [ remote.Inbox ]
        ()
    }

/// The stored remote actor row for an id (follows reference it)
let loadRemote (tx: NpgsqlTransaction) (actorId: string) : Task<RemoteActor option> =
    task {
        use command =
            new NpgsqlCommand(
                """SELECT id, host, handle, actor_type, inbox, shared_inbox, followers_url, name, icon_url, profile_url, key_id, public_key_pem, fetched_at
                   FROM remote_actors WHERE id = @id""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", actorId) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if not (reader.Read()) then
            return None
        else
            let optional (i: int) = if reader.IsDBNull i then None else Some(reader.GetString i)

            return
                Some
                    { Id = reader.GetString 0
                      Host = reader.GetString 1
                      Handle = optional 2
                      ActorType = reader.GetString 3
                      Inbox = reader.GetString 4
                      SharedInbox = optional 5
                      FollowersUrl = optional 6
                      Name = optional 7
                      IconUrl = optional 8
                      ProfileUrl = optional 9
                      KeyId = reader.GetString 10
                      PublicKeyPem = reader.GetString 11
                      FetchedAt = reader.GetFieldValue<DateTimeOffset> 12 }
    }
