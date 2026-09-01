/// Transport settings shared by the command-line server and embedding API.
module Fsdb.ServerOptions

open System
open System.Security.Cryptography.X509Certificates
open Fsdb.OptionFile

/// Server identity, client trust roots, and plaintext policy for a listener.
type Settings =
    { Certificate: X509Certificate2 option
      ClientCertificateAuthorities: X509Certificate2 list
      RequireSecureTransport: bool }

/// Settings for a plaintext listener.
let defaults =
    { Certificate = None
      ClientCertificateAuthorities = []
      RequireSecureTransport = false }

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

/// Splits TLS options from unrelated server options and validates the final TLS settings.
let fromEntries (entries: Entry list) : Result<Settings * Entry list, string> =
    let mutable certificatePath: (string * Entry) option = None
    let mutable keyPath: (string * Entry) option = None
    let mutable certificateAuthorityPath: (string * Entry) option = None
    let mutable requireSecure = defaults.RequireSecureTransport
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
        | "require_secure_transport" ->
            match boolValue "require_secure_transport" entry.Value with
            | Ok value -> requireSecure <- value
            | Error message -> errors.Add(sprintf "%s:%d: %s" entry.Source entry.Line message)
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

    if errors.Count = 0 then
        Ok(
            { Certificate = certificate
              ClientCertificateAuthorities = certificateAuthorities
              RequireSecureTransport = requireSecure },
            List.ofSeq remaining
        )
    else
        Error(String.concat "\n" errors)
