# Developer entry points (MOD-01). Every target is also what CI runs.
SHELL := /bin/bash
# Local secrets live in .env (git-ignored), written once by `make env`; every target below inherits them.
-include .env
export YOJANA_SQL_PASSWORD YOJANA_SEED_PASSWORD YOJANA_SECURITY_AUDIT_HMAC_KEY
SLN := Yojana.slnx
WEB := src/Web/Yojana.Web
CLIENT := web
GATES := dotnet run --project tools/Yojana.Gates --
CONFIG ?= Debug
DOTNET_BUILD_FLAGS := -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
RENDER_WORKER := src/Workers/Yojana.RenderWorker

.PHONY: help env hooks build test test-db gate bench licences notices names budget self-test db-up db-down db-wait migrate seed demo run restore clean db-reset web-install web-check web-test web web-dev web-lcp render-worker browser

help:
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | sed 's/:.*## /\t/'

env: ## Write .env with generated development passwords and audit key; preserve existing values
	@if [ ! -f .env ]; then \
	  printf 'YOJANA_SQL_PASSWORD=%s\nYOJANA_SEED_PASSWORD=%s\n' "Yj$$(openssl rand -hex 10)!A1" "Dev$$(openssl rand -hex 8)!a1" > .env; \
	fi; \
	if ! grep -q '^YOJANA_SECURITY_AUDIT_HMAC_KEY=' .env; then \
	  printf 'YOJANA_SECURITY_AUDIT_HMAC_KEY=%s\n' "$$(openssl rand -base64 32)" >> .env; \
	fi; \
	echo ".env is ready (development secrets; never committed)"

restore: ## Restore packages and local tools
	dotnet restore $(SLN) && dotnet tool restore

build: ## Build the solution with warnings as errors (copies web/dist into the host's wwwroot when present)
	MSBUILDDISABLENODEREUSE=1 dotnet build $(SLN) -c $(CONFIG) --no-restore $(DOTNET_BUILD_FLAGS)

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
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Demo.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Architecture.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Integration.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Platform.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Serialisation.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.RenderWorker.Tests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)

test-db: build ## Database tests; need YOJANA_TEST_SQL (a SQL Server connection string to a disposable database)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Platform.DatabaseTests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Serialisation.DatabaseTests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Integration.DatabaseTests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)
	MSBUILDDISABLENODEREUSE=1 dotnet test tests/Yojana.Knowledge.DatabaseTests -c $(CONFIG) --no-build $(DOTNET_BUILD_FLAGS)

licences: ## Gate 1: every package carries an allow-listed licence or an approved exception
	$(GATES) licences

notices: build ## Regenerate THIRD-PARTY-NOTICES.txt from the host's shipped dependency graph
	$(GATES) notices --configuration $(CONFIG)

names: ## Gate 3: no competitor or external-product name outside the connector boundary
	$(GATES) names

budget: ## Gate 4: web client bundle budgets
	$(GATES) budget

self-test: ## Prove every gate fails on a planted violation
	$(GATES) self-test

gate: build ## All gates, as CI runs them (notices in check mode; needs `make web-install web`)
	$(GATES) all --configuration $(CONFIG)

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

demo: ## Load the IT-hardware demo dataset into the development database through module commands
	MSBUILDDISABLENODEREUSE=1 dotnet build tools/Yojana.Demo -c $(CONFIG) -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
	dotnet run --project tools/Yojana.Demo -c $(CONFIG) --no-build -- $(DEMO_ARGS)

bench: build ## P1-T21 storage benchmark against YOJANA_BENCH_SQL_HOST / YOJANA_BENCH_SQL_PASSWORD (BENCH_ARGS="measure --samples 500 ...")
	@test -n "$(YOJANA_BENCH_SQL_HOST)" || { echo "YOJANA_BENCH_SQL_HOST is not set (see .env.example)"; exit 1; }
	DOTNET_SYSTEM_NET_SECURITY_DISABLETLSRESUME=1 DOTNET_SYSTEM_NET_SECURITY_TLS13_DISABLE=1 dotnet run --project tools/Yojana.Bench -c $(CONFIG) --no-build -- $(BENCH_ARGS)

run: build ## Run the web host on http://localhost:5080
	ASPNETCORE_ENVIRONMENT=Development dotnet run --project $(WEB) -c $(CONFIG) --no-build

render-worker: ## Run the loopback PDF render worker (install the pinned browser first)
	MSBUILDDISABLENODEREUSE=1 dotnet build $(RENDER_WORKER) -c $(CONFIG) $(DOTNET_BUILD_FLAGS)
	cd $(RENDER_WORKER) && dotnet run -c $(CONFIG) --no-build

browser: ## Download, verify and install the pinned headless-shell build for this host
	MSBUILDDISABLENODEREUSE=1 dotnet build $(RENDER_WORKER) -c $(CONFIG) $(DOTNET_BUILD_FLAGS)
	cd $(RENDER_WORKER) && dotnet run -c $(CONFIG) --no-build -- --install-browser

clean:
	dotnet clean $(SLN) >/dev/null; find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

hooks: ## Install the pre-push guard that keeps internal-only paths off the remote
	ln -sf ../../scripts/check-push.sh .git/hooks/pre-push
