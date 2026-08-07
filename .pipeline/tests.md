# Test Report — Dev environment + dev→prod promotion (specs §15)

## Verdict: PASS

Branch: `pipeline/dev-environment`. This report supersedes all prior
`.pipeline/tests.md` cycles (overwritten each cycle, per process). Scope:
`.pipeline/specs.md` §15 as implemented in the "Implementation Changes — Dev
environment + dev→prod promotion (specs §15, 2026-08-06)" entry of
`.pipeline/changes.md`.

**Unusual cycle**: the engineer's conclusion for §15 is that **zero
application code changes are required** — §15.14 explicitly puts "any
application code change" out of scope, and §15.7/§15.8 state there is no
data-model or API-surface impact. `git status` confirms only `.pipeline/*`
docs were touched (`architecture-memory.md`, `changes.md`, `specs.md` — all
additions, no deletions). Per the assigned task, I independently verified
this conclusion rather than trusting it, then ran the full existing test
suite as a regression baseline ahead of the upcoming infra work.

---

## 1. Independent verification of "no code change needed"

I re-derived the same three checks the engineer performed, against the
actual current source (not against the engineer's narrative), plus a general
sweep for anything they might have missed.

### 1a. Hardcoded environment/resource names or single-target assumptions

- `backend/src/TodoApi/Program.cs` — confirmed `Cors:AllowedOrigins` is read
  from `IConfiguration` (`GetSection("Cors:AllowedOrigins").Get<string[]>()`,
  defaulting to `[]`), and the DbContext/telemetry registrations are called
  through `IConfiguration`-taking extension methods. No literal FQDN,
  resource name, or environment name in this file.
- `backend/src/TodoApi/Data/TodoDbContextRegistration.cs` — read in full.
  Host, database, and Postgres `Username` all come from
  `ConnectionStrings:TodoDb` exclusively; nothing is derived from a resource
  or environment name in code. This is exactly what makes the §15.3
  four-way identity invariant (Container App name == MI name == `pgaadauth`
  role == connection-string `Username=`) work per-environment with zero code
  change: dev supplies `Username=todo-api-dev`, prod supplies `Username=todo-api`,
  same binary either way.
- `backend/src/TodoApi/Observability/TelemetryRegistration.cs` — the only
  literal is the SDK-standard key `APPLICATIONINSIGHTS_CONNECTION_STRING`;
  the exporter is gated on that value being present/absent, not on any
  environment name.
- `frontend/src/api.ts` — `BASE_URL` comes from
  `import.meta.env.VITE_API_BASE_URL` only (with a safe `?? ''` fallback and
  trailing-slash trim). No hardcoded host.
- Repo-wide grep of `backend/src` and `frontend/src` for
  `todo-api|todo-web|cae-todo-demo|lemoncliff|pg-todo-demo|rg-todo-demo|
  localhost:5173|localhost:8080` turned up exactly two hits, both harmless
  and non-functional:
  - `backend/src/TodoApi/Dockerfile:2` — an illustrative comment
    (`# From repo root: docker build ... -t todo-api ./backend`), not
    executed code.
  - `backend/src/TodoApi/Data/TodoDbContextRegistration.cs` — the
    "Username is missing" exception message's prose says "in Azure, the
    `todo-api` managed identity's role" as a human-facing hint. It is static
    text thrown regardless of which environment is actually running (dev or
    prod); it does not gate any behavior on the literal string `todo-api`
    and does not affect dev's connection path. The engineer flagged this
    same item in `changes.md` and declined to touch it (editing a passing
    error-message string would itself be an unrequested code change under
    §15.14). I agree with that call.
  - Confirmed `Postgres__UseEntraAuth` / `Postgres:UseEntraAuth` does not
    exist anywhere in `backend/src` as a live config key (one doc-comment
    mention of its historical deletion in the same registration file) — so
    dev's Bicep omitting that key (§15.5) has nothing to bind to, which is
    correct and consistent.
- **Conclusion: confirmed.** No hardcoded environment/resource identifier
  gates any runtime behavior in `backend/` or `frontend/`.

### 1b. Application Insights cloud role naming — auto-differentiates per environment, no code change

- `backend/src/TodoApi/TodoApi.csproj` pins
  `Azure.Monitor.OpenTelemetry.AspNetCore` to **1.6.0** exactly as
  `changes.md` claims.
- I independently inspected the installed NuGet package assemblies (not
  just trusted the claim) — the machine already had 1.6.0 restored at
  `C:\Users\ingda\.nuget\packages\azure.monitor.opentelemetry.aspnetcore\1.6.0\lib\net10.0\Azure.Monitor.OpenTelemetry.AspNetCore.dll`
  and its dependency `Azure.Monitor.OpenTelemetry.Exporter` 1.8.3. Since
  .NET string literals are UTF-16LE in the IL string heap (a plain UTF-8
  grep finds nothing), I decoded UTF-16LE candidate strings from the raw
  assembly bytes with a small Python script and confirmed these literals
  are genuinely present in `Azure.Monitor.OpenTelemetry.AspNetCore.dll`
  1.6.0:
  `CONTAINER_APP_NAME`, `CONTAINER_APP_REPLICA_NAME`, `CONTAINER_APP_REVISION`,
  `CONTAINER_APP_JOB_NAME`, `CONTAINER_APP_JOB_EXECUTION_NAME`,
  `azure_container_apps`, alongside the OpenTelemetry resource keys
  `service.name`, `service.instance.id`, `service.version`,
  `service.namespace` in the same assembly. This corroborates that the
  distro ships an Azure Container Apps resource detector that reads the
  platform-injected `CONTAINER_APP_NAME`/`CONTAINER_APP_REVISION`/
  `CONTAINER_APP_REPLICA_NAME` env vars (which Azure Container Apps sets
  automatically per replica, and which differ by construction between
  `todo-api` and `todo-api-dev` per §15.3's naming convention) and maps them
  onto `service.name`/`service.version`/`service.instance.id` — which is
  what becomes `cloud_RoleName` in Application Insights.
- I also confirmed §15 itself does not require a cloud-role-name or
  `service.name` configuration key (grepped the full spec text — no
  `cloud role` / `RoleName` / `service.name` requirement anywhere in §15),
  and that the primary separation mechanism §15.4.3 actually specifies is a
  **separate Application Insights component** (`appi-todo-demo-dev`), which
  is a stronger and infra-level guarantee that doesn't depend on the
  resource-detector behavior at all.
- **Conclusion: confirmed.** No application code change is needed for
  telemetry separation; both the primary mechanism (separate AI component,
  infra-level) and the secondary mechanism (automatic `cloud_RoleName` via
  the already-present resource detector) require zero backend changes. The
  actual runtime value of `cloud_RoleName` inside a live dev Container App
  cannot be observed until the devops agent's Bicep is deployed — this is
  correctly flagged as a post-deploy check in `changes.md`, not something
  testable pre-deploy.

### 1c. CORS — already supports one-origin-per-environment via config

- Confirmed directly in `Program.cs`: `WithOrigins(allowedOrigins)` where
  `allowedOrigins` is bound from `Cors:AllowedOrigins` (array config
  section), which is the standard ASP.NET Core env-var array-binding path
  (`Cors__AllowedOrigins__0`, `__1`, …). A missing/empty section binds to
  `[]` — deny-by-default, never a wildcard.
- §15.8 requires each environment's backend to allow **only its own**
  frontend origin. That is exactly what setting a single
  `Cors__AllowedOrigins__0` env var per Container App (already the existing
  CD pattern for prod, reused for dev) achieves — no new code path needed,
  because the existing array-binding already supports N origins and dev
  only ever needs N=1.
- **Conclusion: confirmed.**

### 1d. General sweep beyond the three specific questions

- `git status` / `git diff --stat` show only `.pipeline/architecture-memory.md`,
  `.pipeline/changes.md`, `.pipeline/specs.md` modified (all insertions, no
  deletions) — no file under `backend/`, `frontend/`, `infra/`,
  `.github/workflows/`, or `azure-pipelines.yml` was touched this cycle, as
  claimed.
- §15.14 ("Explicitly out of scope") lists "Any application code change" —
  including the `VITE_API_BASE_URL` runtime-config refactor (Q3) and any App
  Insights sampling key — as out of scope for this cycle. The engineer
  correctly did not attempt either.
- §15.7/§15.8 state no data-model or API-surface impact — confirmed no
  `Migrations/`, `Models/`, `Dtos/`, or `Controllers/` changes in the diff
  (there is no diff at all under `backend/`).

**Overall independent verdict on the "no code change" conclusion: CORRECT.**
I found no gap between what §15 requires of the application and what the
application already does. All three specific claims were re-derived from
source/assembly inspection, not taken on the engineer's word.

---

## 2. Full existing test suite — regression baseline

Run from a clean, unmodified working tree (only `.pipeline/*` docs differ
from the last-merged state).

### Backend

```
cd backend
export PATH="/c/Users/ingda/dotnet10:$PATH"   # side-by-side .NET 10 SDK, per prior-cycle env note
dotnet --version                 → 10.0.302
dotnet build TodoApi.sln -c Release
  → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test TodoApi.sln -c Release
  → Passed! Failed: 0, Passed: 81, Skipped: 0, Total: 81
```

Matches the count `changes.md` reported for this cycle (81/81) and the prior
cycle's count — no regression, no test added or removed, consistent with
"no code change."

### Frontend

```
cd frontend
node --version   → v22.18.0
npm --version    → 10.9.3
npm ci           → added 177 packages, 0 errors (6 pre-existing dev-toolchain
                    audit advisories, unrelated to this cycle, unchanged from
                    prior cycles — not gated by `npm ci`/`npm test`)
npm test -- --run
  → Test Files  6 passed (6)
  → Tests       37 passed (37)
npm run build
  → tsc && vite build succeeded, dist/ produced (148.08 kB JS, 2.44 kB CSS)
```

Matches the count `changes.md` reported (37/37) — no regression.

**Result: full suite green, 118/118 tests passing (81 backend + 37
frontend), clean baseline confirmed ahead of the devops/infra work coming
next in this pipeline.**

---

## 3. Why no new test cases were written

There is no new application functionality to test this cycle — §15 is an
infrastructure/pipeline design extension, and its own acceptance criteria
(§15.14) explicitly exclude application code changes. Writing tests against
non-existent code would not satisfy this role's mandate ("assert real
conditions, not just it didn't throw"); the meaningful verification for this
cycle is the audit in §1 above (does the *existing* code already satisfy
§15's per-environment requirements) plus the regression run in §2 (did
nothing break). Both are documented in full above rather than invented as
synthetic unit tests.

The tests that **will** matter for §15 are the infra-level ones the spec
itself defines as the promotion gate (§15.9.3 `verify-dev` job: `/health`,
`GET /api/todos`, `POST`+`DELETE` round-trip, frontend FQDN bake-in check).
Those are CD-workflow assertions against a live dev deployment, owned by the
devops/CD stage, not unit/integration tests this role can author or run
without that infrastructure existing yet.

---

## Coverage summary (mapped to specs.md §15)

| §15 area | Covered how | Result |
|---|---|---|
| §15.7 no data-model impact | Diff inspection — no `Migrations/`/`Models/` changes | Confirmed, no impact |
| §15.8 API surface impact ("none") | Diff inspection — no `Controllers/`/`Dtos/` changes; full backend test suite still 81/81 | Confirmed, no impact |
| §15.6/§15.8 four-way identity invariant works per-environment from one binary | Source read of `TodoDbContextRegistration.cs` — `Username=` flows entirely from config | Confirmed |
| §15.4.3 / cloud role naming, no code change needed | Assembly-level inspection of the pinned `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0 package (UTF-16LE string extraction) | Confirmed — ACA resource-detector literals present |
| §15.8 CORS strictly per-environment, one origin each | Source read of `Program.cs`'s CORS binding | Confirmed — existing array-binding supports it |
| §15.14 "no application code change" | `git status`/`git diff --stat` — only `.pipeline/*` docs changed | Confirmed |
| Regression (backend) | `dotnet build` + `dotnet test` on unmodified tree | 81/81 passed, 0 warnings/errors |
| Regression (frontend) | `npm ci` + `npm test` + `npm run build` on unmodified tree | 37/37 passed, build clean |

## Gaps (not testable at this stage)

- **Live dev Container Apps Environment does not exist yet.** The
  §15.9.3 `verify-dev` smoke checks (health, list, create+delete round-trip,
  frontend FQDN bake-in) require a deployed `cae-todo-demo-dev` +
  `todo-api-dev`/`todo-web-dev` + `pg-todo-demo-dev-cus01`, none of which
  exist. These are correctly the devops/CD stage's responsibility, coming
  in a follow-up commit on this same branch (not waited for here, per
  instructions).
- **Actual runtime `cloud_RoleName` value for `todo-api-dev`** can only be
  confirmed via a Log Analytics query after a real dev deploy (the
  `AppRequests | summarize count() by AppRoleName` check `changes.md`
  already documents as a post-deploy verification step). Assembly
  inspection is the strongest evidence obtainable pre-deploy.
- **Bicep/CI-CD-YAML validity** (`az bicep build`/`lint` on
  `infra/main.dev.bicep`, the `cd.yml`/`ci.yml` job graph, the GitHub
  Environments/required-reviewers availability check) is explicitly the
  devops agent's deliverable, not yet authored on this branch, and out of
  this role's scope (application-level test verification).

---

## Commit / PR

Verdict is PASS. Per instructions, committed and pushed the `.pipeline/*`
doc updates (no app code exists to commit this cycle) and opened a PR
against `main` documenting that this PR alone carries no functional/infra
changes — architecture spec + verification record only, with the actual
`infra/`/`.github/workflows` deliverables following in a subsequent commit
on this same branch from the devops agent.
