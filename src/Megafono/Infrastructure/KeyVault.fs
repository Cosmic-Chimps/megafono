/// Actor key pairs and their encryption at rest (research R-11, constitution Principle III)
module Megafono.Infrastructure.KeyVault

open System
open System.Security.Cryptography
open System.Text

/// Blob layout: [keyVersion:1][nonce:12][tag:16][ciphertext]
let private blobVersion = 1uy
let private nonceSize = 12
let private tagSize = 16
let private headerSize = 1 + nonceSize + tagSize

/// The configured master keys: new data is always encrypted with `Current`; `Previous` only decrypts,
/// so a rotation can roll out before every row is re-encrypted.
type MasterKeys =
    { Current: byte[]
      Previous: byte[] option }

let masterKeys (current: byte[]) (previous: byte[] option) : MasterKeys =
    let check (key: byte[]) =
        if key.Length <> 32 then
            invalidArg (nameof key) "A master key is exactly 32 bytes"

    check current
    previous |> Option.iter check
    { Current = current; Previous = previous }

/// A fresh RSA-2048 key pair; the caller disposes it
let generateRsa () : RSA = RSA.Create 2048

let exportPublicPem (rsa: RSA) : string = rsa.ExportSubjectPublicKeyInfoPem()

let importPublicPem (pem: string) : RSA =
    let rsa = RSA.Create()
    rsa.ImportFromPem pem
    rsa

// The actor id is the associated data: a ciphertext copied to another actor's row no longer authenticates
let private associatedData (actorId: string) = Encoding.UTF8.GetBytes actorId

let private tryDecryptWith (key: byte[]) (actorId: string) (blob: byte[]) : byte[] option =
    let nonce = ReadOnlySpan<byte>(blob, 1, nonceSize)
    let tag = ReadOnlySpan<byte>(blob, 1 + nonceSize, tagSize)
    let cipher = ReadOnlySpan<byte>(blob, headerSize, blob.Length - headerSize)
    let plain = Array.zeroCreate<byte> cipher.Length
    use aes = new AesGcm(key, tagSize)

    try
        aes.Decrypt(nonce, cipher, tag, Span<byte> plain, ReadOnlySpan<byte>(associatedData actorId))
        Some plain
    with :? AuthenticationTagMismatchException ->
        None

/// AES-256-GCM under the current master key, bound to `actorId`
let encrypt (keys: MasterKeys) (actorId: string) (plain: byte[]) : byte[] =
    let blob = Array.zeroCreate<byte> (headerSize + plain.Length)
    let nonce = Span<byte>(blob, 1, nonceSize)
    let tag = Span<byte>(blob, 1 + nonceSize, tagSize)
    let cipher = Span<byte>(blob, headerSize, plain.Length)
    RandomNumberGenerator.Fill nonce
    blob[0] <- blobVersion
    use aes = new AesGcm(keys.Current, tagSize)
    aes.Encrypt(nonce, ReadOnlySpan<byte>(plain), cipher, tag, ReadOnlySpan<byte>(associatedData actorId))
    blob

/// Decrypts with the current key, then the previous one. Throws CryptographicException when the blob is
/// malformed, was made for another actor, or neither key opens it.
let decrypt (keys: MasterKeys) (actorId: string) (blob: byte[]) : byte[] =
    if isNull blob || blob.Length < headerSize || blob[0] <> blobVersion then
        raise (CryptographicException "Unrecognized encrypted key blob")

    let candidates = keys.Current :: Option.toList keys.Previous

    match candidates |> List.tryPick (fun key -> tryDecryptWith key actorId blob) with
    | Some plain -> plain
    | None -> raise (CryptographicException "The encrypted key does not open with the configured master keys for this actor")

/// A key pair ready to store: the public half as PEM, the private half encrypted
type NewActorKey =
    { PublicKeyPem: string
      EncryptedPrivateKey: byte[] }

/// Generates the key for an actor (or the instance actor) once; rotation is a separate, explicit operation
let createActorKey (keys: MasterKeys) (actorId: string) : NewActorKey =
    use rsa = generateRsa ()

    { PublicKeyPem = exportPublicPem rsa
      EncryptedPrivateKey = encrypt keys actorId (rsa.ExportPkcs8PrivateKey()) }

/// Decrypts and imports the private key; the caller disposes the result
let loadPrivateKey (keys: MasterKeys) (actorId: string) (blob: byte[]) : RSA =
    let der = decrypt keys actorId blob
    let rsa = RSA.Create()
    let mutable bytesRead = 0

    try
        rsa.ImportPkcs8PrivateKey(ReadOnlySpan<byte> der, &bytesRead)
        rsa
    with _ ->
        rsa.Dispose()
        reraise ()
