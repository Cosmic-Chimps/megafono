/// The instance actor's key (research R-02, FR-022): created once at first start, used to sign every
/// outbound request that is not made on behalf of a user (handle resolution, key refetch).
module Megafono.Infrastructure.InstanceKey

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Npgsql
open Megafono.Domain

type InstanceKey =
    { ActorId: string
      KeyId: string
      PublicKeyPem: string
      /// Shared by every signer; RSA instances are safe for concurrent signing
      PrivateKey: RSA }

type InstanceKeyProvider = { Get: unit -> Task<InstanceKey> }

let private readRow (dataSource: NpgsqlDataSource) =
    task {
        use command = dataSource.CreateCommand "SELECT public_key_pem, enc_private_key FROM instance_actor WHERE id = 1"
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            return Some(reader.GetString 0, reader.GetFieldValue<byte[]> 1)
        else
            return None
    }

/// Reads the single row, creating it first when missing. Concurrent starts converge on one key: the insert
/// does nothing when a row exists, and everyone reads the stored row back.
let private loadOrCreate (dataSource: NpgsqlDataSource) (keys: KeyVault.MasterKeys) (domain: string) (time: TimeProvider) =
    task {
        let actorId = Ids.instanceActorId domain

        match! readRow dataSource with
        | Some _ -> ()
        | None ->
            let created = KeyVault.createActorKey keys actorId
            use insert =
                dataSource.CreateCommand
                    "INSERT INTO instance_actor (id, public_key_pem, enc_private_key, created_at) VALUES (1, @pem, @enc, @now) ON CONFLICT (id) DO NOTHING"

            insert.Parameters.AddWithValue("pem", created.PublicKeyPem) |> ignore
            insert.Parameters.AddWithValue("enc", created.EncryptedPrivateKey) |> ignore
            insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
            let! _ = insert.ExecuteNonQueryAsync()
            ()

        match! readRow dataSource with
        | Some(pem, encrypted) ->
            return
                { ActorId = actorId
                  KeyId = Ids.instanceActorKeyId domain
                  PublicKeyPem = pem
                  PrivateKey = KeyVault.loadPrivateKey keys actorId encrypted }
        | None -> return failwith "instance_actor row is missing right after it was created"
    }

/// Loads the key once and caches it; a failed load is retried on the next call
let create (dataSource: NpgsqlDataSource) (keys: KeyVault.MasterKeys) (domain: string) (time: TimeProvider) : InstanceKeyProvider =
    let gate = obj ()
    let mutable cached: Task<InstanceKey> = null

    let get () =
        lock gate (fun () ->
            if isNull cached || cached.IsFaulted || cached.IsCanceled then
                cached <- loadOrCreate dataSource keys domain time

            cached)

    { Get = get }

/// Makes sure the row exists before the app starts serving
let hostedService (provider: InstanceKeyProvider) : IHostedService =
    { new IHostedService with
        member _.StartAsync _ = provider.Get() :> Task
        member _.StopAsync _ = Task.CompletedTask }
