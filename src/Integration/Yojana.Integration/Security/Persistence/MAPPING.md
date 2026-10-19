# Security persistence mapping contract

The core Integration context owns the mappings and migration. Security code exposes records and storage ports only.
Every table is in schema `integration`, has `tenant_id` first in its primary key and tenant-led indexes, and receives the
repository's tenant row-level-security policy.

## `connection_credential`

One current or historical encrypted credential per connection and purpose. Expected columns:

- `tenant_id uniqueidentifier`, `connection_id uniqueidentifier`, `purpose nvarchar(100)`
- `data_key_version int`
- `nonce varbinary(12)`, `ciphertext varbinary(max)`, `tag varbinary(16)`
- `created_at datetime2(3)`, `rotated_at datetime2(3) null`, `expires_at datetime2(3) null`, `rotation_due_at datetime2(3)`

Suggested key: `(tenant_id, connection_id, purpose, created_at)`. Current-row lookup index:
`(tenant_id, connection_id, purpose, rotated_at)`.

## `tenant_data_key`

Wrapped per-tenant data-key versions; old versions remain readable after rotation. Expected columns:

- `tenant_id uniqueidentifier`, `version int`
- `key_encryption_key_id nvarchar(200)`, `key_encryption_key_version nvarchar(100)`
- `wrapped_key_nonce varbinary(12)`, `wrapped_key_ciphertext varbinary(max)`, `wrapped_key_tag varbinary(16)`
- `created_at datetime2(3)`, `rotated_at datetime2(3) null`, `expires_at datetime2(3) null`, `is_active bit`

Primary key: `(tenant_id, version)`. Enforce one active key per tenant with a filtered unique index on
`(tenant_id, is_active)` where `is_active = 1`.

## `connection_event`

Append-only connection audit records. Expected columns:

- `tenant_id uniqueidentifier`, `sequence_no bigint`, `event_id uniqueidentifier`, `connection_id uniqueidentifier`
- `event_type nvarchar(64)`, `actor_ref nvarchar(200)`, `occurred_at datetime2(3)`
- `details_json nvarchar(max)`, `previous_record_hash char(64)`, `record_hash char(64)`

Primary key: `(tenant_id, sequence_no)` and alternate key `(tenant_id, event_id)`. The database writer must reject
updates and deletes; immutable-storage replication is a separate `IConnectionAuditSink` implementation.
