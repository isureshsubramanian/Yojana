# Yojana site relay

The relay makes outbound HTTPS connections on TCP 443 to the product host. No inbound Internet
connection or product database access is required. It needs local access to the ERP's service
interface and, when enabled, the webhook listener port. Permit that listener only from the local
webhook messenger. Use a dedicated least-privilege ERP account.

Save the administrator's registration configuration as `relay.json`. Configuration fields:
`tenantId`, `relayId`, `productUrl` (HTTPS product base URL, including any path prefix), `serviceUrl` (fixed local service origin),
`declaration` (the connector's generated method/path and header allow-lists and session declaration),
`credentials` (`site` default or `cloud`), `concurrency` (1–32, default 4), `webhookBind` (optional
explicit IP URL ending in `/`), and `webhookTimeoutSeconds` (default 10).
Treat the declaration as protected installation configuration. Only administrators should edit it;
never replace it from a poll response. Re-provision it when enabling a new connector operation.

Provision secrets using standard input, never command-line arguments:

```
Yojana.Relay.exe C:\YojanaRelay\relay.json --protect-secrets
```

Supply one JSON object with `relaySecret` (one-time registration secret), `company`, `userName`,
`password`, then close standard input (Ctrl+Z and Enter on Windows, Ctrl+D elsewhere). Site ERP
credentials stay here. In cloud credential mode the last three fields may be empty.
Create the Windows service first (`sc.exe create` below), then enable its service SID with
`sc.exe sidtype YojanaRelay unrestricted` before provisioning secrets.
The command requires an explicit configuration path and creates `relay.json.secrets`: DPAPI machine-scope protection with purpose entropy and a protected ACL limited to SYSTEM,
Administrators and the YojanaRelay service SID on Windows; mode 0600
elsewhere. Restrict the installation directory and both files to the service account and local
administrators (DPAPI machine scope alone is not a user ACL). Never place secrets in `relay.json`.
Provision from a secure local console; disable console transcripts and shell history for secret input.
On non-Windows systems plaintext secret storage relies on file and directory permissions.

Run the same executable and configuration path as a console app on Windows, macOS or Linux.
For a Windows service, publish with:

```
dotnet publish src/Relay/Yojana.Relay -r win-x64 --self-contained
sc.exe create YojanaRelay binPath= "\"C:\YojanaRelay\Yojana.Relay.exe\" \"C:\YojanaRelay\relay.json\"" start= auto
sc.exe start YojanaRelay
```

Enable the service SID and provision the secret file before starting. Configure service recovery to restart after
host failures. Transient cloud/network failures are retried with jitter without stopping the agent.
HTTP webhook binds must use an explicit private LAN or loopback IP. HTTPS binds require a local
certificate and HTTP.sys certificate binding (`netsh http add sslcert` on Windows); reserve the URL
for the service account (`netsh http add urlacl`). Do not bind HTTP to a public or wildcard address.
The cloud's `relay_webhook_url` setting must match this listener's reachable local URL. Handshakes
and deliveries are synchronously forwarded; cloud failure returns 503 for messenger retry.

Rotation returns a new secret with a configurable overlap (default 60 minutes). Stop the service,
securely remove/replace the protected secret file with `--protect-secrets`, and restart within the
overlap. A second rotation requires the administrator to force it with an audited reason; this drops both
previous keys immediately and has no overlap. Revocation disables both secrets.
If upgrading or moving machines, re-provision DPAPI secrets on the target machine.

Work leases are not reissued: a lost result is uncertain, especially for writes. The cloud's existing
query-before-create retry logic resolves that uncertainty. Two agents may share a relay id during a
rolling restart; each uses its own local session and receives distinct leases.

Third-party attribution ships beside this executable in `Notices/THIRD-PARTY-NOTICES.txt`.
