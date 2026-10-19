# Local security providers

The local providers use only runtime cryptography and filesystem APIs. They are development facilities, not substitutes
for the production key and immutable-audit implementations.

Select the local key-encryption provider with `Integration:Security:KeyEncryption:Provider=Local`. Supply a random
32-byte key as base64 through user-secrets or deployment configuration under
`Integration:Security:LocalKeyEncryption:Current`, with `KeyBase64`, `KeyId` and `KeyVersion` values. Never put the key
in a tracked settings file. Retain older entries under `Integration:Security:LocalKeyEncryption:Previous:0`, `:1`, and
so on; each entry has the same three fields. The current key wraps new tenant data keys, while every configured version
can unwrap existing ones.

After changing `Current`, run `TenantDataKeyRewrapService.RunBatchAsync` repeatedly with one stable operation id until
it reports `IsComplete`. It checkpoints after every data-key record. The in-memory stores used by this skeleton retain
the checkpoint only for the process lifetime; the Integration persistence task must map both the rewrap store and
checkpoint port to durable `integration` tables before this is used for live credentials.

Production startup rejects this provider. An exceptional isolated deployment must set `AllowInProduction=true` and a
non-empty `ProductionOverrideReason`; both are deliberately explicit and auditable configuration.

Select the local audit sink with `Integration:Security:Audit:Provider=LocalFile` and set
`Integration:Security:Audit:LocalRoot` to a protected directory. Files are partitioned by tenant and UTC day and opened
only for append. Back up or remove the directory outside the application; the sink has no rewrite or delete operation.

When no key-encryption provider is configured, registration installs a refusing provider. Startup therefore remains
cheap for hosts that do not resolve security services, while the first attempted key operation fails with the named
configuration reason.
