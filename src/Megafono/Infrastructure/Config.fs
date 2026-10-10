/// Strongly typed options bound from `Megafono:*` (env form `Megafono__X__Y`) and startup validation
module Megafono.Infrastructure.Config

open System
open System.Text.RegularExpressions
open Microsoft.Extensions.Configuration

type AppOptions =
    { BaseUrl: string
      Token: string
      WebhookSecret: string }

type SoftwareOptions = { Name: string; Version: string }

type PortOptions =
    { Federation: int
      Internal: int
      /// Interface both listeners bind to; tests use 127.0.0.1
      BindAddress: string }

type DeliveryOptions = { PerHost: int; Global: int }

type RateLimitOptions = { PerAddress: int; PerHost: int }

type FederationOptions =
    {
        /// Tests only: lets the HTTP client reach private addresses and plain http (R-13)
        AllowPrivateAddresses: bool
    }

type MegafonoOptions =
    { Domain: string
      /// `ConnectionStrings:Database`
      Database: string
      App: AppOptions
      /// Base64 of 32 bytes (R-11)
      MasterKey: string
      /// Only while rotating the master key: still decrypts, never encrypts
      MasterKeyPrevious: string option
      Software: SoftwareOptions
      Ports: PortOptions
      Delivery: DeliveryOptions
      RateLimit: RateLimitOptions
      TrustedProxies: string list
      Federation: FederationOptions }

let private softwareNamePattern = Regex(@"^[a-z0-9-]+\z", RegexOptions.Compiled)

/// The 32 key bytes, or None when the text is not base64 of exactly 32 bytes
let decodeMasterKey (text: string) : byte[] option =
    if String.IsNullOrWhiteSpace text then
        None
    else
        try
            let bytes = Convert.FromBase64String(text.Trim())
            if bytes.Length = 32 then Some bytes else None
        with :? FormatException ->
            None

/// Reads `Megafono:*`; values that do not parse are reported with the key name rather than defaulted
let read (config: IConfiguration) : MegafonoOptions * string list =
    let errors = ResizeArray<string>()
    let section = config.GetSection "Megafono"

    let text (key: string) (fallback: string) =
        match section[key] with
        | null -> fallback
        | value -> value.Trim()

    let number (key: string) (fallback: int) =
        match section[key] with
        | null
        | "" -> fallback
        | value ->
            match Int32.TryParse value with
            | true, n -> n
            | _ ->
                errors.Add $"Megafono:{key} must be an integer, got '{value}'"
                fallback

    let flag (key: string) =
        match section[key] with
        | null
        | "" -> false
        | value ->
            match Boolean.TryParse value with
            | true, b -> b
            | _ ->
                errors.Add $"Megafono:{key} must be true or false, got '{value}'"
                false

    let proxies =
        // Either `Megafono__TrustedProxies=a,b` or indexed `Megafono__TrustedProxies__0=a`
        let own = text "TrustedProxies" ""
        let indexed = section.GetSection("TrustedProxies").GetChildren() |> Seq.choose (fun c -> Option.ofObj c.Value)

        Seq.append (own.Split(',')) indexed
        |> Seq.map (fun s -> s.Trim())
        |> Seq.filter (fun s -> s <> "")
        |> Seq.distinct
        |> List.ofSeq

    let options =
        { Domain = text "Domain" ""
          Database = defaultArg (Option.ofObj config["ConnectionStrings:Database"]) "" |> fun s -> s.Trim()
          App =
            { BaseUrl = text "App:BaseUrl" ""
              Token = text "App:Token" ""
              WebhookSecret = text "App:WebhookSecret" "" }
          MasterKey = text "MasterKey" ""
          MasterKeyPrevious =
            match text "MasterKeyPrevious" "" with
            | "" -> None
            | key -> Some key
          Software =
            { Name = text "Software:Name" "megafono"
              Version = text "Software:Version" "0.1.0" }
          Ports =
            { Federation = number "Ports:Federation" 8080
              Internal = number "Ports:Internal" 8081
              BindAddress = text "Ports:BindAddress" "0.0.0.0" }
          Delivery =
            { PerHost = number "Delivery:PerHost" 2
              Global = number "Delivery:Global" 32 }
          RateLimit =
            { PerAddress = number "RateLimit:PerAddress" 600
              PerHost = number "RateLimit:PerHost" 600 }
          TrustedProxies = proxies
          Federation = { AllowPrivateAddresses = flag "Federation:AllowPrivateAddresses" } }

    options, List.ofSeq errors

