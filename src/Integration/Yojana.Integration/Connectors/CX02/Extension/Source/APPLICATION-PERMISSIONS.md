# Application registration and permissions

The customer's administrator installs this per-tenant extension in a sandbox, then creates one application registration for the Yojana connection. No client secret, certificate, token or tenant identifier belongs in this source tree or in an extension package.

The administrator enables the application identity, grants the application API permission `API.ReadWrite.All`, gives tenant-wide consent, and assigns the extension permission set `Yojana CX02 Integration`. The application must not receive the `SUPER` role. If the customer's standard automation permission set is needed for standard v2.0 resources used by the connector, assign it separately and record that assignment in the acceptance evidence.

`Yojana CX02 Integration` has explicit table-data and object entries only. Its read permissions cover every contract page. Its write permissions cover the staging record, native tracking reservations, the item journal and only the source documents created or posted by `postTrackedDocument`. Posting codeunits are named individually; there is no object-range wildcard.

Before production, the administrator should disable the sandbox registration, create a distinct production registration, use a certificate or federated credential where the customer's policy permits it, and keep credential rotation in the connection record. Credentials are configured in Yojana, never in this extension.
