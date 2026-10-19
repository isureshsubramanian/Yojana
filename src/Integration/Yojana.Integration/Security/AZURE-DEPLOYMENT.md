# Azure security deployment

## Key Vault key-encryption key

Create a versioned RSA/RSA-HSM or AES/AES-HSM key. Grant the workload identity **Key Vault Crypto User** on that key.
Set:

- `Integration:Security:KeyEncryption:Provider=AzureKeyVault`
- `Integration:Security:AzureKeyVault:VaultUri=https://<vault>.vault.azure.net/`
- `Integration:Security:AzureKeyVault:KeyName=<key name>`
- `Integration:Security:AzureKeyVault:KeyVersion=<current version>`

`Credential` may be `DefaultAzureCredential`, `ManagedIdentity`, or `ClientSecret`. For a user-assigned managed identity,
set `ManagedIdentityClientId`. For a client credential, supply `TenantId`, `ClientId`, and `ClientSecret` from secret
configuration. The default credential deliberately excludes interactive, development-tool, command-line, and broker
credentials; unattended work never uses a person's delegated sign-in.

RSA keys use RSA-OAEP-256 and AES keys use A256KW. Each wrapped tenant data-key record carries the key id, key version,
and algorithm; unwrap always addresses that recorded version.

### Rotation

1. Create a new key version without disabling or deleting earlier versions.
2. Change `KeyVersion` to the new version and restart the workload. New wraps now use it.
3. Run `TenantDataKeyRewrapService.RunBatchAsync` with a stable operation id and bounded batch size until `IsComplete`.
   Re-run the same id after interruption; the durable checkpoint resumes after the last successful tenant data key.
4. Verify all data-key records name the current version and exercise credential decryption.
5. Keep old versions enabled through the rollback and retention window. Disable one only after no stored record refers
   to it; deletion is a separate operator decision and is never performed by Yojana.

## Immutable connection audit container

Create the container before deployment and grant the workload identity **Storage Blob Data Contributor** scoped to that
container. Set `Integration:Security:Audit:Provider=AzureBlob`, `AzureBlob:ServiceUri`, and `AzureBlob:ContainerName`.
`AzureBlob:ManagedIdentityClientId` selects a user-assigned identity. `ConnectionString` exists for Azurite tests only.

Configure a locked, time-based immutability policy (WORM) on the container for the tenant's audit-retention period.
Enable legal holds where the tenant's process requires them. The application creates append blobs named
`tenant/<tenant id>/<UTC date>.jsonl` and only issues create-if-absent, read, list, and conditional append operations; it
contains no delete or overwrite path. The policy is still mandatory because it enforces immutability against operators,
other principals, and defects outside this adapter.

The hash chain continues across daily blobs. Monitor failed conditional appends and authorization failures. Changing
retention is an operator and tenant-policy decision; the application never shortens a container policy.

## Opt-in integration checks

Normal tests use fakes. Set `YOJANA_TEST_KEYVAULT_URI` to a full versioned key URI to exercise wrap/unwrap using the
non-interactive credential chain. Set `YOJANA_TEST_AZURITE` to an existing Azurite connection string to exercise append
blobs; the test creates a uniquely named container and does not install or start Azurite.
