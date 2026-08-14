# Implementation Changes — Cycle 1

Implements the Todo application per `.pipeline/specs.md`. Backend (.NET 10 Web
API + EF Core 10 + Npgsql), frontend (Vite + React + TypeScript), Dockerfiles,
local `docker-compose` Postgres, and an Azure deployment runbook.

## Summary

- **Backend** `backend/` — `TodoApi.sln` with `src/TodoApi` (net10.0) and an
  empty `tests/TodoApi.Tests` xUnit project wired into the solution for the
  tester agent. Full CRUD `TodosController`, DTOs with data-annotation
  validation, `TodoDbContext` mapping the `todos` table exactly as specified,
  an `InitialCreate` EF migration, `/health` endpoint, CORS policy `frontend`,
  Swagger in Development, and auto-migrate-on-startup (guarded).
- **Frontend** `frontend/` — Vite + React 18 + TypeScript SPA. `useTodos` hook
  owns list state and CRUD orchestration (update-on-success, no optimistic
  updates); thin `fetch`-based `api.ts` client that throws on non-2xx;
  `TodoForm` / `TodoList` / `TodoItem` (inline edit, complete checkbox, delete);
  single `index.css`. API base URL from `VITE_API_BASE_URL`.
- **Containers** — multi-stage Dockerfiles for both (backend build context
  `backend/`, frontend served by nginx on 8080 with SPA fallback).
- **Local dev** — `docker-compose.yml` runs Postgres only; `.env.example`,
  `README.dev.md`.
- **Azure** — `scripts/deploy-azure.sh` is a parameterized runbook (NOT executed
  here). No Azure resources were provisioned.

## Verification performed locally

- `dotnet build TodoApi.sln -c Release` → **succeeded, 0 warnings, 0 errors**
  (using the .NET 10.0.302 SDK, see deviations).
- EF migration generated (`dotnet ef migrations add InitialCreate`); the
  generated DDL matches spec §2 (table `todos`, column types/lengths, defaults,
  `ix_todos_created_at` descending index).
- Ran the API (`dotnet run`): boots cleanly; `GET /health` → `200`
  `{"status":"ok"}`; `GET /swagger/index.html` → `200`. With no database
  reachable, migration failure is caught + logged (app does not crash) and
  `GET /api/todos` returns `500` rather than taking down the process — i.e.
  error handling is explicit, not silent.
- `npm install && npm run build` in `frontend/` → **succeeded** (tsc typecheck
  + vite production bundle).
- **Not** verified end-to-end against a real database — Docker Desktop was not
  running in this environment, so local Postgres could not be started. Once
  `docker compose up -d` works, the auto-migration path will create the schema
  on first API start. See Known limitations.

## Files created

### Backend
- `backend/TodoApi.sln` — classic-format solution referencing both projects.
- `backend/.dockerignore` — excludes bin/obj/tests from the image context.
- `backend/src/TodoApi/TodoApi.csproj` — net10.0 project + package refs.
- `backend/src/TodoApi/Program.cs` — DI, CORS, Swagger (Dev), `/health`,
  controller mapping, guarded `Database.Migrate()` at startup; exposes
  `public partial class Program` for `WebApplicationFactory` tests.
- `backend/src/TodoApi/appsettings.json` — empty `ConnectionStrings:TodoDb` /
  `Cors:AllowedOrigins` placeholders (real values via env/Dev config).
- `backend/src/TodoApi/appsettings.Development.json` — local connection string
  + CORS origin `http://localhost:5173`.
- `backend/src/TodoApi/Properties/launchSettings.json` — runs on
  `http://localhost:8080`, `ASPNETCORE_ENVIRONMENT=Development`.
- `backend/src/TodoApi/Models/Todo.cs` — EF entity.
- `backend/src/TodoApi/Dtos/CreateTodoRequest.cs` / `UpdateTodoRequest.cs` /
  `TodoResponse.cs` — request/response DTOs + validation + entity→response map.
- `backend/src/TodoApi/Data/TodoDbContext.cs` — `DbSet<Todo>` +
  `OnModelCreating` column/index config.
- `backend/src/TodoApi/Data/TodoDbContextFactory.cs` — design-time factory so
  `dotnet ef` works regardless of environment (see Assumptions).
- `backend/src/TodoApi/Controllers/TodosController.cs` — GET(list/by-id), POST,
  PUT, DELETE with 200/201/204/400/404 semantics.
- `backend/src/TodoApi/Migrations/*` — `InitialCreate` migration + snapshot.
- `backend/src/TodoApi/Dockerfile` — multi-stage SDK→aspnet, listens on 8080.
- `backend/tests/TodoApi.Tests/TodoApi.Tests.csproj` — empty xUnit project
  (references the API + `Microsoft.AspNetCore.Mvc.Testing`) for the tester.

### Frontend
- `frontend/package.json`, `frontend/package-lock.json`, `frontend/tsconfig.json`,
  `frontend/vite.config.ts`, `frontend/index.html`.
- `frontend/.env.development`, `frontend/.env.example` — `VITE_API_BASE_URL`.
- `frontend/nginx.conf`, `frontend/Dockerfile`, `frontend/.dockerignore`.
- `frontend/src/main.tsx`, `App.tsx`, `index.css`, `types.ts`, `api.ts`,
  `vite-env.d.ts`.
- `frontend/src/hooks/useTodos.ts`.
- `frontend/src/components/TodoForm.tsx`, `TodoList.tsx`, `TodoItem.tsx`.

### Root / ops
- `docker-compose.yml` — local Postgres only.
- `.env.example` — local env var template (throwaway dev creds only).
- `README.dev.md` — quickstart, API table, container build, deploy pointer.
- `scripts/deploy-azure.sh` — Azure runbook (documentation; not executed).

## Deviations from specs.md (and why)

1. **.NET 10 SDK was not installed in the environment** (only 6/8/9 present).
   To honor the mandated `net10.0` TFM and EF Core 10, I installed the official
   **.NET SDK 10.0.302** (via `dotnet-install.ps1`, to a user-local dir) and
   the `dotnet-ef` 10.0.0 global tool. No spec change — this just made the
   spec's stack buildable here. Downstream agents need a .NET 10 SDK available.

2. **Swashbuckle.AspNetCore pinned to `10.2.3`, not a 9.x.** The 9.x line is
   binary-incompatible with the `Microsoft.OpenApi 2.0.0` that .NET 10's native
   OpenAPI stack pulls in — 9.0.6 threw `TypeLoadException: 'GetSwagger' … does
   not have an implementation` at startup. Swashbuckle 10.2.3 targets the .NET
   10 / OpenApi 2.x line, boots correctly (Swagger UI verified at `/swagger`),
   and as a bonus cleared the transitive `NU1903` Microsoft.OpenApi advisory —
   the Release build is now 0 warnings. The spec named Swashbuckle without a
   version, so this is within spec intent.

3. **Solution is classic `.sln`, created with `--format sln`.** .NET 10's
   `dotnet new sln` now defaults to the newer `.slnx` XML format; the spec
   requires `TodoApi.sln`, so I forced the classic format to keep the exact
   filename the tester depends on.

4. **`UpdateTodoRequest.IsCompleted` is `bool?` + `[Required]`** rather than a
   plain `bool`. A non-nullable value type can't distinguish "omitted" from
   "false", so `[Required]` on a plain `bool` never fails. Nullable + `[Required]`
   makes an omitted `isCompleted` a genuine 400 validation error, matching the
   spec's "required boolean". The controller reads `.Value` after validation.

## Assumptions made (spec was ambiguous or silent)

- **Design-time `TodoDbContextFactory`** was added (not in the §3.2 file list)
  so `dotnet ef` commands work without a fully-configured host/environment. It
  never connects to a DB; runtime still uses the DI-registered context.
- **Two extra small files** not enumerated in the spec but required by the
  toolchains: `Properties/launchSettings.json` (so `dotnet run` binds 8080 in
  Development) and `src/vite-env.d.ts` (types for `import.meta.env`).
