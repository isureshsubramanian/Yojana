# Developer entry points (MOD-01). Every target is also what CI runs.
SHELL := /bin/bash
# Local secrets live in .env (git-ignored), written once by `make env`; every target below inherits them.
-include .env
export YOJANA_SQL_PASSWORD YOJANA_SEED_PASSWORD
SLN := Yojana.slnx
WEB := src/Web/Yojana.Web
CLIENT := web
GATES := dotnet run --project tools/Yojana.Gates --
CONFIG ?= Debug

.PHONY: help env build test test-db gate bench licences notices names budget self-test db-up db-down db-wait migrate seed run restore clean db-reset web-install web-check web-test web web-dev web-lcp

help:
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | sed 's/:.*## /\t/'

env: ## Write .env with generated development passwords (SQL sa login, seeded users) unless it exists
	@if [ -f .env ]; then echo ".env exists; leaving it alone"; else \
	  printf 'YOJANA_SQL_PASSWORD=%s\nYOJANA_SEED_PASSWORD=%s\n' "Yj$$(openssl rand -hex 10)!A1" "Dev$$(openssl rand -hex 8)!a1" > .env; \
	  echo "wrote .env (development passwords; never committed)"; fi

restore: ## Restore packages and local tools
	dotnet restore $(SLN) && dotnet tool restore

build: ## Build the solution with warnings as errors (copies web/dist into the host's wwwroot when present)
	dotnet build $(SLN) -c $(CONFIG) --no-restore

web-install: ## Install the web client's packages exactly as locked (web/package-lock.json)
	cd $(CLIENT) && npm ci --no-fund --no-audit

web-check: ## Type-check the web client
	cd $(CLIENT) && npm run check

web-test: ## Unit tests of the web client's pure logic (Node's built-in runner, no packages)
	cd $(CLIENT) && npm test

web: web-check web-test ## Build the web client into web/dist (run before `make build` so the host ships it)
	cd $(CLIENT) && npm run build

web-dev: ## Web client with live reload on http://localhost:5173, API proxied to the host on 5080
	cd $(CLIENT) && npm run dev

web-lcp: ## Measure largest contentful paint of the shell against a running host (Chromium-based browser required)
	node tools/web-perf/lcp.mjs --base "$${YOJANA_LCP_BASE:-http://localhost:5080}" --budget-ms 2000 $(LCP_ARGS)

test: build ## Unit and architecture tests (no database)
	dotnet test tests/Yojana.Architecture.Tests -c $(CONFIG) --no-build
	dotnet test tests/Yojana.Platform.Tests -c $(CONFIG) --no-build
	dotnet test tests/Yojana.Serialisation.Tests -c $(CONFIG) --no-build

test-db: build ## Database tests; need YOJANA_TEST_SQL (a SQL Server connection string to a disposable database)
	dotnet test tests/Yojana.Platform.DatabaseTests -c $(CONFIG) --no-build
	dotnet test tests/Yojana.Serialisation.DatabaseTests -c $(CONFIG) --no-build

licences: ## Gate 1: every package carries an allow-listed licence or an approved exception
	$(GATES) licences

notices: build ## Regenerate THIRD-PARTY-NOTICES.txt from the host's shipped dependency graph
	$(GATES) notices

names: ## Gate 3: no competitor or external-product name outside the connector boundary
	$(GATES) names

budget: ## Gate 4: web client bundle budgets
	$(GATES) budget

self-test: ## Prove every gate fails on a planted violation
	$(GATES) self-test

gate: build ## All gates, as CI runs them (notices in check mode; needs `make web-install web`)
	$(GATES) all

db-up: ## Start the development SQL Server container (password from .env)
	@test -n "$(YOJANA_SQL_PASSWORD)" || { echo "YOJANA_SQL_PASSWORD is not set; run: make env"; exit 1; }
	docker compose up -d sql

db-wait: ## Block until the development database answers
	@for i in $$(seq 1 60); do docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$(YOJANA_SQL_PASSWORD)" -C -Q "SELECT 1" >/dev/null 2>&1 && exit 0; sleep 2; done; echo "database not ready" && exit 1

db-reset: ## Remove the development database container and its data volume (needed after changing the sa password)
	docker compose down -v

db-down: ## Stop the development database (data volume kept)
	docker compose down

migrate: build ## Apply migrations to the development database
	dotnet run --project $(WEB) -c $(CONFIG) --no-build -- --migrate

seed: build ## Migrate and seed the DEV tenant and its users
	dotnet run --project $(WEB) -c $(CONFIG) --no-build -- --seed

bench: build ## P1-T21 storage benchmark against YOJANA_BENCH_SQL_HOST / YOJANA_BENCH_SQL_PASSWORD (BENCH_ARGS="measure --samples 500 ...")
	@test -n "$(YOJANA_BENCH_SQL_HOST)" || { echo "YOJANA_BENCH_SQL_HOST is not set (see .env.example)"; exit 1; }
	DOTNET_SYSTEM_NET_SECURITY_DISABLETLSRESUME=1 DOTNET_SYSTEM_NET_SECURITY_TLS13_DISABLE=1 dotnet run --project tools/Yojana.Bench -c $(CONFIG) --no-build -- $(BENCH_ARGS)

run: build ## Run the web host on http://localhost:5080
	ASPNETCORE_ENVIRONMENT=Development dotnet run --project $(WEB) -c $(CONFIG) --no-build

clean:
	dotnet clean $(SLN) >/dev/null; find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
