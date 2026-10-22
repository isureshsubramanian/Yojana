# Yojana benchmark and P6 concurrency harness

The benchmark tool targets an explicitly named SQL Server database. Never point it at a customer or development
database. The P6 concurrency run uses a benchmark tenant prepared by this tool and signs in through the real web API.
Passwords come from environment variables and are never written to a command line, report or file.

## Migration source and end-to-end rehearsal

`migration-source` recreates the synthetic source tables from the extraction protocol itself, then bulk-loads a
repeatable first-customer-shaped data set. A database name uses `YOJANA_TEST_SQL` (or `YOJANA_BENCH_SQL`) only as the
server and credential template; a full connection string is also accepted. Never use a live source or target.

```sh
dotnet run --project tools/Yojana.Bench -c Release -- migration-source \
  --serials 20000 --seed 36 --connection yojana_src_p9t36

dotnet run --project tools/Yojana.Bench -c Release -- migration-rehearsal \
  --target yojana_test_p9t36 --source yojana_src_p9t36 --tenant new \
  --seed 36 --out artifacts/p9t36-rehearsal
```

The fixed profile is deliberately named in `MigrationSourceProfile`: 40 item codes in eight IT-hardware families and
50 contract templates. Of the requested serial population, the generator targets 65% delivered, 5% returned, 5%
transferred, 1% cancelled-delivery pairs, 2% credit memos, 2% goods issues, 85% equipment cards, 35% service
contracts, 1% replacements, 2% multi-partner cards, and 1% cards whose customer differs from the last delivery.
Anomalies target 0.4% duplicates after normalisation, 1% whitespace/case variants, 0.3% placeholders, and 0.2%
orphan cards. Small data sets use at least one row for the branches where exercising the branch matters.

The rehearsal invokes the production extraction, profiling, canonicalisation, reconstruction, template decomposition,
mapping, validation, final-load, delta-load and rollback services. Its only synthetic decisions are explicit: merge an
unambiguous same-item duplicate into the suggested candidate, accept recorded history, leave an orphan excluded for
source correction, and keep every other ambiguous record apart. It approves every unambiguous template proposal,
publishes the resulting drafts, and selects published base and supplier fallback policies through the existing tenant
mapping setting. Those choices belong to the rehearsal tenant only and are not production defaults.

After the final-load watermark, the source mutation adds 0.5% serials, changes cover on 1% of non-contract cards,
changes owners on 0.2% of different non-contract cards, and removes 0.1% of serial masters. The runner requires all
four finding kinds, undoes the delta, undoes the final load, and verifies that no migrated-unit registration remains.
On failure it records the completed phases and attempts both rollbacks; the report says if cleanup itself fails. A
failed run is not resumed in place: inspect its report, correct the cause, and use a new rehearsal tenant. Source
generation is deterministic for `(serials, seed)` and safely recreates only the extraction-protocol tables.
The undos remove loaded unit data, but retain the tenant, settings, policies, migration batches, decisions and
validation records as rehearsal evidence. Use a disposable target or rehearsal tenant when a pristine reset matters.

Each run writes JSON and a Markdown table with wall time, rows in/out, a lower-bound SQL command count, database size,
and the two slowest observed statements per phase. Add `--capture-plans` to a separate diagnostic run to write the two
slowest actual post-execution plans as `.sqlplan` files. Actual-plan collection adds SQL Server instrumentation work,
so do not use that run for the scaling curve. The Codex SQL container is Express: keep local runs at or below 20,000
serials; run 420,000 only on the designated benchmark server.

## Build and migrate the benchmark database

Set `YOJANA_BENCH_SQL` to the full connection string for the benchmark database and set
`YOJANA_BENCH_PASSWORD` to the password the generated benchmark users will share. The connection string must name the
benchmark database; do not use the web host's design-time localhost connection.

```sh
export MSBUILDDISABLENODEREUSE=1
export DOTNET_SYSTEM_NET_SECURITY_DISABLETLSRESUME=1
export DOTNET_SYSTEM_NET_SECURITY_TLS13_DISABLE=1
export Platform__ConnectionString="$YOJANA_BENCH_SQL"

dotnet build Yojana.slnx -c Debug -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet run --project src/Web/Yojana.Web -c Debug --no-build -- --migrate
```

The two TLS variables are the same compatibility settings used by the Makefile `bench` target. The migration command
uses the current web-host build and `Platform__ConnectionString`; do not use `dotnet ef database update`.

## Upgrading a large existing database

Stop application writers for the schema-migration window and take the normal database backup first. Two P6 migrations
deserve operator attention:

| Migration | Work during migration | Planning duration per million units |
|---|---|---|
| `P6PerfLedgerReadShapes` | Creates two empty sparse-projection tables and their indexes. It does not rewrite the ledger. | Under one second of database work; 0.4 s above no-op host startup in the Codex SQL measurement. |
| `P6PerfCreatedEventTime` | Scans the live and archive ledgers once into temporary staging, then fills `unit` and `unit_history` in restart-safe 2,000-row keyed updates. | 34 s measured for 1.0 M units and 3.95 M ledger rows. Allow about 4–6 minutes for 1.0 M units beside a 152 M-row ledger on similar Express-class I/O; measure the sequential staging scan on the target tier. |

