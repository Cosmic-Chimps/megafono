/// In-process test PKI: a root CA and one leaf certificate per fake domain (R-15).
/// Everything the containers need is written to a temp directory as PEM, and
/// `httpClient` builds an `HttpClient` that trusts only this CA.
module Megafono.Interop.Tests.TestCa

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading.Tasks

type Leaf =
    { Domain: string
      CertPem: string
      KeyPem: string
      CertPath: string
      KeyPath: string }

type TestPki =
    { Directory: string
      CaCert: X509Certificate2
      CaPem: string
      /// Path of the CA bundle (just the root here); mounted as `SSL_CERT_FILE`.
      CaBundlePath: string
      Leaves: Map<string, Leaf> }

    member this.Leaf(domain: string) = this.Leaves[domain]

let private newKey () = ECDsa.Create ECCurve.NamedCurves.nistP256

let private randomSerial () =
    let bytes = RandomNumberGenerator.GetBytes 16
    bytes[0] <- bytes[0] &&& 0x7Fuy
    bytes

/// Generates the CA and one leaf per domain (SAN = the domain) and writes the PEM files under `dir`.
let create (dir: string) (domains: string list) : TestPki =
    Directory.CreateDirectory dir |> ignore
    let notBefore = DateTimeOffset.UtcNow.AddDays -1.0
    let notAfter = DateTimeOffset.UtcNow.AddDays 30.0

    use caKey = newKey ()
    let caReq = CertificateRequest("CN=Megafono Interop Test CA", caKey, HashAlgorithmName.SHA256)
    caReq.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
    caReq.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign ||| X509KeyUsageFlags.CrlSign, true))
    caReq.CertificateExtensions.Add(X509SubjectKeyIdentifierExtension(caReq.PublicKey, false))
    let caCert = caReq.CreateSelfSigned(notBefore, notAfter)
    let caPem = caCert.ExportCertificatePem()
    let caBundlePath = Path.Combine(dir, "ca.pem")
    File.WriteAllText(caBundlePath, caPem)

    let makeLeaf (domain: string) =
        use key = newKey ()
        let req = CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256)
        let san = SubjectAlternativeNameBuilder()
        san.AddDnsName domain
        req.CertificateExtensions.Add(san.Build())
        req.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, true))
        req.CertificateExtensions.Add(X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true))
        let serverAuth = OidCollection()
        serverAuth.Add(Oid "1.3.6.1.5.5.7.3.1") |> ignore
        req.CertificateExtensions.Add(X509EnhancedKeyUsageExtension(serverAuth, false))
        req.CertificateExtensions.Add(X509SubjectKeyIdentifierExtension(req.PublicKey, false))
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false))
        use cert = req.Create(caCert, notBefore, notAfter, randomSerial ())
        let certPem = cert.ExportCertificatePem()
        let keyPem = key.ExportPkcs8PrivateKeyPem()
        let certPath = Path.Combine(dir, $"{domain}.crt.pem")
        let keyPath = Path.Combine(dir, $"{domain}.key.pem")
        File.WriteAllText(certPath, certPem)
        File.WriteAllText(keyPath, keyPem)
        domain,
        { Domain = domain
          CertPem = certPem
          KeyPem = keyPem
          CertPath = certPath
          KeyPath = keyPath }

    { Directory = dir
      CaCert = caCert
      CaPem = caPem
      CaBundlePath = caBundlePath
      Leaves = domains |> List.map makeLeaf |> Map.ofList }

/// An `HttpClient` that trusts only the test CA. `connectTo` redirects every TCP connection to
/// the given host and port (the Caddy front's mapped port) while keeping the URL host for SNI and
/// `Host`, so the test process can call `https://gts.test/...` without touching /etc/hosts.
/// Redirects are not followed and cookies are kept, which the OAuth sign-in flow needs.
let httpClient (pki: TestPki) (connectTo: (string * int) option) : HttpClient =
    let handler = new SocketsHttpHandler()
    handler.AllowAutoRedirect <- false
    handler.UseCookies <- true
    handler.CookieContainer <- CookieContainer()
    handler.SslOptions.RemoteCertificateValidationCallback <-
        RemoteCertificateValidationCallback(fun _ cert _ errors ->
            if isNull cert || errors.HasFlag SslPolicyErrors.RemoteCertificateNameMismatch
               || errors.HasFlag SslPolicyErrors.RemoteCertificateNotAvailable then
                false
            else
                use chain = new X509Chain()
                chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
                chain.ChainPolicy.CustomTrustStore.Add pki.CaCert |> ignore
                chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
                chain.Build(new X509Certificate2(cert)))
    match connectTo with
    | Some(host, port) ->
        handler.ConnectCallback <-
            Func<_, _, _>(fun _ (ct: Threading.CancellationToken) ->
                ValueTask<IO.Stream>(
                    task {
                        let socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                        try
                            do! socket.ConnectAsync(host, port, ct)
                            return new NetworkStream(socket, true) :> IO.Stream
                        with e ->
                            socket.Dispose()
                            return raise e
                    }
                ))
    | None -> ()
    let client = new HttpClient(handler, true, Timeout = TimeSpan.FromSeconds 30.0)
    // GoToSocial answers 418 to requests without a User-Agent.
    client.DefaultRequestHeaders.UserAgent.ParseAdd "megafono-interop-tests/0.1"
    client
