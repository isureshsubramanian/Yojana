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
- `key_encryption_key_id nvarchar(500)`, `key_encryption_key_version nvarchar(100)`,
  `key_encryption_algorithm nvarchar(32)`
- `wrapped_key_nonce varbinary(12)`, `wrapped_key_ciphertext varbinary(max)`, `wrapped_key_tag varbinary(16)`
- `created_at datetime2(3)`, `rotated_at datetime2(3) null`, `expires_at datetime2(3) null`, `is_active bit`

Primary key: `(tenant_id, version)`. Enforce one active key per tenant with a filtered unique index on
`(tenant_id, is_active)` where `is_active = 1`.

## `tenant_data_key_rewrap_checkpoint`

One durable cursor per maintenance operation: `operation_id nvarchar(100)` primary key, `tenant_id uniqueidentifier`,
`data_key_version int`, and `updated_at datetime2(3)`. `TenantDataKeyRewrapService` saves the cursor after each successful
conditional rewrap. The cursor and data-key replacement do not need one transaction: a crash after replacement but
before the cursor update safely sees the already-current version and advances on retry.

## `connection_event`

Append-only connection audit records. Expected columns:

- `tenant_id uniqueidentifier`, `sequence_no bigint`, `event_id uniqueidentifier`, `connection_id uniqueidentifier`
- `event_type nvarchar(64)`, `actor_ref nvarchar(200)`, `occurred_at datetime2(3)`
- `details_json nvarchar(max)`, `previous_record_hash char(64)`, `record_hash char(64)`

Primary key: `(tenant_id, sequence_no)` and alternate key `(tenant_id, event_id)`. The database writer must reject
updates and deletes; immutable-storage replication is a separate `IConnectionAuditSink` implementation.

## `connection_throttle_profile`

One editable throttle profile and its current UTC-day budget per connection. Connector declarations seed these values
at onboarding; they are tenant-owned data after that point, not hard-coded connector rules. Expected columns:

- `tenant_id uniqueidentifier`, `connection_id uniqueidentifier`, `connector_code nvarchar(20)`
- `requests_per_second int null`, `requests_per_minute int null`, `requests_per_day int null`
- `burst int`, `max_concurrent_calls int`, `retry_after_ceiling_seconds int`
- `alert_threshold_percent int` (connector declaration normally seeds `80`)
- `updated_at datetime2(3)`
- `budget_date_utc date null`, `used_calls int`, `approach_alert_raised bit`, `cap_alert_raised bit`
- `budget_revision bigint`

Primary key: `(tenant_id, connection_id)`. Constraint all configured caps to positive values, `burst` and
`max_concurrent_calls` to at least one, `alert_threshold_percent` to 1–100, and `used_calls`/`budget_revision` to
non-negative values. `IConnectionDailyBudgetStore.TrySaveAsync` uses `budget_revision` for optimistic concurrency;
the update of count, alert flags and revision is one conditional write. Resetting for a new UTC date is the same
conditional write, so a restart cannot restore spent calls or re-raise either once-per-day event. The core mapping may
split the daily fields into a tenant-led child table if its transaction adapter preserves that compare-and-swap
contract.