The `P6PerfCreatedEventTime` estimate separates ledger density from unit count: the staging scan grows with ledger rows,
while the keyed write phase grows with units. The measured pre-batching statement finished in 10.9 s on the smaller
ledger but held an object-level X lock on `unit` throughout. The production shape intentionally trades some elapsed
time for bounded write locks; its slowest measured unit batch was 43 ms and no object-level X lock was observed. If the
process is interrupted, rerun the same host migration command. The nullable column and committed batches are detected,
only remaining NULL rows are staged, and temporal versioning plus the tenant policy are restored on successful finish.

Watch the fill from an administrator connection while it runs:

```sql
SELECT COUNT_BIG(*) AS total_units,
       SUM(CASE WHEN created_event_time IS NULL THEN 1 ELSE 0 END) AS units_remaining
FROM serialisation.unit WITH (READPAST);

SELECT status, command, wait_type, wait_time, reads, writes, logical_reads
FROM sys.dm_exec_requests
WHERE database_id = DB_ID() AND session_id <> @@SPID;
```

During the initial read-only staging pass, `units_remaining` does not move; `sys.dm_exec_requests.command` reports
`INSERT`. It then falls in 2,000-row steps during the keyed `UPDATE` phase. A failed startup must be rerun before serving
traffic; the migration is not recorded as complete until the column is non-null, system versioning is on, and RLS is
restored.

`P6PerfLedgerReadShapes` deliberately leaves the historical `ledger_base_order_ref` fill to the existing throttled,
resumable tenant job. Readers retain their bounded legacy fallback until its checkpoint is complete. Tenant
administrators watch that separate, potentially multi-hour ledger scan at
`GET /api/admin/ledger-base-order-backfill`; the response reports rows done/written, estimated rows, checkpoint month,
and completed/total months.

For a new database, load the benchmark shape before preparing concurrency data:

```sh
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- prepare --units 1000000 --events 150000000 --tenants 4 --out artifacts/bench-setup
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- load --units 1000000 --events 150000000 --tenants 4 --out artifacts/bench-setup
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- entitlements --units 1000000 --events 150000000 --tenants 4 --out artifacts/bench-setup
```

## Seed and prepare tenant 1

`seed-users` creates only the platform tenant and identity rows for the selected benchmark tenant. It is idempotent and
creates one account per role plus an administrator. Tenant 1's administrator is
`admin@bench1.yojana.local`; its password is read only from `YOJANA_BENCH_PASSWORD`.

`prepare-concurrency` then creates the persistent working set through application command handlers. Stable command IDs
make reruns safe. Pass the same `--engineers`, `--duration` and optional `--weights` as the intended run: preparation
sizes the RMA-eligible pool from those values with 50% headroom and a 12,000-unit floor for fast short verification
runs. `--documents` is the total number of editable campaigns and policy drafts, split approximately evenly.

```sh
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- seed-users --tenant 1
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- prepare-concurrency --tenant 1 --documents 50 --engineers 120 --duration 10m --numbering sequence
```

For the example above, the prepared set contains at least 12,000 RMA-eligible customer-owned shipped units with live outbound
cover plus 200 returned units owned and held by us with open refurbishment work orders, 150 submitted adjudication claims for each numbering-mode run, 50
separate editable claims, and approved replacement sources plus replacement-stock units sized from the run's engineer count,
duration and workload weights with the same 12,000-operation safety floor as the RMA pool, as well as version-checked
campaign/policy/claim documents. Preparation finishes with a whole-unit consistency check over ownership, custody,
entitlements, identifiers and the ledger events that produced those states.

## Start the host and run both numbering modes

Use any free port. Keep `Platform__ConnectionString` and the two TLS variables in the host environment.

```sh
export ASPNETCORE_URLS=http://127.0.0.1:5086
dotnet run --project src/Web/Yojana.Web -c Debug --no-build
```

In another shell with the same benchmark variables, run the mode prepared above:

```sh
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- concurrency \
  --tenant 1 --url http://127.0.0.1:5086 --engineers 120 --duration 10m \
  --numbering sequence --out artifacts/bench-concurrency
```

Before the gap-free run, prepare a fresh round for that mode; both the approved claims and replacement-stock selection
are consumed by a run:

```sh
YOJANA_BENCH_CLAIM_ROUND=2 dotnet run --project tools/Yojana.Bench -c Debug --no-build -- prepare-concurrency \
  --tenant 1 --documents 50 --engineers 120 --duration 10m --numbering gap_free

YOJANA_BENCH_CLAIM_ROUND=2 dotnet run --project tools/Yojana.Bench -c Debug --no-build -- concurrency \
  --tenant 1 --url http://127.0.0.1:5086 --engineers 120 --duration 10m \
  --numbering gap_free --out artifacts/bench-concurrency
```

Each run writes a timestamped Markdown report and CSV file to `artifacts/bench-concurrency/`. The report includes
per-operation throughput and p50/p95/p99 latency, errors by type, expected `document_changed` conflicts, platform
concurrency retries, retries exhausted, connection-acquisition time, SQL wait deltas, RMA-create Query Store timings,
published-event queue depth, deadlocks and lost updates.

Without `--tenant`, `concurrency` retains its DEV-tenant default for the small development/test case. Benchmark runs
must always pass `--tenant <index>`.

## Running again on the same database

Each run adjudicates the open claims of its numbering pool. Before another run of the same mode, prepare a fresh round
of claims: `YOJANA_BENCH_CLAIM_ROUND=3 dotnet run --project tools/Yojana.Bench -c Release --no-build -- prepare-concurrency --tenant 1 --documents 50 --engineers 120 --duration 10m --numbering sequence`
(increase the round each time). Units and refurbishment work orders are topped up or reused automatically.
