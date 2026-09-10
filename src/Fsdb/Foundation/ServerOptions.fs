/// Immutable listener settings shared by the command-line server and embedding API.
module Fsdb.ServerOptions

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Fsdb.OptionFile

/// Which server-owned files a listener may expose to SQL statements.
[<RequireQualifiedAccess>]
type SecureFilePolicy =
    | Disabled
    | Directory of string
    | Unrestricted

/// Transport security and server-file policy for a listener.
type Settings =
    { Certificate: X509Certificate2 option
      ClientCertificateAuthorities: X509Certificate2 list
      AuthenticationRsaKeys: Map<Authentication.Plugin, Authentication.RsaKeyPair>
      RequireSecureTransport: bool
      SecureFiles: SecureFilePolicy }

/// Settings for a plaintext listener with server-owned files disabled.
let defaults =
    { Certificate = None
      ClientCertificateAuthorities = []
      AuthenticationRsaKeys = Map.empty
      RequireSecureTransport = false
      SecureFiles = SecureFilePolicy.Disabled }

/// Adds a server certificate to the transport settings.
let withCertificate (certificate: X509Certificate2) (settings: Settings) =
    { settings with Certificate = Some certificate }

/// Trusts client certificates issued by `certificateAuthority`.
let withClientCertificateAuthority (certificateAuthority: X509Certificate2) (settings: Settings) =
    { settings with
        ClientCertificateAuthorities = certificateAuthority :: settings.ClientCertificateAuthorities }

/// Refuses plaintext handshakes.
let requireSecureTransport (settings: Settings) =
    { settings with RequireSecureTransport = true }

/// Uses `privateKey` for full authentication by accounts assigned to `plugin`.
let withAuthenticationRsaKey plugin (privateKey: RSA) (settings: Settings) =
    { settings with
        AuthenticationRsaKeys =
            settings.AuthenticationRsaKeys
            |> Map.add plugin (Authentication.rsaKeyPair privateKey) }

let internal canonicalPath (path: string) =
    let fullPath = Path.GetFullPath path
    let root = Path.GetPathRoot fullPath
    let relative = Path.GetRelativePath(root, fullPath)

    if relative = "." then
        root
    else
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        |> Array.fold
            (fun current part ->
                let next = Path.Combine(current, part)

                let entry: FileSystemInfo =
                    if Directory.Exists next then DirectoryInfo next
                    else FileInfo next

                if entry.Exists then
                    match entry.ResolveLinkTarget true with
                    | null -> next
                    | target -> target.FullName
                else
                    next)
            root

let internal normalizeSecureFileDirectory (path: string) =
    if String.IsNullOrWhiteSpace path then
        invalidArg "path" "secure_file_priv needs a directory"

    let fullPath = canonicalPath path

    if not (Path.IsPathFullyQualified path) then
        invalidArg "path" "secure_file_priv needs an absolute directory"
    elif not (Directory.Exists fullPath) then
        invalidArg "path" "secure_file_priv directory does not exist"

    fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    + string Path.DirectorySeparatorChar

/// Restricts server-side file statements to one operator-owned directory.
let withSecureFileDirectory (path: string) (settings: Settings) =
    { settings with SecureFiles = SecureFilePolicy.Directory(normalizeSecureFileDirectory path) }

/// Allows server-side file statements to use any path visible to the process.
let allowUnrestrictedServerFiles (settings: Settings) =
    { settings with SecureFiles = SecureFilePolicy.Unrestricted }

let secureFileVariable =
    function
    | SecureFilePolicy.Disabled -> None
    | SecureFilePolicy.Directory path -> Some path
    | SecureFilePolicy.Unrestricted -> Some ""

let private boolValue (name: string) (value: string option) =
    match value |> Option.map (fun text -> text.Trim().ToLowerInvariant()) with
    | None
    | Some "1"
    | Some "on"
    | Some "true" -> Ok true
    | Some "0"
    | Some "off"
    | Some "false" -> Ok false
    | Some text -> Error(sprintf "%s: '%s' is not a boolean" name text)

let private loadCertificate (certPath: string) (keyPath: string) =
    try
        let certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath)

        if certificate.HasPrivateKey then
            Ok certificate
        else
            Error "certificate has no private key"
    with ex ->
        Error ex.Message

let private loadCertificateAuthorities (path: string) =
    try
        let certificates = X509Certificate2Collection()
        certificates.ImportFromPemFile path

        if certificates.Count = 0 then
            Error "file contains no certificates"
        else
            certificates |> Seq.toList |> Ok
    with ex ->
        Error ex.Message

let private loadAuthenticationRsaKey (privatePath: string) (publicPath: string) =
    try
        use privateKey = RSA.Create()
        privateKey.ImportFromPem(File.ReadAllText privatePath)
        let pair = Authentication.rsaKeyPair privateKey

        use publicKey = RSA.Create()
        publicKey.ImportFromPem(File.ReadAllText publicPath)

        if Authentication.matchesPublicKey pair publicKey then
            Ok pair
        else
            Error "public key does not match the private key"
    with ex ->
        Error ex.Message

