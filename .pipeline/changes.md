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
