/// Test-mode capture of verified inbound requests (T085, constitution Principle VI): with
/// `Megafono:Testing:CaptureInbound=<dir>` the inbox writes one JSON file per request that passed signature
/// verification, so real GoToSocial and Mastodon payloads can be kept as fixtures. Refused in Production.
module Megafono.Infrastructure.InboundCapture

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Megafono.Infrastructure.HttpSignatures
open Megafono.Infrastructure.RemoteActors

/// Writes `request` (method, target, headers, raw body) and the key it verified with. Never throws: a capture that
/// fails must not fail the request.
let write (directory: string) (now: DateTimeOffset) (request: SignedRequest) (signer: RemoteActor) (activityType: string) : unit =
    try
        Directory.CreateDirectory directory |> ignore
        let doc = JsonObject()
        doc["capturedAt"] <- JsonValue.Create(now.UtcDateTime.ToString("O"))
        doc["activityType"] <- JsonValue.Create activityType
        doc["method"] <- JsonValue.Create request.Method
        doc["pathAndQuery"] <- JsonValue.Create request.PathAndQuery

        let headers = JsonArray()

        for name, value in request.Headers do
            let h = JsonObject()
            h["name"] <- JsonValue.Create name
            h["value"] <- JsonValue.Create value
            headers.Add h

        doc["headers"] <- headers
        doc["bodyBase64"] <- JsonValue.Create(Convert.ToBase64String request.Body)
        // Convenience for reading a capture by eye; the base64 form is the exact bytes
        doc["body"] <- JsonValue.Create(Encoding.UTF8.GetString request.Body)

        let key = JsonObject()
        key["actorId"] <- JsonValue.Create signer.Id
        key["keyId"] <- JsonValue.Create signer.KeyId
        key["publicKeyPem"] <- JsonValue.Create signer.PublicKeyPem
        doc["signer"] <- key

        let name = $"{now.UtcDateTime:yyyyMMddTHHmmssfff}-{activityType}-{Guid.NewGuid():N}.json"
        File.WriteAllText(Path.Combine(directory, name), doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)))
    with _ ->
        ()