/// Every problem that must stop startup, in plain words; empty means valid
let validate (environmentName: string) (options: MegafonoOptions) : string list =
    [ if String.IsNullOrWhiteSpace options.Domain then
          "Megafono:Domain is required (the public host name, e.g. example.com)"
      elif options.Domain.Contains '/' || options.Domain.Contains "://" || options.Domain |> Seq.exists Char.IsWhiteSpace then
          "Megafono:Domain must be a bare host name without scheme, path or spaces"

      if String.IsNullOrWhiteSpace options.Database then
          "ConnectionStrings:Database is required (the Postgres connection string)"

      match Uri.TryCreate(options.App.BaseUrl, UriKind.Absolute) with
      | true, uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps -> ()
      | _ -> "Megafono:App:BaseUrl is required and must be an absolute http(s) URL (the app's private address)"

      if String.IsNullOrWhiteSpace options.App.Token then
          "Megafono:App:Token is required (the bearer token for /internal/* and for calls to the app)"

      if String.IsNullOrWhiteSpace options.App.WebhookSecret then
          "Megafono:App:WebhookSecret is required (signs the webhooks sent to the app)"

      if not (fst (Net.IPAddress.TryParse options.Ports.BindAddress)) then
          "Megafono:Ports:BindAddress must be an IP address"

      if (decodeMasterKey options.MasterKey).IsNone then
          "Megafono:MasterKey must be base64 of exactly 32 bytes (for example `openssl rand -base64 32`)"

      match options.MasterKeyPrevious with
      | Some previous when (decodeMasterKey previous).IsNone ->
          "Megafono:MasterKeyPrevious must be base64 of exactly 32 bytes when set"
      | _ -> ()

      if not (softwareNamePattern.IsMatch options.Software.Name) then
          "Megafono:Software:Name must match ^[a-z0-9-]+$"

      if options.Federation.AllowPrivateAddresses && String.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) then
          "Megafono:Federation:AllowPrivateAddresses is a test switch and is refused when ASPNETCORE_ENVIRONMENT is Production"

      for name, port in [ "Ports:Federation", options.Ports.Federation; "Ports:Internal", options.Ports.Internal ] do
          if port < 0 || port > 65535 then
              $"Megafono:{name} must be between 0 and 65535"

      if options.Ports.Federation = options.Ports.Internal && options.Ports.Federation <> 0 then
          "Megafono:Ports:Federation and Megafono:Ports:Internal must differ"

      for name, value in
          [ "Delivery:PerHost", options.Delivery.PerHost
            "Delivery:Global", options.Delivery.Global
            "RateLimit:PerAddress", options.RateLimit.PerAddress
            "RateLimit:PerHost", options.RateLimit.PerHost ] do
          if value < 1 then
              $"Megafono:{name} must be at least 1" ]

/// Reads and validates; the error lists everything that is wrong, not just the first problem
let load (environmentName: string) (config: IConfiguration) : Result<MegafonoOptions, string list> =
    let options, readErrors = read config

    match readErrors @ validate environmentName options with
    | [] -> Ok options
    | errors -> Error errors

/// The message startup fails with
let describeErrors (errors: string list) : string =
    "Invalid Megafono configuration:" + String.Join("", errors |> List.map (fun e -> $"{Environment.NewLine}  - {e}"))