/// Splits immutable listener options from process-wide runtime settings.
let fromEntries (entries: Entry list) : Result<Settings * Entry list, string> =
    let mutable certificatePath: (string * Entry) option = None
    let mutable keyPath: (string * Entry) option = None
    let mutable certificateAuthorityPath: (string * Entry) option = None
    let mutable cachingPrivateKeyPath: (string * Entry) option = None
    let mutable cachingPublicKeyPath: (string * Entry) option = None
    let mutable sha256PrivateKeyPath: (string * Entry) option = None
    let mutable sha256PublicKeyPath: (string * Entry) option = None
    let mutable requireSecure = defaults.RequireSecureTransport
    let mutable secureFiles = defaults.SecureFiles
    let remaining = ResizeArray<Entry>()
    let errors = ResizeArray<string>()

    for entry in entries do
        let name =
            let normalized = normalizeName entry.Name
            if normalized.StartsWith "loose_" then normalized.Substring 6 else normalized

        match name with
        | "ssl_cert" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> certificatePath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: ssl_cert needs a path" entry.Source entry.Line)
        | "ssl_key" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> keyPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: ssl_key needs a path" entry.Source entry.Line)
        | "ssl_ca" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> certificateAuthorityPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: ssl_ca needs a path" entry.Source entry.Line)
        | "caching_sha2_password_private_key_path" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> cachingPrivateKeyPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: caching_sha2_password_private_key_path needs a path" entry.Source entry.Line)
        | "caching_sha2_password_public_key_path" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> cachingPublicKeyPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: caching_sha2_password_public_key_path needs a path" entry.Source entry.Line)
        | "sha256_password_private_key_path" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> sha256PrivateKeyPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: sha256_password_private_key_path needs a path" entry.Source entry.Line)
        | "sha256_password_public_key_path" ->
            match entry.Value with
            | Some path when not (String.IsNullOrWhiteSpace path) -> sha256PublicKeyPath <- Some(path, entry)
            | _ -> errors.Add(sprintf "%s:%d: sha256_password_public_key_path needs a path" entry.Source entry.Line)
        | "require_secure_transport" ->
            match boolValue "require_secure_transport" entry.Value with
            | Ok value -> requireSecure <- value
            | Error message -> errors.Add(sprintf "%s:%d: %s" entry.Source entry.Line message)
        | "secure_file_priv" ->
            match entry.Value with
            | None -> errors.Add(sprintf "%s:%d: secure_file_priv needs a value" entry.Source entry.Line)
            | Some value when value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ->
                secureFiles <- SecureFilePolicy.Disabled
            | Some "" -> secureFiles <- SecureFilePolicy.Unrestricted
            | Some path ->
                try
                    secureFiles <- SecureFilePolicy.Directory(normalizeSecureFileDirectory path)
                with
                | error
                    when (error :? ArgumentException)
                         || (error :? IOException)
                         || (error :? UnauthorizedAccessException) ->
                    errors.Add(sprintf "%s:%d: %s" entry.Source entry.Line error.Message)
        | _ -> remaining.Add entry

    let certificate =
        match certificatePath, keyPath with
        | None, None -> None
        | Some _, None ->
            errors.Add "ssl_cert requires ssl_key"
            None
        | None, Some _ ->
            errors.Add "ssl_key requires ssl_cert"
            None
        | Some(certPath, certEntry), Some(keyPath, _) ->
            match loadCertificate certPath keyPath with
            | Ok certificate -> Some certificate
            | Error message ->
                errors.Add(sprintf "%s:%d: cannot load TLS certificate: %s" certEntry.Source certEntry.Line message)
                None

    if requireSecure && certificate.IsNone then
        errors.Add "require_secure_transport needs ssl_cert and ssl_key"

    let certificateAuthorities =
        match certificateAuthorityPath with
        | None -> []
        | Some(path, entry) ->
            match loadCertificateAuthorities path with
            | Ok certificates -> certificates
            | Error message ->
                errors.Add(sprintf "%s:%d: cannot load client certificate authorities: %s" entry.Source entry.Line message)
                []

    if not (List.isEmpty certificateAuthorities) && certificate.IsNone then
        errors.Add "ssl_ca needs ssl_cert and ssl_key"

    let loadKeyPair plugin optionName privatePath publicPath =
        match privatePath, publicPath with
        | None, None -> None
        | Some _, None ->
            errors.Add(sprintf "%s_private_key_path requires %s_public_key_path" optionName optionName)
            None
        | None, Some _ ->
            errors.Add(sprintf "%s_public_key_path requires %s_private_key_path" optionName optionName)
            None
        | Some(privatePath, entry), Some(publicPath, _) ->
            match loadAuthenticationRsaKey privatePath publicPath with
            | Ok key -> Some(plugin, key)
            | Error message ->
                errors.Add(sprintf "%s:%d: cannot load authentication RSA keys: %s" entry.Source entry.Line message)
                None

    let authenticationRsaKeys =
        [ loadKeyPair
              Authentication.CachingSha2Password
              "caching_sha2_password"
              cachingPrivateKeyPath
              cachingPublicKeyPath
          loadKeyPair Authentication.Sha256Password "sha256_password" sha256PrivateKeyPath sha256PublicKeyPath ]
        |> List.choose id
        |> Map.ofList

    if errors.Count = 0 then
        Ok(
            { Certificate = certificate
              ClientCertificateAuthorities = certificateAuthorities
              AuthenticationRsaKeys = authenticationRsaKeys
              RequireSecureTransport = requireSecure
              SecureFiles = secureFiles },
            List.ofSeq remaining
        )
    else
        Error(String.concat "\n" errors)
