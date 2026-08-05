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