- **ACR pull uses admin-user credentials** stored as a Container Apps secret
  (per spec §5.5's "otherwise fall back" option) — chosen because it needs no
  extra role-assignment round-trip in the runbook. Managed-identity `AcrPull`
  is noted in-script as the production-preferred alternative.
- **Defaults**: region `eastus`, Postgres 16 Burstable `Standard_B1ms`, resource
  names from §5.3 (ACR/PG names get a `$RANDOM` suffix for global uniqueness).
  All overridable via env vars in `scripts/deploy-azure.sh`.
- **CRUD state updates on API success** (not optimistic) — the spec's stated
  preference.
- Backend runs on `http://localhost:8080` locally (matches `VITE_API_BASE_URL`
  and the container port). CORS Dev origin is `http://localhost:5173`.

## Known limitations / TODOs

- **No end-to-end DB run in this environment.** Docker Desktop was not running,
  so local Postgres never started. The migration/CRUD paths are exercised only
  insofar as the app boots and fails gracefully without a DB. Reviewer/tester
  should run `docker compose up -d` then start the API to validate the full
  path (schema is created automatically on first start).
- **Frontend `npm audit`** reports 2 advisories (esbuild/vite). These are
  **dev-server-only** issues in Vite 5's toolchain and are not present in the
  nginx-served production bundle. I kept the spec-pinned Vite 5 rather than take
  the breaking `vite@8` upgrade; revisit if a non-breaking fix ships.
- **Auto-migrate-on-startup** is intentional per spec §3.4/§9.7 (demo
  convenience). For production this should move to a discrete migration step.
- `tests/TodoApi.Tests` contains no tests yet — owned by the tester agent.

## How to run it locally

Prereqs: .NET 10 SDK, `dotnet-ef` 10, Node 22, Docker.

```bash
# 1. Postgres
docker compose up -d

# 2. Backend  (from backend/src/TodoApi)
dotnet run                     # API http://localhost:8080, Swagger /swagger
                               # migrations auto-apply on startup

# 3. Frontend (from frontend)
npm install
npm run dev                    # SPA http://localhost:5173
```

Container images:

```bash
docker build -f backend/src/TodoApi/Dockerfile -t todo-api ./backend
docker build -f frontend/Dockerfile \
  --build-arg VITE_API_BASE_URL=http://localhost:8080 -t todo-web ./frontend
```

Azure: see `scripts/deploy-azure.sh` (a human runs it after `az login`; set
`PG_ADMIN_PASSWORD` in the environment first).

---

# Cycle 2 — Fix: malformed id returns 400 (review rejection)

## Summary

Addresses the single blocking defect from `.pipeline/review.md` (cycle 1,
REJECTED): malformed (non-GUID) `{id}` path segments on the id-based endpoints
returned a bare `404` instead of the spec-mandated `400` (spec §3.5: *"`{id}` is
a `Guid`; malformed ids return `400`."*).

Root cause: the id-based actions used the `{id:guid}` route constraint. A URL
segment that is not a valid GUID fails the `:guid` constraint at the routing
layer, so ASP.NET Core reports "no endpoint matched" and returns `404` — the
action is never invoked, so no `400`/`ProblemDetails` is produced.

## Files changed

- `backend/src/TodoApi/Controllers/TodosController.cs` — removed the `:guid`
  route constraint from all three id-based actions and moved GUID validation
  into the action bodies.

## What changed (all three id-based actions: GET, PUT, DELETE by id)

- Route template `{id:guid}` → `{id}` (no constraint).
- Action parameter `Guid id` → `string id`.
- Each action now calls `Guid.TryParse(id, out var todoId)` first; on failure it
  returns a `400` via a new private `MalformedId(string)` helper. On success it
  proceeds with the existing logic using the parsed `todoId`.
- Added `[ProducesResponseType(StatusCodes.Status400BadRequest)]` to `GetTodo`
  and `DeleteTodo` (PUT already declared 400 for body validation) so the OpenAPI
  doc reflects the new response.
- New `private BadRequestObjectResult MalformedId(string id)` helper returns a
  `ValidationProblemDetails` (an `errors` dictionary keyed by `"id"`), which is a
  `ProblemDetails`-shaped body per spec §3.5 and matches what the frontend's
  existing `extractErrorMessage` reads (`.errors` / `.title`) — per the
  reviewer's non-blocking suggestion #2.

`CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, ...)` in `CreateTodo` is
unchanged and still works: the `Guid` route value serializes into the `{id}`
template correctly.

No other actions/routes used the `{id:guid}` pattern (verified by search — the
only remaining `:guid` occurrence is in an explanatory code comment).

## Before / after route behavior

| Request                          | Before (cycle 1) | After (cycle 2) |
|----------------------------------|------------------|-----------------|
| `GET /api/todos/not-a-guid`      | `404` (bare)     | `400` `ProblemDetails` |
| `PUT /api/todos/not-a-guid`      | `404` (bare)     | `400` `ProblemDetails` |
| `DELETE /api/todos/not-a-guid`   | `404` (bare)     | `400` `ProblemDetails` |
| `GET /api/todos/{unknown-guid}`  | `404`            | `404` (unchanged) |
| Valid GUID, existing todo        | `200`/`204`      | `200`/`204` (unchanged) |

## Verification

Run with the user-local .NET 10 SDK on `PATH` (`C:\Users\ingda\dotnet10`, per the
cycle-1 environment note):

- `dotnet build TodoApi.sln -c Release` → **succeeded, 0 warnings, 0 errors**.
- `dotnet test TodoApi.sln -c Release` → **27 passed, 0 failed, 0 skipped**. The
  three previously-failing tests
  (`GetTodo/UpdateTodo/DeleteTodo_WithMalformedId_ShouldReturn400PerSpec`) now
  pass; the other 24 remain green (no regressions).

## Scope

Frontend and Azure deployment artifacts were not touched (reviewer confirmed
they are fine). No spec deviations introduced by this fix.

---

# 2026-07-24 — CI/CD-readiness pass for the app code (specs §11 extension)

## Summary

The spec gained a §11 "CI/CD & Infrastructure-as-Code" section and an
`architecture-memory.md`. IaC/workflow authoring (Bicep, GitHub Actions,
`azure-pipelines.yml`) belongs to the downstream `devops` subagent and was
deliberately **not** touched here. This pass was a narrow audit of the
**existing application code** to confirm it can be built, tested, and deployed
by an automated pipeline with no local Postgres and no interactive steps.

**Result: no application code changes were required.** Every §11 CI/CD-readiness
concern was already satisfied by the app as built in the prior cycles. The five
items requested were each verified rather than modified. Details below so the
reviewer can see what was checked (and why nothing needed fixing).

## What was checked and verified (no changes made)

1. **`dotnet test` runs headlessly with no Postgres.** `TodoApiFactory`
   (`backend/tests/TodoApi.Tests/TodoApiFactory.cs`) swaps the Npgsql-backed
   `TodoDbContext` for EF Core **InMemory** and injects a dummy (never-dialled)
   connection string purely to satisfy `Program.cs`'s startup guard. No test
   opens a real DB connection at build or test time. Verified:
   `dotnet test TodoApi.sln -c Release` → **29 passed, 0 failed** on a machine
   with no Postgres running.

2. **`npm run build` and the frontend suite run headlessly in CI.** `build` is
   `tsc && vite build` (non-interactive) and `test` is `vitest run` (single-run,
   not watch). Verified: `npm run build` produces `dist/` cleanly and
   `npm test` → **37 passed, 6 files**. `npm ci` also verified to exit 0, so the
   committed `package-lock.json` is in sync (a stale lockfile would hard-fail
   the CI `npm ci` step).

3. **Connection string and CORS origin come from config/env, not hardcode.**
   `Program.cs` reads `ConnectionStrings:TodoDb` via `GetConnectionString` and
   `Cors:AllowedOrigins` via `GetSection(...).Get<string[]>()`. Both bind from
   the `ConnectionStrings__TodoDb` / `Cors__AllowedOrigins__0` env vars that the
   CD workflow (§11.5) and Bicep (§11.3) inject as Container Apps secrets/env
   vars. `appsettings.json` ships **empty** placeholders (no secrets committed);
   real localhost values live only in `appsettings.Development.json`. Confirmed
   no origins/connection strings are string-literal in code.

4. **`/health` is fast and dependency-free.** `Program.cs` maps
   `GET /health` → `Results.Ok(new { status = "ok" })`. It does **not** touch
   the DB, is registered after `UseCors` but CORS only adds response headers
   (it never blocks a non-browser smoke-check `curl`), and there is **no auth
   middleware** gating it. The startup `db.Database.Migrate()` is separate,
   spec-mandated (§3.4), and already wrapped in try/catch-and-log so a DB outage
   never crashes the process or the `/health` path. So Container Apps probes and
   the CD post-deploy smoke check will not flake on the endpoint itself.

5. **Other automation gaps — none found. Specifically confirmed OK:**
   - **No `UseHttpsRedirection`** in the pipeline. Correct for Container Apps
     (TLS terminates at ingress; the container serves plain HTTP on :8080).
     Had it been present, an HTTP `/health` probe would 307-redirect and the
     smoke check could flake — verified absent.
   - **`.gitignore` is comprehensive** (`bin/`, `obj/`, `node_modules/`,
     `dist/`, real `.env` files) and `git ls-files` shows **zero** tracked build
     artifacts, so a fresh CI checkout builds from clean sources.
   - **Backend Docker build does not depend on the test project** — the
     Dockerfile restores/publishes only `src/TodoApi/TodoApi.csproj`, and
     `backend/.dockerignore` excludes `tests/`, `bin/`, `obj/`. The frontend
     `.dockerignore` excludes `node_modules`/`dist`, so `COPY . .` after
     `npm ci` won't clobber the installed deps.
   - **No localhost URL leaks into the production frontend bundle.** Only
     `.env.development` and `.env.example` exist; `vite build` runs in
     `production` mode and does not load `.env.development`, so the baked
     `VITE_API_BASE_URL` comes solely from the Docker `--build-arg` (or is empty
     in a credential-free CI build-check, which `api.ts` tolerates via `?? ''`).

## Deviations from specs.md

None. No code was changed, so no new deviations were introduced.

## Assumptions made

- Interpreted "make the app CI/CD-ready" as *verify and fix only concrete gaps*,
  not add new features. Since no gap was found, the correct action was to change
  nothing and document the verification, rather than invent work (e.g. I did not
  add ASP.NET Core Health Checks middleware, a `HEALTHCHECK` Docker instruction,
  or a `npm run lint` script — the spec's §11.4 CI jobs don't require them and
  the current `/health` + `tsc` typecheck already satisfy the stated needs).

## Known limitations / notes for the devops stage

- `npm audit` reports dev-toolchain advisories (esbuild/vite 5), carried over
  from prior cycles. They affect only the Vite dev server, not the nginx-served
  production bundle, and the §11.4 CI frontend job (`npm ci` + `npm run build`)
  does **not** run `npm audit`, so they will not fail CI. If the devops agent
  adds an `npm audit --audit-level=high` gate, it must set the level to avoid
  failing on these known dev-only advisories (or accept the Vite 5→newer bump).
- Scale-to-zero backend (min replicas 0) means the CD smoke check triggers a
  cold start that includes the synchronous startup migration; the `/health`
  endpoint itself is instant once the app is listening. This is inherent to the
  spec's migrate-at-startup design (§3.4) and was intentionally left unchanged.

## How to verify

```bash
# prepend the .NET 10 SDK, per prior-cycle env note
export PATH="/c/Users/ingda/dotnet10:$PATH"
cd backend && dotnet build TodoApi.sln -c Release && dotnet test TodoApi.sln -c Release
cd ../frontend && npm ci && npm run build && npm test
```

## Security & PCI DSS scope

Unchanged from prior cycles. This is a no-auth Todo demo that processes **no
payment or cardholder data** — PCI DSS is not in scope for any part of the
system. The only security-relevant items this pass confirmed remain intact:
secrets are injected via env/Container Apps secrets (never committed — verified
no tracked `.env`/connection strings), CORS stays restricted to the configured
frontend origin, TLS-to-Postgres is enforced by the deploy-time connection
string (`Ssl Mode=Require`), and EF Core parameterizes all queries. No new
attack surface was introduced because no code changed.

---

# Implementation Changes — Observability + Identity Policy (2026-07-28)

Branch: `pipeline/appinsights-tracing` (off `main`). Cycle 1 of up to 3 for the
combined tracing (§12) + identity/secrets policy (§13) scope. Backend only —
no frontend, Bicep/IaC, or pipeline YAML touched (those are devops/tester stages).

## Summary

Implemented the two backend capabilities specified in specs.md §12 and §13:

1. **Distributed tracing to Application Insights (§12).** Added
   `Azure.Monitor.OpenTelemetry.AspNetCore` and wired OpenTelemetry so every
   inbound request produces one W3C-`traceId`-correlated trace spanning the
   ASP.NET Core request, outbound `HttpClient` calls, and Npgsql DB calls.
   Existing `ILogger`/console logging is unchanged and becomes trace-correlated
   automatically via `Activity.Current`. When
   `APPLICATIONINSIGHTS_CONNECTION_STRING` is unset (local dev / CI) the exporter
   cleanly no-ops — in-process tracing still flows, nothing is exported, nothing
   throws.

2. **Managed-identity Postgres authentication (§13.4).** Added `Azure.Identity`
   and a config-gated (`Postgres__UseEntraAuth`) code path that authenticates to
   PostgreSQL Flexible Server with an Entra token (scope
   `https://ossrdbms-aad.database.windows.net/.default`) supplied to Npgsql via
   its **periodic** password provider (auto-refresh, not a one-shot fetch).
   When the flag is false/unset (local Docker dev, and the deployed env until
   devops cuts over) the existing password-based connection path is used
   unchanged.

Additionally, per §13.5, Application Insights ingestion is authenticated with the
Container App's managed identity (`options.Credential`) rather than the
connection string's embedded key, and the AI connection string is treated as a
**plain non-secret env var** (no Container Apps secret, no Key Vault).

## Files changed/created

- `backend/src/TodoApi/TodoApi.csproj` — added package references:
  `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0, `Npgsql.OpenTelemetry` 10.0.3,
  `Azure.Identity` 1.21.0. (Transitively pulled `Azure.Core` 1.60.0 and the
  OpenTelemetry AspNetCore/Http instrumentation packages.)
- `backend/src/TodoApi/Observability/TelemetryRegistration.cs` — **new.**
  `AddTodoTelemetry(...)` extension: always registers in-process OpenTelemetry
  tracing (ASP.NET Core + HttpClient + Npgsql spans); adds the Azure Monitor
  exporter with managed-identity auth only when the AI connection string is
  present. Single responsibility: telemetry wiring only.
- `backend/src/TodoApi/Data/TodoDbContextRegistration.cs` — **new.**
  `AddTodoDbContext(...)` extension: assembles the `TodoDbContext` registration,
  selecting password auth (default) vs. Entra/managed-identity auth
  (`Postgres:UseEntraAuth=true`) with an Npgsql periodic-password-provider
  `NpgsqlDataSource` (registered as a singleton so DI disposes it). Single
  responsibility: connection assembly / auth-mode selection.
- `backend/src/TodoApi/Program.cs` — replaced the inline connection-string read +
  `AddDbContext` with `builder.Services.AddTodoTelemetry(...)` and
  `builder.Services.AddTodoDbContext(...)`. Controllers, Swagger, CORS, `/health`,
  and the startup `Database.Migrate()` try/log block are untouched. The
  missing-connection-string guard moved into `AddTodoDbContext` (same exception
  message, same behavior).
- `backend/src/TodoApi/appsettings.json` — added an explicit
  `"Postgres": { "UseEntraAuth": false }` default so the new flag is
  discoverable; overridden to `true` in Azure via the `Postgres__UseEntraAuth`
  env var.

`Data/TodoDbContextFactory.cs` (EF Core design-time factory) was intentionally
left unchanged — it is used only by `dotnet ef` locally and correctly stays on
the local password/env-var connection (design-time never runs in Azure; runtime
`Migrate()` uses the DI-registered context, which honors the Entra path).

## Key decisions

- **Conditional exporter, always-on in-process tracing (deviation from the
  literal §12.3 snippet — see below).** OpenTelemetry tracing + instrumentation
  is registered unconditionally so `traceId`/`spanId` flow onto logs and the
  §12.8 in-process correlation tests work with no live endpoint; `UseAzureMonitor`
  is only invoked when the connection string is present. This guarantees the
  §12.5 / Feature-1 "no-op cleanly, do not throw when unset" requirement and
  avoids double-registering the ASP.NET Core / HttpClient instrumentation that
  the distro adds (each instrumentation is added in exactly one branch; Npgsql is
  added once for both modes).
- **`DefaultAzureCredential` for both boundaries.** §13.4's Postgres example uses
  `DefaultAzureCredential`; §13.5's AI example uses `ManagedIdentityCredential`
  but explicitly permits `DefaultAzureCredential` as an alternative. I used
  `DefaultAzureCredential` for both: it resolves to the managed identity inside
  ACA, is consistent across the two paths, and avoids a CS0618 obsolete-
  constructor warning on the parameterless `ManagedIdentityCredential` in
  Azure.Identity 1.21.0 (build is warning-clean as a result). The credential is
  only constructed when its trigger is active (AI connection string present /
  `UseEntraAuth=true`), so it is never invoked during local dev.
- **Npgsql periodic password provider**
  (`UsePeriodicPasswordProvider(cb, 50min, 5s)`) — the token is refreshed on a
  schedule ahead of the ~60-min expiry rather than fetched once, per §13.4.
  Verified the API exists on `NpgsqlDataSourceBuilder` in Npgsql 10.0.3.
- **`NpgsqlDataSource` as a singleton** in the Entra path so the token provider /
  connection pool are shared and disposed by the container.
- **AI connection string is non-secret config** (§13.5): read from
  `APPLICATIONINSIGHTS_CONNECTION_STRING`, never placed in `appsettings*.json`,
  never hardcoded.

## Deviations from specs.md

1. **§12.3 shows `AddOpenTelemetry().UseAzureMonitor()` called unconditionally;
   this implementation calls `UseAzureMonitor` only when
   `APPLICATIONINSIGHTS_CONNECTION_STRING` is set.** Justification: Feature-1's
   explicit requirement and §12.5 mandate that an unset connection string must
   no-op cleanly and not throw, and §13.5 requires the managed-identity credential
   to never be set/invoked locally. Gating the exporter call satisfies both while
   still leaving in-process tracing fully active (so §12.8's tracer-provider and
   trace-context tests pass with no endpoint). This is a behavior-preserving
   refinement of the spec's illustrative snippet, not a scope change.
2. **§12.3/§13.5 name `ManagedIdentityCredential`; used `DefaultAzureCredential`
   instead** (explicitly permitted by §13.5). Rationale above (consistency +
   avoids an obsolete-constructor warning). Functionally equivalent inside ACA.

No other deviations. No changes to controllers, DTOs, the data model/migrations,
CORS, health, or the startup-migration logic.

## Assumptions made

- The managed identity's Postgres **role name** arrives as the `Username=` in the
  deployed (passwordless) connection string, assembled at the infra/devops layer
  (§13.4) — the backend code reads whatever `ConnectionStrings__TodoDb` provides
  and does not construct the host/username itself.
- `Postgres__UseEntraAuth` is set (`true`) on the `todo-api` Container App by
  devops and left unset locally; unset resolves to `false` (`GetValue<bool>`),
  preserving the existing local password path with zero config changes needed.
- The in-DB `pgaadauth_create_principal` grant and the AI role assignment
  (`Monitoring Metrics Publisher`) / `DisableLocalAuth` are devops/manual-human
  steps (§13.4/§13.5/§13.10) and are out of scope for this backend-code cycle.

## Known limitations / TODOs (for later stages, not this cycle)

- Verified against a live Azure Postgres / App Insights: not possible in this
  environment. The Entra token path was exercised locally only to the point of a
  (caught) connection attempt; a human should confirm end-to-end auth after
  devops enables Entra on the server and runs the `pgaadauth` bootstrap.
- Devops must: deliver `ConnectionStrings__TodoDb` (passwordless) and
  `APPLICATIONINSIGHTS_CONNECTION_STRING` as **plain env vars**, set
  `Postgres__UseEntraAuth=true`, add the AI Bicep module + `Monitoring Metrics
  Publisher` role + `DisableLocalAuth=true`, enable Entra auth on Postgres, and
  drop the `todo-db-connection` / `appinsights-connection` Container Apps secrets
  (§13.10).
- Tester owns the §12.8 in-process trace-correlation / env-var-absence tests.

## How to run / verify

```
export PATH="/c/Users/ingda/dotnet10:$PATH"     # SDK 10.0.302
cd backend
dotnet build -c Release        # clean: 0 warnings, 0 errors
dotnet test  -c Release        # 29/29 passed (unchanged, EF Core InMemory)
```

Verification performed this cycle:
- `dotnet build -c Release` — succeeded, **0 warnings / 0 errors**.
- `dotnet test -c Release` — **29/29 passed** (existing suite, unchanged).
- Startup smoke tests (Production env, no DB running so `Migrate()` fails and is
  caught, then `/health` polled):
  - No `APPLICATIONINSIGHTS_CONNECTION_STRING` → app starts, `/health` = `200
    {"status":"ok"}`, no Azure Monitor error (clean no-op).
  - Dummy `APPLICATIONINSIGHTS_CONNECTION_STRING` set (exporter + managed-identity
    credential wired) → app starts, `/health` = `200`.
  - `Postgres__UseEntraAuth=true` (Entra `NpgsqlDataSource` + periodic token
    provider built) → app starts, `/health` = `200`; token/connection attempt
    fails only because there is no local managed identity / server, and is caught
    by the existing migration try/log.

## Security & PCI DSS scope

Unchanged and still **not in PCI DSS scope** — this is a no-auth Todo demo that
processes no payment or cardholder data; no PAN/CVV/track data anywhere. This
cycle **strengthens** the security posture per the managed-identity-first policy
(§13):

- **Secrets removed, not added.** The Postgres password and the AI connection
  string are no longer credentials in the deployed system — Postgres uses an
  Entra token via managed identity, and (once devops disables local auth on the
  AI component) the AI connection string is a non-sensitive resource identifier.
  No secret, key, or connection string is hardcoded or committed (verified: AI
  string is env-only and absent from `appsettings*.json`; the only committed
  credential remains the throwaway local Docker `todo` password in
  `appsettings.Development.json`, per §6.3).
- **No secrets in logs.** Tracing correlates logs by `traceId`/`spanId` only; no
  tokens, connection strings, or credentials are logged. Npgsql DB spans capture
  SQL text/metadata, not credentials (the Entra token is passed as the connection
  password and is never emitted to a span or log).
- **OWASP-relevant risks:** no new injection surface (EF Core parameterized
  queries unchanged; no raw SQL added). Sensitive-data-exposure risk is reduced
  by eliminating the stored DB password. Two residual items are **owned by devops/
  infra, not closable in backend code this cycle**: (a) `Trust Server
  Certificate=true` in the connection string encrypts but skips cert verification
  (MITM-susceptible) — production should move to `SslMode=VerifyFull` with the
  Azure Postgres CA (already flagged in prior review); (b) disabling AI local auth
  (`DisableLocalAuth=true`) and granting `Monitoring Metrics Publisher` must be
  done in Bicep for the managed-identity telemetry auth to actually take effect —
  until then the connection-string key would still be a usable credential.

---

# Implementation Changes — Npgsql Entra first-connection fix (2026-07-28)

Branch: `fix/npgsql-entra-first-connection` (off `pipeline/appinsights-tracing`).
Backend code-only, targeted bug fix. No infra/Bicep, frontend, or CI/CD YAML
touched. Fixes the live outage documented in
`deployment-lessons-learned.md` §5a.

## The bug (recap)

Flipping `Postgres__UseEntraAuth=true` on the live `todo-api` Container App made
the startup EF `Migrate()` (and every DB request) fail on the **first** connection
with `Npgsql.NpgsqlException: No password has been provided but the backend
requires one (in cleartext)`. The password came back **empty**, not wrong. Rolled
back to password auth immediately.

## Research findings — Npgsql's actual documented behavior (verified, not assumed)

Inspected the installed **Npgsql 10.0.3** assembly + its shipped XML docs
(`~/.nuget/packages/npgsql/10.0.3/lib/net10.0/Npgsql.xml`) and confirmed method
signatures/attributes by reflection:

- `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider(...)` remarks state verbatim:
  **"The provided callback is invoked in a timer, and not when opening connections.
  It therefore doesn't affect opening time."**
- `NpgsqlDataSourceBuilder.UsePasswordProvider(Func<…,string> sync,
  Func<…,CancellationToken,ValueTask<string>> async)` remarks state verbatim:
  **"Configures a password provider, which is called by the data source when opening
  connections."** — one callback invoked on `Open()`, the other on `OpenAsync()`.
- Neither method is marked `[Obsolete]` in 10.0.3 (checked via reflection).

**Root cause — the user's hypothesis is CONFIRMED by Npgsql's own docs.** With
`UsePeriodicPasswordProvider`, the token is fetched on a background timer that is
*not* on the connection-open path. `Build()` returns before the first timer tick
completes; the app then calls `db.Database.Migrate()` (a **synchronous** open —
verified in `Program.cs:60`) immediately, which races ahead of that first fetch and
finds an empty password → the exact "no password ... in cleartext" error, on the
first-ever connection. The GSSAPI `libgssapi_krb5.so.2` log line was correctly a red
herring (noted in §5a).

**Chosen fix vs. the hypothesised fix.** The user's suggested fix (eagerly fetch a
token and seed `Password=` while *keeping* the periodic provider) would also work,
but the better-documented, cleaner fix is Npgsql's newer `UsePasswordProvider`,
which is *designed* to run on the connection-open path — so the token is guaranteed
present before the first auth handshake, with no timer to race, and no dead
`Password=` seed to reconcile against the rotating provider. `TokenCredential`
caches tokens in-memory and only hits the network near expiry, so calling it per
physical open is cheap and still handles the ~60-minute rotation (no separate timer
needed). Both the sync and async callbacks are supplied, because startup `Migrate()`
takes the sync `Open()` path while request-time EF operations take `OpenAsync()`.

## Fix applied

- `src/TodoApi/Data/EntraTokenPasswordProvider.cs` (**new**): small, single-purpose,
  testable class holding the injected `TokenCredential` and the Postgres AAD scope.
  Exposes `GetPassword` (sync) and `GetPasswordAsync` (async) — the two callbacks
  Npgsql invokes on connection open.
- `src/TodoApi/Data/TodoDbContextRegistration.cs`: `BuildEntraAuthenticatedDataSource`
  now wires `dataSourceBuilder.UsePasswordProvider(provider.GetPassword,
  provider.GetPasswordAsync)` instead of `UsePeriodicPasswordProvider(...)`. Added an
  `internal` overload that takes the `TokenCredential` as a parameter (the public path
  passes `new DefaultAzureCredential()`), so token acquisition is injectable for
  tests. Removed the now-unused `TokenRefreshPeriod`/`TokenRefreshFailureRetry`
  fields. **Public contract unchanged**: the `Postgres:UseEntraAuth` gate, the
  `UseEntraAuthKey` constant, the password-auth fallback path, and the
  `AddTodoDbContext(IServiceCollection, IConfiguration)` signature are all untouched.
- `src/TodoApi/TodoApi.csproj`: added `<InternalsVisibleTo Include="TodoApi.Tests" />`
  so the test project can reach the internal seam.
- `tests/TodoApi.Tests/TodoApi.Tests.csproj`: added `Azure.Identity` 1.21.0 (for
  `TokenCredential`/`AccessToken` to build a fake credential in tests).

## Test(s) added — the test that would have caught this

`tests/TodoApi.Tests/EntraTokenPasswordProviderTests.cs` (**new**), using a fake
instrumented `RecordingTokenCredential` (no network, no live server). It proves the
property the old tests never asserted — that a **real, non-empty token is produced
on the connection-open seam**, not deferred to a background timer:

- `GetPassword_ReturnsFetchedTokenSynchronously_AndActuallyInvokesCredential` — the
  synchronous `Open()` path (the one startup `Migrate()` uses) returns the token and
  actually invokes the credential (call count = 1).
- `GetPasswordAsync_ReturnsFetchedToken_AndActuallyInvokesCredential` — async path.
- `GetPassword_NeverReturnsNullOrEmpty` — asserts directly against the exact live
  failure mode (empty password → "no password ... in cleartext").
- `GetPassword_RequestsTheAzurePostgresAadScope` — the correct
  `https://ossrdbms-aad.database.windows.net/.default` scope is requested.
- `Constructor_NullCredential_Throws`, `Constructor_BlankScope_Throws` (x2 Theory).
- `BuildEntraAuthenticatedDataSource_WithInjectedCredential_BuildsWithoutThrowing`.
- `BuildEntraAuthenticatedDataSource_DoesNotAcquireTokenUntilAConnectionIsOpened` —
  building the data source alone triggers **zero** credential calls, documenting that
  acquisition is tied to the open path (where it's guaranteed present).

## What remains UNVERIFIED without a live Postgres server (human to do)

These tests prove the token is available at the moment Npgsql asks for it (open
time) — the property that was previously violated. They **cannot** prove the
end-to-end first-connection against the real Entra-enabled Postgres Flexible Server
succeeds, because there is no live server in this environment. The final live check
(flip `Postgres__UseEntraAuth=true` on `todo-api`, confirm startup `Migrate()` and
DB requests authenticate on the first connection) is left for the human to run
against the deployed server, per the task. Npgsql does not expose the resolved
password publicly, so opening a real connection is the only way to exercise the full
data-source → provider → auth-handshake path.

## Build & test verification

Ran from `backend` with `C:\Users\ingda\dotnet10` prepended to PATH:

- `dotnet build -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `dotnet test -c Release` → **Passed! Failed: 0, Passed: 53, Skipped: 0,
  Total: 53** (was 44 before; +9 new Entra-auth regression cases). All pre-existing
  tests still pass.

## Security & PCI DSS scope

No change to PCI scope — this app processes to-do items, not payment/cardholder
data; no PAN/CVV/track data anywhere, so PCI DSS is not engaged. The fix is
security-relevant only in that it **strengthens** the credential posture: the DB
password is a short-lived Entra access token acquired via managed identity on the
connection-open path, never stored and never logged (Npgsql passes it as the
connection password; it is not emitted to any span or log). No secret/key/credential
is hardcoded. `InternalsVisibleTo` widens visibility only to the test assembly, not
across a trust boundary. The pre-existing `Trust Server Certificate=true` MITM
caveat (devops-owned, flagged in the prior cycle) is unchanged by this fix.

---

# Implementation Changes — Entra-ONLY Postgres auth for the application (2026-08-06)

Branch: `pipeline/entra-passwordless-connection-string`. Implements **specs.md §14**
(§14.4 config contract, §14.5 backend behavior, §14.6 local dev). §1–§13 are prior
cycles and were not touched.

**Not in this change (by instruction and by spec §14.8):** `infra/*.bicep` and
`infra/README.md` are the `devops` agent's job in a later pipeline step, working from
the same spec. They still reference `Postgres__UseEntraAuth` — that is expected and is
*their* work item, not drift introduced here.

## Summary

The application now authenticates to PostgreSQL with **Entra / managed identity only,
unconditionally, in every environment including local development**. There is one code
path, one configuration value, and zero database passwords in application config.

1. **`Postgres:UseEntraAuth` and the password-auth branch are deleted.**
   `AddTodoDbContext` always registers the Entra-authenticated `NpgsqlDataSource`
   (lazy singleton factory) and builds `TodoDbContext` over it. The public
   `UseEntraAuthKey` const is gone.
2. **New pure normalizer `BuildEntraConnectionString`** (`internal static`, no I/O, no
   ambient state) applies §14.5 steps 1–6 using `NpgsqlConnectionStringBuilder` (never
   hand-rolled parsing, so keyword aliases like `pwd` and quoting are handled):
   - strips **both** `Password` and `Passfile` — this is the actual live bug from
     §14.1(a): Npgsql's `PrepareConfiguration()` throws
     `NotSupportedException: "...a password or password file may not be set"` if
     *either* is present alongside a password provider;
   - **fails fast** with a message naming `ConnectionStrings:TodoDb` and `Username`
     when `Username=` is missing;
   - **forces `SslMode=Require` for non-loopback hosts only** when the resolved mode is
     `Disable`/`Allow` (the Entra token travels in the cleartext-password field).
     `localhost` / `127.0.0.1` / `::1` are left exactly as configured so local dev is
     not forced into TLS the container does not serve. `Prefer` / `Require` /
     `VerifyCA` / `VerifyFull` are never changed;
   - preserves every other keyword verbatim (`Host`, `Port`, `Database`,
     `Trust Server Certificate`, pooling/timeouts, keywords we do not know about).

   It returns an `internal sealed record EntraConnectionString` carrying the normalized
   string plus the non-sensitive facts the startup log needs. (Spec offered
   `out NormalizationReport` or a record and left the shape to the engineer; the record
   keeps the function pure and directly assertable.)
3. **Startup logging (§14.5).** One `Information` line
   `"Postgres auth: Entra / managed identity. Host={Host} Database={Database} Username={Username}"`,
   plus the two conditional `Warning` lines (credential stripped / SslMode forced).
   Emitted once, at singleton construction — not per connection, not per request.
   Category `TodoApi.Data.TodoDbContextRegistration`. The `ILogger` parameter is
   optional and every call site is null-safe, so tests can build a data source with no
   logger. **No token, password, or full connection string is ever logged.**
4. **`UsePasswordProvider` is unchanged**, as required. `EntraTokenPasswordProvider` is
   byte-for-byte unchanged. `UsePeriodicPasswordProvider` is *not* reintroduced.
5. **Local dev is passwordless**: `POSTGRES_HOST_AUTH_METHOD: trust`, no
   `POSTGRES_PASSWORD`, no `Password=` in `appsettings.Development.json`, `.env.example`,
   or the EF design-time factory.

## Files changed

| File | Purpose of the change |
|---|---|
| `backend/src/TodoApi/Data/TodoDbContextRegistration.cs` | Rewritten: single unconditional Entra path, new pure `BuildEntraConnectionString` normalizer + `EntraConnectionString` record, startup logging, all dual-mode prose/XML docs corrected. |
| `backend/src/TodoApi/Program.cs` | Comment now reads "PostgreSQL DbContext. Entra / managed-identity auth only (specs §14)." |
| `backend/src/TodoApi/appsettings.json` | Deleted the `"Postgres": { "UseEntraAuth": false }` section (it became empty). `ConnectionStrings:TodoDb: ""` kept as-is. |
| `backend/src/TodoApi/appsettings.Development.json` | Connection string is now `Host=localhost;Port=5432;Database=tododb;Username=todo;Ssl Mode=Disable` — no `Password=`. |
| `backend/src/TodoApi/Data/TodoDbContextFactory.cs` | **Not named in the spec** — its hard-coded design-time fallback still carried `Password=todo`, violating §14.4's "delete every occurrence". Now passwordless. |
| `docker-compose.yml` | `POSTGRES_HOST_AUTH_METHOD: trust`, `POSTGRES_PASSWORD` removed, with the `down -v` caveat documented inline. |
| `.env.example` | Passwordless template: `POSTGRES_HOST_AUTH_METHOD=trust`, no `POSTGRES_PASSWORD`, passwordless `ConnectionStrings__TodoDb`. |
| `README.dev.md` | Prerequisites + "Run locally" step 1 rewritten for trust auth, the one-time `docker compose down -v`, the `az login` guidance, and the documented `trust` trade-off. |
| `backend/tests/TodoApi.Tests/TodoDbContextRegistrationTests.cs` | Rewritten for the new contract (see below). |
| `backend/tests/TodoApi.Tests/TodoApiFactory.cs` | Dropped `Password=unused` from the injected dummy connection string (§14.10 sanctions fixing it here). |
| `backend/tests/TodoApi.Tests/ObservabilityTests.cs` | Same one-line dummy-connection-string cleanup, for the same reason. |

`EntraTokenPasswordProvider.cs` and `EntraTokenPasswordProviderTests.cs` are
deliberately unchanged (§14.12), and still pass.

## Tests

The parent task explicitly assigned test updates to this step (the existing tests
encoded the deleted flag/branch and would neither compile nor pass otherwise).
`TodoDbContextRegistrationTests` was **rewritten**, not extended, because its central
assertion — that an unset flag produces a `Password=`-bearing DbContext connection
string — is precisely the behavior §14 deletes. The class XML doc states that reason
in-file for the tester and reviewer.

Coverage maps to §14.10: regression 1–2 (a production-shaped `Password=` string builds
and resolves through DI without throwing), normalizer 3–11 (`Password` / `pwd` /
`PASSWORD` / `Passfile` stripping; missing, empty and whitespace `Username` →
`InvalidOperationException` naming both `ConnectionStrings:TodoDb` and `Username`;
unparseable input wrapped; keyword preservation including `Command Timeout=45` and
`Maximum Pool Size=27`; `Disable`/`Allow` → `Require` on a remote host;
`Require`/`VerifyCA`/`VerifyFull`/`Prefer`/omitted left alone; loopback exemption for
`localhost`, `LOCALHOST`, `127.0.0.1`, `::1`; idempotence), structural 12–13 (exactly
one `NpgsqlDataSource` singleton backing the context; the registration stays lazy;
`Postgres:UseEntraAuth` set to `true` **or** `false` changes nothing), and 14 (the
blank/missing connection-string guard, unchanged). Three added tests assert the startup
logging, including that a `Password=` value never appears in any log entry. Every
assertion re-parses the result into an `NpgsqlConnectionStringBuilder` rather than
string-matching raw output, per §14.10.

Note: `NpgsqlConnectionStringBuilder.TrustServerCertificate` is `[Obsolete]` in Npgsql 10
("no longer needed and does nothing"), so the preservation test reads that keyword via
the builder's indexer. The keyword still round-trips; whether it remains meaningful to
the driver is an infra-side question, out of scope here.

## §14.6 open question — ANSWERED EMPIRICALLY

> *"Verify whether Npgsql invokes the password provider when the server replies
> `AuthenticationOk` immediately, and record the observed behaviour."*

**Answer: it does NOT. `az login` is a convenience locally, not a hard prerequisite.**

Method (all actually executed, not reasoned about):

1. `docker compose down -v && docker compose up -d` with the new `trust` config;
   confirmed the `pg_hba.conf` generated inside the container is `trust` on every line
   (including the appended `host all all all trust`).
2. A throwaway console probe (scratchpad, not committed) built an
   `NpgsqlDataSourceBuilder` with `UsePasswordProvider` against
   `Host=localhost;...;Username=todo;Ssl Mode=Disable` — the exact mechanism the app uses:
   - **Probe A** (counting provider; both the async and the synchronous open paths
     exercised): connected as `todo`; **sync provider calls = 0, async provider
     calls = 0**.
   - **Probe B** (provider *throws* on every call, simulating a machine that has never
     run `az login`): **both `OpenConnectionAsync()` and `OpenConnection()` succeeded**.
     Definitive — the callback is never reached.
3. End-to-end with the real application: `dotnet run` against the trust container
   applied `InitialCreate` via the startup `Migrate()` ("Database migrations applied
   successfully"), `/health` returned `{"status":"ok"}`, and `POST /api/todos` +
   `GET /api/todos` round-tripped a real row. The new startup log line appeared exactly
   as specified —
   `Postgres auth: Entra / managed identity. Host=localhost Database=tododb Username=todo`
   — with no warnings, since the local config is already clean.

Conclusion: Npgsql invokes the password provider only when the backend actually sends an
authentication request, so under `trust` the single Entra code path runs unmodified with
no token and no Azure dependency. `README.dev.md` still says "run `az login` once" (as
§14.6 requires) but now correctly frames it as needed only for developers who also reach
real Azure resources — it will not block the everyday local loop.

**Environment caveat found while doing this:** this machine already runs a *native*
Windows PostgreSQL service listening on `0.0.0.0:5432`, which shadows the container's
published port — the first probe attempt hit that server instead and failed with SASL
`28P01`. The probe was re-run with a scratchpad-only compose override publishing
`55432:5432`. The committed `docker-compose.yml` is unchanged (`5432:5432`, per §14.6).
Developers with a local Postgres install will hit the same shadowing and must stop that
service (or override the published port locally).

## Deviations from specs.md

None functional. Two shape/scope notes:

- §14.5 sketched
  `internal static string BuildEntraConnectionString(string, out NormalizationReport)`
  and explicitly left the shape to the engineer; a returned `internal sealed record
  EntraConnectionString` is used instead of an `out` parameter, which keeps the function
  pure and directly assertable. All binding rules are implemented as written.
- `TodoDbContextFactory.cs` and `ObservabilityTests.cs` are not listed in §14.4's
  deletion table but each contained a `Password=`; both were cleaned to satisfy the
  stated invariant "delete every occurrence".

## Assumptions made

1. **Loopback means exactly `localhost`, `127.0.0.1`, `::1`** (case-insensitive,
   tolerating the bracketed `[::1]` form), as §14.5 lists. Anything else — including
   other `127.x.x.x` addresses, multi-host connection strings, and unix-socket paths —
   is treated as **non-loopback** and therefore gets TLS forced. That is the fail-safe
   direction.
2. **A `Prefer` SslMode on a remote host is left alone**, per the literal rule (only
   `Disable`/`Allow` are corrected). `Prefer` is Npgsql's default, so a connection string
   that omits `Ssl Mode` entirely is also left alone. This is a deliberate reading of the
   spec and worth a reviewer glance: `Prefer` silently falls back to plaintext if the
   server refuses TLS, which would expose the token. Azure Postgres Flexible Server
   requires TLS and the checked-in Bicep sets `Ssl Mode=Require` explicitly, so this is
   not reachable in the deployed configuration.
3. **Parse failures** from `NpgsqlConnectionStringBuilder` (`ArgumentException` /
   `FormatException`) are wrapped in `InvalidOperationException` naming
   `ConnectionStrings:TodoDb`; other exception types propagate rather than being
   swallowed.
4. `ArgumentNullException.ThrowIfNull` guards were added on `AddTodoDbContext`'s
   parameters and on the injected `TokenCredential` — not required by the spec, but the
   alternative is a `NullReferenceException` at an unhelpful point.

## Known limitations / TODOs

- **Not verified end-to-end against real Entra.** Everything here was proven against a
  local `trust` Postgres with a fake or absent credential. Real token acquisition, the
  Postgres AAD handshake, and the in-DB `pgaadauth` grants are **only** provable by the
  human-run live cutover in §14.9. Until that has run and been recorded,
  `.pipeline/deployment-lessons-learned.md` §5a stays UNRESOLVED.
- **`infra/main.bicep` still sets `Postgres__UseEntraAuth`** and `infra/README.md` still
  documents it. The app now ignores that variable entirely (a test pins this), so it is
  dead config rather than a live defect — but the devops step must remove it (§14.8
  item 1) before the docs stop misleading the next reader.
- **No in-app fallback.** If the Entra path breaks in Azure, recovery is a **revision
  rollback**, not a config flag (§14.9 step 5b). This is intended; it works because the
  server keeps password auth and the previous revision plus the `todo-db-connection`
  secret stay intact until after verification.
- **Breaking change (§14.11), deliberately.** Any environment still supplying a
  `Password=`-bearing connection string now has that credential *ignored with a warning*
  and will then fail authentication unless `Username=` names a valid Entra-mapped role.
  The live `todo-api` app is in exactly that state today
  (`Username=todoadmin;Password=...`) until §14.9 step 3 is executed by a human.
- **`trust` on the local container** means anything on the developer's machine can
  connect on port 5432 with no credential. Documented in `docker-compose.yml` and
  `README.dev.md`, with "drop the `ports:` mapping" as the mitigation — never
  reintroducing a password.

## How to run it

```bash
# Local Postgres (ONCE after this change, to re-init with trust auth)
docker compose down -v && docker compose up -d

# Backend (prepend the .NET 10 SDK on this machine: C:\Users\ingda\dotnet10)
cd backend && dotnet build -c Release && dotnet test -c Release
cd src/TodoApi && dotnet run          # API on :8080, Swagger at /swagger

# Frontend
cd frontend && npm install && npm run dev     # :5173
```

Verified on this machine: `dotnet build -c Release` → **0 Warning(s), 0 Error(s)**;
`dotnet test -c Release` → **Passed! Failed: 0, Passed: 81, Skipped: 0, Total: 81**
(was 53 before this cycle). No frontend code changed.

## Security & PCI DSS scope

**PCI DSS: out of scope, and unchanged by this cycle.** This system stores to-do items —
a title, description, completed flag, and timestamps. It handles **no cardholder data**:
no PAN, no CVV/CVC, no track/magnetic-stripe data, no payment-processor integration, no
billing address. Nothing in this change moves it toward a cardholder data environment,
so tokenization / hosted-fields questions do not arise. If payments are ever added, card
entry must be delegated to a PCI-compliant processor's hosted fields (Stripe Elements or
equivalent) so that only a token ever reaches this server — that is an architect
decision, not something this change can or should pre-empt.

**OWASP-relevant posture of this change (net improvement):**

- *Identification & authentication failures / hard-coded credentials* — **strengthened.**
  The application now has **zero** database credentials in configuration, source, or
  container images. The DB password is a short-lived Entra access token acquired at
  connection-open time via managed identity and never persisted. Deleting the password
  branch removes the only code path that could consume a static credential.
- *Cryptographic failures / data in transit* — **strengthened.** TLS is now *forced* to
  `Ssl Mode=Require` for any non-loopback host configured `Disable`/`Allow`, precisely
  because the Entra token is transmitted in the cleartext-password field. See assumption
  2 for the one case a reviewer should sanity-check (`Prefer` on a remote host is left as
  configured).
- *Security logging and monitoring* — **improved, with no leakage.** One `Information`
  line records which principal the app authenticates as (host / database / username
  only), and warnings fire when a credential had to be stripped or TLS had to be forced.
  A test asserts that a `Password=` value never reaches any log entry. The token itself
  is never logged, never traced, and never returned in a response.
- *Injection* — unchanged: EF Core parameterized queries only, no raw SQL added.
- *Security misconfiguration* — the **local** `trust` Postgres is a deliberate,
  documented, local-only weakening (§14.6); it is not used in Azure and cannot be, since
  Azure Postgres Flexible Server does not offer it. The residual risk (anything on the
  dev machine can reach port 5432 without a credential) is stated in `docker-compose.yml`
  and `README.dev.md` alongside the no-password mitigation.
- **Risk I could not close from application code:** the live `todo-api` Container App
  still holds the `todo-db-connection` secret containing a real `todoadmin` password, and
  its `ConnectionStrings__TodoDb` still resolves to it. The app now *ignores* that
  password (with a warning), which is safer than before, but the secret itself is only
  retired by the human-run §14.9 steps 3 and 6. Likewise the `todoadmin` login and
  server-side `passwordAuth: Enabled` remain by explicit user requirement (§14.12) — a
  standing credential outside this change's control.
- **Dependency versions** (unchanged this cycle, listed per the engineering standard):
  `Azure.Identity` 1.21.0, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0,
  `Npgsql.OpenTelemetry` 10.0.3, `Microsoft.EntityFrameworkCore` 10.0.0,
  `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0, `Swashbuckle.AspNetCore` 10.2.3,
  `Microsoft.AspNetCore.OpenApi` 10.0.0. No package was added, removed, or upgraded here,
  and I am aware of no CVE affecting these versions.

---

# Implementation Changes — End-user authentication with Microsoft Entra External ID (2026-08-12)

Implements **specs §16** on branch `pipeline/entra-external-id-auth`. End users now
sign up / sign in through an Entra External ID (CIAM) tenant; **Entra proves who the
caller is, PostgreSQL decides what they may do**.

Substantial parts of the backend `Auth/` layer, `UsersController`, the `AppUser`/
`UserRole` models, the user DTOs and the `TodoDbContext` mapping were already present
on disk (uncommitted) when this pass started. That work was reviewed line by line,
kept where it matched the current spec, corrected where it did not, and **completed** —
it was not rewritten or duplicated.

## Summary

**Backend (`backend/src/TodoApi`)**

- Added the `Microsoft.Identity.Web` package and wired it in `Program.cs`:
  `AddTodoAuthentication()` (JWT bearer validation bound to the `AzureAd` section,
  fail-fast config validation) + `AddTodoAuthorization()` (fallback policy
  `RequireAuthenticatedUser()`, `AdminOnly` policy, scoped `IClaimsTransformation`
  and `IUserProvisioningService`).
- Middleware order is now exactly §16.7.6:
  `UseCors → UseAuthentication → UseAuthOutcome → UseAuthorization → /health
  (AllowAnonymous) → MapControllers`. `/health` carries an explicit
  `.AllowAnonymous()` because the fallback policy otherwise authenticates everything.
- **Closed the authorization hole flagged by the architect:** `TodosController` now
  carries `[Authorize]` **and**
  `[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]`, matching
  `UsersController`. Previously a token minted for this audience but **without**
  `scp = Todos.ReadWrite` would have reached `/api/todos`. Attributes only — routes,
  DTOs, payloads, status codes and ordering are untouched (§16.15).
- Fail-fast startup validation covers all **five** required keys (`Instance`,
  `TenantId`, `ClientId`, `Audience`, `Scopes`) and names every missing one, plus
  shape checks: `Instance` must be an absolute **https** URI, `TenantId` must parse as
  a GUID. `Users:PhoneClaimName` stays optional, as required.
- Generated the additive EF migration **`AddUsers`** (`users` table, unique index
  `ix_users_entra_object_id`, `ck_users_role` CHECK, role stored as text). It does not
  touch `todos`; `todos` gains no `owner_id` and no FK (§16.4.3).
- Auth-outcome mapping verified: `invalid_principal` → **401** +
  `WWW-Authenticate: Bearer`, `role_source_unavailable` → **503**, both as
  `ProblemDetails`, both skipped for `IAllowAnonymous` endpoints.

**Frontend (`frontend/`)**

- Added `@azure/msal-browser`, `@azure/msal-react` and `react-router-dom`; new
  `authConfig.ts` (PKCE public client, `knownAuthorities` for `*.ciamlogin.com`,
  `sessionStorage` cache), `main.tsx` bootstrap (`initialize()` →
  `handleRedirectPromise()` → register the API token provider → render).
- New `Root.tsx` shell: `AuthGate` (silent SSO, then `LoginScreen` with
  "Sign in / Sign up" and "Continue with Google" via `domain_hint`), `UserProvider`
  (`GET /api/users/me` once), router with `/` (todos) and `/admin`
  (`RequireAdmin` + `AdminPage`), nav with the admin link hidden for non-admins and a
  sign-out action.
- `api.ts` gained `configureApiAuth(getToken, onUnauthorized?)`, bearer-token
  attachment, the four user endpoints, and 401 handling that drops the session back to
  the login screen instead of retry-looping. With no provider configured it behaves
  exactly as before — **all 37 pre-existing frontend tests pass unchanged**.
- `frontend/Dockerfile` gained the four `VITE_ENTRA_*` / `VITE_API_SCOPE`
  `ARG`/`ENV` pairs next to `VITE_API_BASE_URL`.

## Files changed / created

**Backend — completed or corrected this pass**

| File | Purpose |
|---|---|
| `backend/src/TodoApi/Program.cs` | **CHANGED** — registers auth/authorization; §16.7.6 middleware order; `/health` explicitly anonymous |
| `backend/src/TodoApi/TodoApi.csproj` | **CHANGED** — added `Microsoft.Identity.Web` 4.14.2 |
| `backend/src/TodoApi/Controllers/TodosController.cs` | **CHANGED (attributes only)** — `[Authorize]` + `[RequiredScope(...)]`; closes the scope gap |
| `backend/src/TodoApi/appsettings.json` | **CHANGED** — documents the `AzureAd` and `Users` keys (all blank; blank ⇒ fail fast) |
| `backend/src/TodoApi/Auth/EntraAuthOptions.cs` | Binds + validates the `AzureAd` section; five-key fail fast, https/GUID shape checks |
| `backend/src/TodoApi/Auth/AuthenticationRegistration.cs` | `AddTodoAuthentication` / `AddTodoAuthorization`; hardens token validation; missing `Microsoft.AspNetCore.Authentication` using added |
| `backend/src/TodoApi/Auth/RoleClaimsTransformation.cs` | Per-request role re-derivation from Postgres, inbound-role stripping, fail-closed marking (using fixed) |
| `backend/src/TodoApi/Auth/AuthOutcomeMiddleware.cs` | Marker → 401/503 mapping; `ProblemDetails` writing fixed to use `Results.Problem(...)` |
| `backend/src/TodoApi/Auth/UserProvisioningService.cs` + `IUserProvisioningService.cs` | Lazy get-or-create of the caller's row; role always the server-side literal `User` |
| `backend/src/TodoApi/Auth/ProfileClaimsReader.cs` | `oid`/`tid`/email/name/phone extraction (no authorization influence) |
| `backend/src/TodoApi/Auth/ClaimsPrincipalExtensions.cs`, `TodoClaimTypes.cs`, `UserProfileOptions.cs` | Read helpers, `todo:`-namespaced claim constants, `Users` options |
| `backend/src/TodoApi/Controllers/UsersController.cs` | `GET/PUT /api/users/me`, `GET /api/users`, `PATCH /api/users/{id}/role` with self-demotion / last-admin guards and an audit log line |
| `backend/src/TodoApi/Models/AppUser.cs`, `Models/UserRole.cs` | `users` entity + the two roles (stored as text) |
| `backend/src/TodoApi/Dtos/UserResponse.cs`, `UpdateUserProfileRequest.cs`, `UpdateUserRoleRequest.cs` | Response shape and the two write DTOs (mass-assignment guard) |
| `backend/src/TodoApi/Data/TodoDbContext.cs` | **CHANGED** — `DbSet<AppUser>` + `users` mapping (additive) |
| `backend/src/TodoApi/Migrations/20260812224117_AddUsers*.cs`, `TodoDbContextModelSnapshot.cs` | **NEW** — additive `AddUsers` migration |

**Frontend**

| File | Purpose |
|---|---|
| `frontend/src/authConfig.ts` | **NEW** — MSAL config, login requests, `isAuthConfigured` |
| `frontend/src/main.tsx` | **CHANGED** — MSAL init/redirect handling, token-provider registration, config-error screen |
| `frontend/src/Root.tsx` | **NEW** — auth gate + router + nav shell |
| `frontend/src/auth/AuthGate.tsx` | **NEW** — silent SSO once, then the login screen |
| `frontend/src/auth/LoginScreen.tsx` | **NEW** — sign in / sign up + "Continue with Google" |
| `frontend/src/auth/tokenProvider.ts` | **NEW** — `acquireTokenSilent` with redirect fallback |
| `frontend/src/auth/authSession.ts` | **NEW** — carries "the API returned 401" from the API client into React |
| `frontend/src/auth/UserContext.tsx` | **NEW** — `GET /api/users/me` once; `{user, role, loading, error, reload}` |
| `frontend/src/auth/RequireAdmin.tsx` | **NEW** — not-authorized panel; issues no admin API call |
| `frontend/src/pages/AdminPage.tsx` | **NEW** — user list + promote/demote |
| `frontend/src/api.ts` | **CHANGED** — `configureApiAuth`, bearer attachment, 401 hook, user endpoints |
| `frontend/src/types.ts`, `src/vite-env.d.ts`, `src/index.css` | **CHANGED** — user types, new env var typings, auth/admin styles |
| `frontend/package.json`, `package-lock.json` | **CHANGED** — MSAL + router dependencies |
| `frontend/Dockerfile`, `frontend/.env.development` | **CHANGED** — four new public build args / dev variables |

**Docs**

| File | Purpose |
|---|---|
| `README.dev.md` | **CHANGED** — local auth env vars, updated API surface table, new frontend build args |
| `.env.example` | **CHANGED** — the five `AzureAd__*` (+ optional `Users__PhoneClaimName`) and four `VITE_*` identity variables, all blank |

`App.tsx`, `useTodos.ts` and the todo components are **unchanged**: `App` is still the
todo screen, which is also why every existing frontend test keeps passing.

## Verification performed locally

- `dotnet build TodoApi.sln -c Release` → **succeeded, 0 warnings, 0 errors**
  (.NET SDK 10.0.400 installed locally for this pass; the machine only had 9.x).
- `dotnet ef migrations add AddUsers` → generated; reviewed: `users` only, no change
  to `todos`.
- **Ran the API** with dummy (syntactically valid, non-existent) tenant identifiers:
  - `GET /health` → **200**;
  - `GET /health` with a garbage bearer token → **200** (the `AllowAnonymous` skip
    holds — a bad client cannot take the probe down);
  - `GET /api/todos` with no token → **401** + `WWW-Authenticate: Bearer`;
  - `GET /api/users/me` with no token → **401**;
  - CORS preflight (`OPTIONS`, `Origin: http://localhost:5173`,
    `Access-Control-Request-Headers: authorization`) → **204** with
    `Access-Control-Allow-Headers: authorization` — answered before authentication.
- **Resolved `JwtBearerOptions` inspected at runtime** (throwaway harness, deleted):
  `RequireHttpsMetadata=True`, `ValidateIssuer` / `ValidateAudience` /
  `ValidateLifetime` / `ValidateIssuerSigningKey` all `True`, `ClockSkew = 00:05:00`,
  `ValidAudiences = [api://<api-client-id>, <api-client-id>]`,
  `ValidIssuers = [https://<subdomain>.ciamlogin.com/<tid>/v2.0,
  https://<tid>.ciamlogin.com/<tid>/v2.0]`, authority built from `Instance`+`TenantId`.
- **Fail-fast guards exercised**: blank `AzureAd:Scopes` → throws naming it;
  `Instance = http://…` → throws (TLS required); non-GUID `TenantId` → throws.
- **`RoleClaimsTransformation` behaviour smoke-checked** with a stubbed provisioning
  service (throwaway harness, deleted): inbound `roles=Admin` + `ClaimTypes.Role=Admin`
  with a DB row of `User` ⇒ principal is **User** with exactly **one** role claim and
  no raw `roles` claim; DB `Admin` ⇒ `IsInRole("Admin")`; a second `TransformAsync` on
  the same principal ⇒ **one** DB lookup, one role claim; wrong `tid` ⇒
  `invalid_principal`, no role; missing `oid` ⇒ `invalid_principal`, no row created;
  provisioning throws ⇒ `role_source_unavailable`, no role claim (fail closed);
  anonymous principal returned untouched; phone claim resolved both by `_phoneNumber`
  suffix and by configured claim name.
- Frontend: `npm run build` (tsc + vite) → **succeeded**; `npm test` → **37/37 pass,
  unchanged**; `npm audit --omit=dev` → **0 vulnerabilities**.

## Deviations from specs.md (and why)

1. **`useAccessToken` hook → `createAccessTokenProvider(pca)` factory** (§16.9.3 names
   a hook). Registering the token provider from a component effect is a real bug:
   React runs **child** effects before **parent** effects, so `useTodos` and
   `GET /api/users/me` would fire their first request before the provider existed —
   every page load would start with a spurious 401 and bounce to the login screen.
   The provider is therefore built in `main.tsx` before the first render. Same MSAL
   calls (`acquireTokenSilent` → `acquireTokenRedirect` on
   `InteractionRequiredAuthError`), same public `configureApiAuth` contract.
2. **`configureApiAuth` takes an optional second argument** (`onUnauthorized`). The
   spec's signature is `configureApiAuth(getToken)`; the 401→login-screen requirement
   in the same section needs a channel out of the framework-agnostic API client. The
   second parameter is optional, so the spec's call shape still compiles.
3. **A build without the `VITE_ENTRA_*` values renders an explicit "sign-in is not
   configured" screen** instead of the app. The spec does not say what an unconfigured
   bundle should do; silently rendering an app whose every call 401s is the wrong
   failure mode. This mirrors the backend's fail-fast posture.
4. **`RequiredScopeAttribute` is imported from `Microsoft.Identity.Web.Resource`**, not
   `Microsoft.Identity.Web` — that is where the type lives in Microsoft.Identity.Web 4.x.
   Behaviour is exactly as specified (403 on insufficient scope, key bound to
   `AzureAd:Scopes`).
5. **`AuthOutcomeMiddleware` writes its ProblemDetails via `Results.Problem(...)`**
   rather than a hand-built `WriteAsJsonAsync` overload (which does not exist with that
   argument shape in .NET 10). Same RFC 7807 body and `application/problem+json`
   content type, produced by the framework's own serializer.
6. **`.github/workflows/*.yml` and `azure-pipelines.yml` were NOT edited.** §16.12
   item (4) assigns the frontend `--build-arg` plumbing to the **devops** agent, and
   the infra pass that added the Key Vault and the five `AzureAd__*` Container App env
   vars has not yet covered it. `frontend/Dockerfile` (item 3) *is* done here because
   it is frontend source. **This is a hard delivery dependency: until CD passes
   `VITE_ENTRA_TENANT_SUBDOMAIN`, `VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_SPA_CLIENT_ID`
   and `VITE_API_SCOPE` as build args, the deployed SPA will render the
   "sign-in is not configured" screen.**

## Assumptions made (spec silent or ambiguous)

1. **`Microsoft.Identity.Web` 4.14.2** — the current release; the spec names the package
   but not a version. It ships a `net10.0` target, so no downlevel shim is used.
2. **`react-router-dom` 7.18.2**, not 6.x: the 6.x line (and 7.x up to 7.17.0) carries
   two moderate advisories (open redirect via backslash in `<Link>`/`useNavigate`, and
   `deserializeErrors()` during SSR hydration). `npm audit --omit=dev` is clean on
   7.18.2, and the router API used here
   (`BrowserRouter`/`Routes`/`Route`/`Link`/`Navigate`) is identical.
3. **`@azure/msal-browser` 4.x + `@azure/msal-react` 3.x** — the versions compatible
   with the installed React 18 and with CIAM authorities.
4. **Sign-out uses `logoutRedirect()`** (a full IdP sign-out). The spec specifies the
   login UX but not sign-out; a local-only cache clear would silently re-SSO on the
   next load, which is confusing.
5. **The admin table's "Make User" button is disabled for your own row.** The server
   returns 409 anyway (§16.8); this just avoids inviting the error.
6. **`appsettings.json` lists the `AzureAd`/`Users` keys with empty values** for
   discoverability. Empty is treated as missing, so this does not weaken the fail-fast
   guard, and no real identifier is committed.
7. **`GET /api/users/me` re-reads the row** rather than reusing the row the claims
   transformation just loaded — one extra indexed single-row read on two endpoints, in
   exchange for the controller not depending on transformation internals.

## Known limitations / TODOs

- **The existing backend test suite does not pass against the new pipeline: 34 of 81
  tests fail** because `WebApplicationFactory` now boots an app that (a) requires the
  five `AzureAd:*` settings and (b) authenticates every endpoint. This is exactly the
  work spec §16.18 item 22 assigns to the **tester agent**: extend `TodoApiFactory` with
  the `AzureAd:*` in-memory configuration and a test authentication scheme plus a
  stubbed role source. Nothing in the implementation blocks it — configuration is
  injectable and every collaborator (`IUserProvisioningService`, `IClaimsTransformation`,
  `TodoDbContext`) is an interface resolved from DI. `TodoDbContextRegistrationTests`
  and `EntraTokenPasswordProviderTests` are untouched by this cycle.
- **Nothing here is verified end to end against a live tenant.** The external tenant,
  the two app registrations, the user flow, the `phoneNumber` custom attribute and the
  Google IdP are all human/portal work (§16.11, §16.13). Assumptions A2–A6 and A8
  (`oid` present and stable, phone collection on Google sign-up, `domain_hint` routing,
  the issuer/authority host mismatch, optional claims in the **access** token, `scp`
  present) remain **unverified**; each degrades safely (null profile fields, or a
  fail-closed 401/403), never into an open door.
- The **issuer validator installed by `Microsoft.Identity.Web` is an `IssuerValidator`
  delegate** (`AadIssuerValidator`), which also consults `ValidIssuers` as templates —
  the two concrete External ID issuer strings added here are belt-and-braces for the
  documented `IDX10205` gotcha. If a real token still fails, the remedy is more
  `ValidIssuers` entries; **disabling issuer validation is never acceptable.**
- **Swagger UI has no sign-in affordance**, so in Development it can only exercise
  `/health` without a manually pasted token. Out of scope this cycle.
- Role lookup is **not cached** (deliberate, §16.7.3): revocation is immediate, at the
  cost of one indexed read per authenticated request.
- The repo root still contains an untracked `dotnet-install.sh` left by an earlier
  pass; it is unrelated to this change and was not added or committed by me.

## How to run it

```bash
# 1. Postgres (local, trust auth, no password — §14.6)
docker compose up -d

# 2. Backend (from backend/src/TodoApi). The five values are PUBLIC identifiers from
#    the human-run tenant bootstrap (§16.11); the API refuses to start without them.
export AzureAd__Instance=https://<tenant-subdomain>.ciamlogin.com/
export AzureAd__TenantId=<external-tenant-guid>
export AzureAd__ClientId=<todo-api-app-client-id>
export AzureAd__Audience=api://<todo-api-app-client-id>
export AzureAd__Scopes=Todos.ReadWrite
dotnet run                      # http://localhost:8080  (/health is anonymous)

# 3. Frontend (from frontend) — fill the VITE_ENTRA_* values in .env.development first
npm install
npm run dev                     # http://localhost:5173

# Build / test
dotnet build backend/TodoApi.sln -c Release
cd frontend && npm run build && npm test
```

Container images: see `README.dev.md` — the frontend image now needs four extra
`--build-arg` values and must be rebuilt whenever any of them changes.

## Security & PCI DSS scope

**Payment / cardholder data: none. This application has no payment functionality.**
There is no checkout, order, invoice or payment endpoint; no payment-provider
integration; no PAN, CVV, track data, or any payment-related column, table or log field
anywhere in the backend, frontend, database or infrastructure. Specs §16.10 records the
same finding, verified against the full source tree. **This change therefore adds
nothing to PCI DSS scope, and no part of the system is in a cardholder data
environment.** The two roles carry no payment meaning: `Admin` grants exactly
`GET /api/users` and `PATCH /api/users/{id}/role`, nothing else.

*If payments are ever added*, specs §16.10 records binding forward constraints and this
implementation is consistent with them: card entry must be delegated to a PCI-compliant
processor's hosted fields / client-side tokenization so only a token ever reaches the
server (never storing PAN, CVV or track data, even encrypted); `Admin` must **not**
implicitly grant payment access — a payment feature adds its own policy, re-derived
server-side from Postgres exactly as `RoleClaimsTransformation` does; no payment
endpoint may be `[AllowAnonymous]` (the fallback policy already makes authentication the
default, so an omission fails closed); and a processor's API credentials — a third-party
secret that cannot use Entra auth — would follow the §13.6 Container Apps **Key Vault
reference** pattern with `Key Vault Secrets User` on the app's managed identity, *not*
the custodial-only vault introduced in §16.6.

**Secrets and service-to-service authentication**

- **Application runtime secrets: still zero.** The five `AzureAd__*` settings and the
  four `VITE_*` build args are public identifiers (URLs, GUIDs, a scope name), so they
  are plain env vars / build args — never Container Apps secrets, never Key Vault.
  Neither app registration has a client secret: the SPA is a public client using
  authorization code + **PKCE**, and the API only *validates* tokens against the
  tenant's public JWKS.
- **The new Key Vault is custodial only.** No code in this change reads from it, and no
  application identity (`todo-api`'s or `todo-web`'s) is granted any role on it. The
  Google OAuth client secret it holds is consumed by Entra External ID, not by us, and
  is written by a human whose own Entra identity authenticates to the vault — no vault
  access key, no SAS token.
- **`todo-api` → PostgreSQL is unchanged (§14):** one passwordless connection string,
  one Entra/managed-identity code path, `Ssl Mode=Require`. The `users` table is read
  and written over that same `NpgsqlDataSource`. No second connection, no second auth
  path, no password anywhere.
- No credential-shaped value appears in source, config, workflow files, the Dockerfiles,
  `.env.example`, `README.dev.md` or this document — placeholders only.

**OWASP posture of this change**

- **Broken access control — the headline fix.** The API was fully anonymous; every
  endpoint except `/health` now requires a validated token *and*
  `scp = Todos.ReadWrite`. The `[RequiredScope]` gap on `TodosController` (present in
  the code found on disk) is closed — that was a genuine hole, since a token minted for
  this audience without the delegated scope would have reached `/api/todos`. Deny by
  default is structural: the authorization **fallback policy** authenticates any
  endpoint that does not explicitly opt out. Authorization data is **never** taken from
  the client: inbound `role`/`roles`/`ClaimTypes.Role` claims are stripped before the
  single role claim is written from the database row (verified by the smoke check
  above). Admin endpoints additionally require the `AdminOnly` policy, re-derived per
  request, so a demotion takes effect on the caller's **next** request with no
  re-login. Hiding the admin nav link is UX only — stated in code comments and here.
- **Identification & authentication failures.** No password handling, hashing, reset or
  lockout logic lives in this codebase: the hosted Microsoft user flow owns all of it.
  Token validation is delegated to `Microsoft.Identity.Web`; signature, issuer,
  audience, lifetime and signing-key validation are all on, `RequireHttpsMetadata` is
  true, `ClockSkew` is the 5-minute default, and audience validation is what makes the
  SPA's **ID token** unusable against the API. The tenant (`tid`) is re-checked in the
  claims transformation as defence in depth. Identity is keyed on `oid` only — never
  `sub` (pairwise), `email` or `preferred_username` (mutable, re-assignable).
- **Cryptographic failures / data in transit.** Every hop is TLS: browser→ACA ingress,
  browser→`ciamlogin.com`, API→JWKS, API→Postgres. `Instance` is rejected at startup if
  it is not `https://`. The only non-TLS URI in the design is the loopback dev redirect
  `http://localhost:5173/`.
- **Security misconfiguration.** Missing or blank auth configuration fails startup
  loudly rather than silently serving unauthenticated traffic. CORS keeps its exact
  origin allow-list (no `AllowAnyOrigin`, no `AllowCredentials` — the token is a header,
  not a cookie). No debug endpoint, no default credential, no verbose error body:
  401/403/503 bodies are generic `ProblemDetails` that never echo a claim, a token, or
  which check failed.
- **Injection.** EF Core parameterized queries only; no raw SQL, no string-built
  predicates. The single hand-written SQL statement in the design (the one-time admin
  promotion) is human-run in psql, per spec.
- **Insecure deserialization / mass assignment.** `PUT /api/users/me` binds a dedicated
  DTO with exactly two properties (`displayName`, `phoneNumber`); `role`, `email`, `id`
  and `entraObjectId` have no bindable surface, so sending them changes nothing.
  `PATCH /api/users/{id}/role` accepts only the exact strings `User`/`Admin`
  (case-sensitive, matching the DB CHECK constraint) — anything else is a 400.
- **XSS.** All rendering is React's default text interpolation; no
  `dangerouslySetInnerHTML` anywhere. Tokens are cached in `sessionStorage` rather than
  `localStorage` to shrink the blast radius of any future XSS (they die with the tab).
- **Security logging and monitoring.** Provisioning (`oid` only), role changes (actor
  `oid`, target id, old/new role) and refused authenticated callers (method + path only)
  are logged. **No token, `Authorization` header, email, phone or display name is ever
  logged**, and the `users` PII is deliberately kept out of telemetry dimensions.
- **Availability / fail-closed.** If Postgres is unreachable the role cannot be
  determined, so the request is refused with **503** — never "assume `User`", and
  categorically never "assume `Admin`". `/health` is unaffected, so Container Apps does
  not restart-loop.
- **Risks I could not close from application code:** everything inside the external
  tenant (admin consent, the user flow, the Google IdP, optional claims) and the Key
  Vault role assignment are human/portal steps (§16.11 / §16.13); until they are done
  the app is fail-closed but non-functional. Deploy ordering is also a real, accepted
  risk: the moment the new backend is live the old frontend image gets 401 on every
  call, so backend and frontend must be rolled together with both revision names
  captured for rollback (§16.16).
- **Dependency versions added this cycle:** `Microsoft.Identity.Web` **4.14.2**
  (backend); `@azure/msal-browser` **4.30.0**, `@azure/msal-react` **3.0.29**,
  `react-router-dom` **7.18.2** (frontend). `npm audit --omit=dev` reports **0
  vulnerabilities**, and I am aware of no CVE affecting these versions. Pre-existing
  package versions are unchanged.
