# Yojana

Serial number and warranty management at million-unit scale: a modular monolith on .NET 10 and
Microsoft SQL Server / Azure SQL, with a React web client. The domain design lives in the project's
architecture decision records; the build plan lives in `docs/plan/` (both are kept beside the code and
are not part of the repository history).

## Layout

| Path | What it is |
|---|---|
| `src/Platform/` | MOD-01 platform foundation: tenancy (session context + row-level security), SQL Server conventions, Wolverine messaging with the SQL Server outbox, Quartz.NET scheduling with the SQL Server job store, authentication and role policies, development seeding. `Yojana.Platform.Contracts` is the only project every module may reference besides its own and other modules' `.Contracts` projects. |
| `src/Serialisation/`, `src/Integration/` | Bounded contexts, one implementation project and one `.Contracts` project each. Modules talk through contracts, commands and events, never through each other's tables. |
| `src/Web/Yojana.Web` | The composition root and HTTP host. Serves the web client from its `wwwroot` (copied from `web/dist` at build), `/notices` (third-party attributions), `/health`, sign-in, tenant selection, the shell's overview and health endpoints, and the P0 smoke endpoints. |
| `src/Workers/Yojana.RenderWorker` | Loopback-only certificate PDF worker. It owns the bounded browser pool and the pinned headless-shell installer; the serialisation module talks to it only through the `IPdfRenderer` HTTP contract. |
| `web/` | MOD-02 web client: React shell in the Glass house theme (tokens in `src/styles/theme-glass.css`, applied through `data-theme`; light and dark through `data-mode` or the OS preference), routing with one lazy chunk per route, one inline SVG sprite, system font stack, print stylesheet. Built by esbuild alone (`web/build.mjs`); no bundler plugins, no CSS-in-JS. Shared components in `web/src/components/` (scan-first input, server-paged grid, find-within bar, selection-set preview, approval inbox, timeline rail, status badge, search palette with hover card) render server-shaped data only. Development builds add a `/patterns` gallery of them on fixture data. |
| `tools/web-perf/` | `lcp.mjs`: drives a Chromium-based browser over the DevTools protocol (no packages) to measure largest contentful paint under a 10 Mbit link and to take the design screenshots. |
| `tests/` | Architecture rules (project-reference graph), platform unit tests, and database tests that run when `YOJANA_TEST_SQL` points at a disposable SQL Server database. |
| `tools/Yojana.Gates` | The four gates: licence policy, notices generation, name audit, bundle budget, plus a self-test that plants a violation for each. |
| `tools/gates/` | Gate configuration: the licence allow-list and exceptions, the budgets, and the base64-encoded name deny-list. |
| `tools/notices-input/` | Generator inputs for notices that cannot be read from a package (repository licence texts with source URL and date, shared frameworks, vendored files). |
| `tools/sqlserver-verification/` | The P0-T08 verification scripts and harness; results are recorded in the ADR. |

## Developer environment

Prerequisites: .NET SDK 10.0.3xx (see `global.json`), Node.js 22 or later with npm, Docker, GNU make.

```
make env            # writes .env with generated development passwords (git-ignored; see .env.example)
make restore        # packages and local tools (dotnet-ef)
make web-install    # web client packages, exactly as locked
make web            # type-check and build the web client into web/dist
make db-up db-wait  # SQL Server 2022 Express in Docker on localhost:1433, sa password from .env
make seed           # migrate and seed tenant DEV with one user per role
make run            # http://localhost:5080 serves the client and the API
make web-dev        # optional: client with live reload on http://localhost:5173 (builds into web/.dev), API proxied to 5080
```

### Certificate PDF worker

The default certificate renderer remains dependency-free HTML. To enable PDF output:

```
make browser        # downloads and verifies the pinned headless-shell archive for this host
make render-worker  # loopback worker at http://127.0.0.1:5091
```

The browser comes directly from the upstream Chrome for Testing archive onto the customer host; it is
not redistributed with Yojana. The installer verifies the configured SHA-256 before unpacking, records
the installed executable hash, and keeps `LICENSE.headless_shell` beside the executable. It downloads
only `chrome-headless-shell`: it never downloads full Chrome or ffmpeg. Browser version, platform URLs,
hashes and install path are pinned in `src/Workers/Yojana.RenderWorker/appsettings.json`.

