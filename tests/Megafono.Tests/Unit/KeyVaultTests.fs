module Megafono.Tests.Unit.KeyVaultTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open Megafono.Infrastructure

let private keyA = Array.init 32 byte
let private keyB = Array.init 32 (fun i -> byte (255 - i))
let private vault = KeyVault.masterKeys keyA None
let private actor = "https://megafono.test/ap/actors/u1"

[<Fact>]
let ``R-11 round trip returns the plaintext`` () =
    let plain = Encoding.UTF8.GetBytes "private key bytes"
    let blob = KeyVault.encrypt vault actor plain
    Assert.Equal<byte[]>(plain, KeyVault.decrypt vault actor blob)

[<Fact>]
let ``R-11 blob layout is version, 12 byte nonce, 16 byte tag, ciphertext`` () =
    let plain = Array.zeroCreate<byte> 100
    let blob = KeyVault.encrypt vault actor plain
    Assert.Equal(1uy, blob[0])
    Assert.Equal(1 + 12 + 16 + 100, blob.Length)

[<Fact>]
let ``R-11 every encryption uses a fresh nonce`` () =
    let plain = Array.zeroCreate<byte> 32
    let a = KeyVault.encrypt vault actor plain
    let b = KeyVault.encrypt vault actor plain
    Assert.NotEqual<byte[]>(a, b)

[<Fact>]
let ``R-11 a blob moved to another actor does not decrypt`` () =
    let blob = KeyVault.encrypt vault actor [| 1uy; 2uy; 3uy |]
    Assert.ThrowsAny<CryptographicException>(fun () -> KeyVault.decrypt vault "https://megafono.test/ap/actors/u2" blob |> ignore)
    |> ignore

[<Fact>]
let ``R-11 a flipped bit anywhere in the blob fails`` () =
    let blob = KeyVault.encrypt vault actor (Array.zeroCreate<byte> 20)

    for index in [ 1; 13; 29; blob.Length - 1 ] do
        let tampered = Array.copy blob
        tampered[index] <- tampered[index] ^^^ 1uy
        Assert.ThrowsAny<CryptographicException>(fun () -> KeyVault.decrypt vault actor tampered |> ignore) |> ignore

[<Fact>]
let ``R-11 malformed blobs are refused`` () =
    let blob = KeyVault.encrypt vault actor [| 1uy |]
    let wrongVersion = Array.copy blob
    wrongVersion[0] <- 9uy

    for bad in [ [||]; [| 1uy; 2uy |]; wrongVersion ] do
        Assert.ThrowsAny<CryptographicException>(fun () -> KeyVault.decrypt vault actor bad |> ignore) |> ignore

[<Fact>]
let ``R-11 a different master key cannot decrypt`` () =
    let blob = KeyVault.encrypt vault actor [| 1uy |]
    let other = KeyVault.masterKeys keyB None
    Assert.ThrowsAny<CryptographicException>(fun () -> KeyVault.decrypt other actor blob |> ignore) |> ignore

[<Fact>]
let ``R-11 the previous master key still decrypts, and new data uses the current one`` () =
    let oldBlob = KeyVault.encrypt (KeyVault.masterKeys keyA None) actor [| 7uy; 8uy |]
    let rotated = KeyVault.masterKeys keyB (Some keyA)
    Assert.Equal<byte[]>([| 7uy; 8uy |], KeyVault.decrypt rotated actor oldBlob)

    let newBlob = KeyVault.encrypt rotated actor [| 9uy |]
    // After the rotation the old key alone no longer opens new data
    Assert.ThrowsAny<CryptographicException>(fun () -> KeyVault.decrypt vault actor newBlob |> ignore) |> ignore
    Assert.Equal<byte[]>([| 9uy |], KeyVault.decrypt (KeyVault.masterKeys keyB None) actor newBlob)

[<Fact>]
let ``master keys must be 32 bytes`` () =
    Assert.Throws<ArgumentException>(fun () -> KeyVault.masterKeys (Array.zeroCreate 16) None |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> KeyVault.masterKeys keyA (Some(Array.zeroCreate 31)) |> ignore) |> ignore

[<Fact>]
let ``R-11 a new actor key is RSA-2048 and its private half comes back from the encrypted blob`` () =
    let created = KeyVault.createActorKey vault actor
    Assert.StartsWith("-----BEGIN PUBLIC KEY-----", created.PublicKeyPem)
    Assert.DoesNotContain("PRIVATE", created.PublicKeyPem)

    use priv = KeyVault.loadPrivateKey vault actor created.EncryptedPrivateKey
    use pub = KeyVault.importPublicPem created.PublicKeyPem
    Assert.Equal(2048, pub.KeySize)

    let data = Encoding.UTF8.GetBytes "hello"
    let signature = priv.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    Assert.True(pub.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))

[<Fact>]
let ``R-11 the private key never appears in the stored blob in clear`` () =
    let created = KeyVault.createActorKey vault actor
    use priv = KeyVault.loadPrivateKey vault actor created.EncryptedPrivateKey
    let der = priv.ExportPkcs8PrivateKey()
    let blob = created.EncryptedPrivateKey
    // The DER prefix of a PKCS#8 key is highly regular; it must not survive encryption
    Assert.False(blob.AsSpan().IndexOf(ReadOnlySpan<byte>(der[..15])) >= 0)

[<Fact>]
let ``R-11 loading a key for another actor fails`` () =
    let created = KeyVault.createActorKey vault actor

    Assert.ThrowsAny<CryptographicException>(fun () ->
        KeyVault.loadPrivateKey vault "https://megafono.test/ap/actors/other" created.EncryptedPrivateKey |> ignore)
    |> ignore
