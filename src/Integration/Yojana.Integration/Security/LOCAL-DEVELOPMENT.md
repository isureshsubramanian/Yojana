# Local security providers

The local providers use only runtime cryptography and filesystem APIs. They are development facilities, not substitutes
for the production key and immutable-audit implementations.

Select the local key-encryption provider with `Integration:Security:KeyEncryption:Provider=Local`. Supply a random
32-byte key as base64 through user-secrets or deployment configuration at
`Integration:Security:LocalKeyEncryption:KeyBase64`, plus stable `KeyId` and `KeyVersion` values. Never put the key in a
tracked settings file. Changing the configured key requires retaining a provider capable of unwrapping each stored old
version.

Production startup rejects this provider. An exceptional isolated deployment must set `AllowInProduction=true` and a
non-empty `ProductionOverrideReason`; both are deliberately explicit and auditable configuration.

Select the local audit sink with `Integration:Security:Audit:Provider=LocalFile` and set
`Integration:Security:Audit:LocalRoot` to a protected directory. Files are partitioned by tenant and UTC day and opened
only for append. Back up or remove the directory outside the application; the sink has no rewrite or delete operation.