Set `Serialisation:Certificates:Renderer` to `pdf` and
`Serialisation:Certificates:PdfWorkerBaseUrl` to `http://127.0.0.1:5091` in the web-host configuration.
Leaving the renderer as `html` is the documented fallback when no worker is configured. The worker accepts
`POST /render` with `{ html, page }` (`page` contains size, orientation, four millimetre margins,
header/footer HTML and the page-number flag) and returns `application/pdf` bytes. It disables JavaScript,
blocks every page request except `data:` URLs, enforces the configured render timeout, and recycles bounded
browser slots after the configured render count.

The generated `src/Web/Yojana.Web/Notices/THIRD-PARTY-NOTICES.txt` includes PuppeteerSharp, every newly shipped
transitive package and the platform-specific `LICENSE.headless_shell` texts. Restored transitive packages supplied
by the .NET shared framework are still licence-gated, but are not duplicated as worker artefact notice blocks. The
generated file is copied into the worker artefact under
`Notices/` and is available from the loopback worker at `/notices`. The notice gate compares every configured
browser version, URL and archive hash with `tools/notices-input/components/`, so a browser bump cannot pass
without its matching notice input.

No password is built into the repository. The host's development connection string carries the login only;
`YOJANA_SQL_PASSWORD` completes it (a string with no SQL login, or with its own password, is used as is), and
the container, `make db-wait` and the seed read the same `.env`. Changing the sa password after the container
exists needs `make db-reset` (drops the development data volume) before `make db-up`.

Seeded users are `<role>@dev.yojana.local` (for example `serial_administrator@dev.yojana.local`) and
`admin@dev.yojana.local`, password `YOJANA_SEED_PASSWORD` from `.env`. Sign in with
`POST /api/auth/sign-in {"email","password"}`; with one membership the tenant is selected automatically,
otherwise `POST /api/auth/select-tenant {"tenantCode"}`. `POST /api/smoke/ping {"note"}` publishes a
message through the outbox; `GET /api/smoke` shows the request, handler and job rows for the tenant.

`make test` runs the database-free tests; `make test-db` needs `YOJANA_TEST_SQL`; `make web-test` runs the client's unit tests with Node's built-in runner. `make web-lcp` measures the
shell's largest contentful paint against a running host with a Chromium-based browser (`YOJANA_BROWSER` to pick
one) and fails above 2.0 s; CI runs it against the seeded database.

## Gates

`make gate` is what CI runs: licence policy, notices up to date, name audit, bundle budget and client source
rules. `make self-test` proves each gate fails on a planted violation. Adding a NuGet package means, in the same
change: a version in `Directory.Packages.props`, a licence the policy allows (or an exception the owner has
decided on in `tools/gates/licence-policy.json`), and `make notices` with the regenerated
`src/Web/Yojana.Web/Notices/THIRD-PARTY-NOTICES.txt` committed. Adding an npm package means the same: its
licence (development tooling included) must be allowed, and the production closure is credited from the
licence text each package ships. The notices file ships inside the artefact and is served at `/notices` and
from the user menu.

The budget gate also enforces two MOD-02 source rules: no CSS-in-JS runtime may be imported, and only
`web/src/theme/` may touch the `data-theme` and `data-mode` attributes.

The name audit forbids competitor and external-product names in source. Protocol facts of an external
system (table names, URL schemes, format identifiers) are allowed only under
`src/Integration/Yojana.Integration/Connectors/<code>/` and the migration staging adapter.

## Conventions enforced by the platform

- `tenant_id` leads every primary key and index of a tenant table; the model is validated at start.
- Surrogate keys come from `TimeOrderedId.New()`, which is time-ordered under SQL Server's
  `uniqueidentifier` comparison (a plain UUIDv7 is not; see the ADR verification notes).
- Every tenant table carries a FILTER and BLOCK predicate pair on `sec.fn_tenant_predicate`, added by the
  module's migration with `migrationBuilder.AddTenantPolicy(schema, table)`; the tenant is set once per
  connection through `SESSION_CONTEXT('tenant_id')`.
- `datetime2(3)` in UTC for every timestamp; snake_case names; one schema per module; platform
  migrations apply before module migrations.
- Messages are published inside the unit of work and delivered after commit (Wolverine outbox); jobs
  derive from `TenantJob` and are scheduled per tenant.
