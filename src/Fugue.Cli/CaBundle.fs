module Fugue.Cli.CaBundle

open System
open System.Security.Cryptography.X509Certificates

/// If FUGUE_CA_BUNDLE env var points to a PEM file, load each cert and install
/// into the current-user CA authority store so all HttpClient instances trust it.
/// Returns the count of certs installed, or 0 if env var is unset.
let install () : int =
    let path = Environment.GetEnvironmentVariable "FUGUE_CA_BUNDLE" |> Option.ofObj
    match path with
    | None -> 0
    | Some p when not (System.IO.File.Exists p) ->
        eprintfn "FUGUE_CA_BUNDLE: file not found: %s" p
        0
    | Some p ->
        try
            let certs = X509Certificate2Collection()
            certs.ImportFromPemFile p
            if certs.Count = 0 then
                eprintfn "FUGUE_CA_BUNDLE: no certs found in %s" p
                0
            else
                use store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser)
                store.Open OpenFlags.ReadWrite
                let mutable added = 0
                for cert in certs do
                    // Skip if already trusted
                    let existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false)
                    if existing.Count = 0 then
                        store.Add cert
                        added <- added + 1
                added
        with ex ->
            eprintfn "FUGUE_CA_BUNDLE: failed to load %s: %s" p ex.Message
            0
