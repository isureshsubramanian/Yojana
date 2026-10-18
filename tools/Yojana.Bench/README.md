# Yojana benchmark and P6 concurrency harness

The benchmark tool targets an explicitly named SQL Server database. Never point it at a customer or development
database. The P6 concurrency run uses a benchmark tenant prepared by this tool and signs in through the real web API.
Passwords come from environment variables and are never written to a command line, report or file.

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
make reruns safe. `--documents` is the total number of editable campaigns and policy drafts, split approximately evenly.

```sh
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- seed-users --tenant 1
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- prepare-concurrency --tenant 1 --documents 50
```

The prepared set contains 2,000 customer-owned shipped units with live outbound cover, 200 returned units owned and
held by us with open refurbishment work orders, 150 submitted adjudication claims for each numbering-mode run, 50
separate editable claims, approved replacement sources, a 10,000-unit replacement-stock selection, and version-checked
campaign/policy/claim documents. Preparation finishes with a whole-unit consistency check over ownership, custody,
entitlements, identifiers and the ledger events that produced those states.

## Start the host and run both numbering modes

Use any free port. Keep `Platform__ConnectionString` and the two TLS variables in the host environment.

```sh
export ASPNETCORE_URLS=http://127.0.0.1:5086
dotnet run --project src/Web/Yojana.Web -c Debug --no-build
```

In another shell with the same benchmark variables:

```sh
dotnet run --project tools/Yojana.Bench -c Debug --no-build -- concurrency \
  --tenant 1 --url http://127.0.0.1:5086 --engineers 120 --duration 10m \
  --numbering sequence --out artifacts/bench-concurrency

dotnet run --project tools/Yojana.Bench -c Debug --no-build -- concurrency \
  --tenant 1 --url http://127.0.0.1:5086 --engineers 120 --duration 10m \
  --numbering gap_free --out artifacts/bench-concurrency
```

Each run writes a timestamped Markdown report and CSV file to `artifacts/bench-concurrency/`. The report includes
per-operation throughput and p50/p95/p99 latency, errors by type, expected `document_changed` conflicts, platform
concurrency retries, retries exhausted, number-series and SQL lock waits, deadlocks and lost updates.

Without `--tenant`, `concurrency` retains its DEV-tenant default for the small development/test case. Benchmark runs
must always pass `--tenant <index>`.
