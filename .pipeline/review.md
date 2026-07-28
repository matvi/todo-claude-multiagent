# Review — Todo Application, Cycle 1

Reviewer: Review agent (read-only gate)
Date: 2026-07-23

## VERDICT: REJECTED — needs re-implementation

One confirmed, spec-violating defect blocks approval. It is a small, well-isolated
implementation bug in `TodosController` (route constraint), not an architectural
flaw and not a testing gap. Everything else in the delivery independently checks
out against the spec.

---

## What I verified independently (not just trusting the reports)

I read the actual source, not just `changes.md`/`tests.md`:

- **The defect is real and I reproduced the root cause by reading the code.**
  `backend/src/TodoApi/Controllers/TodosController.cs` declares the id-based
  actions with the `:guid` route constraint:
  - line 34: `[HttpGet("{id:guid}")]`
  - line 75: `[HttpPut("{id:guid}")]`
  - line 101: `[HttpDelete("{id:guid}")]`
  A URL segment that is not a valid GUID fails the `:guid` constraint, so ASP.NET
  Core routing reports "no endpoint matched" and returns a plain `404` — the
  action is never invoked, so no `400`/`ProblemDetails` is produced. This
  directly contradicts **spec §3.5**: *"`{id}` is a `Guid`; malformed ids return
  `400`."* The tester's root-cause analysis is accurate, and the three failing
  tests (`GetTodo/UpdateTodo/DeleteTodo_WithMalformedId_ShouldReturn400PerSpec`)
  correctly encode the spec requirement. The tests are not wrong — the code is.

- **Everything else matches the spec.** Cross-checked file-by-file:
  - **Data model (§2):** `Todo.cs`, `TodoDbContext.cs`, and the generated
    migration `Migrations/20260723074003_InitialCreate.cs` produce exactly the
    specified table — `uuid` PK, `varchar(200)` NOT NULL title, `varchar(2000)`
    nullable description, `boolean` default false, two `timestamptz` columns with
    `now()` defaults, and `ix_todos_created_at` DESC. Column names snake_case,
    migration named `InitialCreate`. Correct.
  - **API surface (§3.5):** all six endpoints present with the right verbs,
    status codes (200/201/204/400/404), `CreatedAtAction` Location header,
    newest-first ordering (`OrderByDescending(t => t.CreatedAt)`), and
    `[ApiController]` automatic validation. DTOs and data-annotation validation
    (`[Required]`, `[StringLength(200, MinimumLength=1)]`, `[StringLength(2000)]`)
    match §3.5. The `bool? IsCompleted` + `[Required]` decision (changes.md
    deviation #4) is correct reasoning and matches "required boolean".
  - **Config (§3.3):** connection-string key `TodoDb`, CORS policy `frontend`
    from `Cors:AllowedOrigins`, `/health` returning `{status:"ok"}` un-gated by
    CORS. `Program.cs` guards the missing-connection-string case and wraps
    `Database.Migrate()` in try/log (§3.4). Correct.
  - **Frontend (§4):** `api.ts` (fetch wrapper, throws `ApiError` on non-2xx,
    parses `ValidationProblemDetails`), `useTodos.ts` (update-on-success, newest-
    first prepend, toggleComplete issues PUT with flipped flag), and the three
    components all match §4.3. Dockerfile + `nginx.conf` (listen 8080, SPA
    fallback) match §4.4.
  - **Backend Dockerfile (§3.6):** multi-stage sdk→aspnet, port 8080, backend
    build context — matches the spec verbatim.
  - **Azure runbook (§5):** `scripts/deploy-azure.sh` follows §5.7 ordering
    (backend first, capture FQDN, build frontend with baked URL, then open CORS),
    uses the §5.6 scaling/sizing values (backend min 0/max 3, frontend min 1/
    max 2, 0.25 vCPU/0.5 GiB), `Ssl Mode=Require` (§5.4), and Container Apps
    secret for the connection string (§5.5). Not executed — correct per spec.

- **Security review: clean.** No hardcoded production secrets. The only committed
  credential is the throwaway local Postgres password (`todo`) in
  `appsettings.Development.json`, which is exactly the documented local-dev value
  in spec §6.3. The Azure runbook takes `PG_ADMIN_PASSWORD` from the environment
  and refuses to run if unset. CORS is origin-restricted, TLS to Postgres is
  required, and all DB access is via EF Core (parameterized, no raw SQL). No
  injection, auth-bypass, or secret-leak issues (auth is explicitly out of scope,
  §7).

- **changes.md claims match the diff.** The deviations it lists (Swashbuckle
  10.2.3, classic `.sln`, `bool?` IsCompleted, design-time factory, extra
  `launchSettings.json`/`vite-env.d.ts`) are all present in the actual code and
  are reasonable. No overstated claims found.

- **Tests genuinely cover the spec.** The 27 backend tests exercise each endpoint,
  validation boundaries (title 1/200/201 chars, description 2000/2001, whitespace-
  only title, missing isCompleted), 404 paths, ordering, CORS preflight, and
  health — real integration tests via `WebApplicationFactory<Program>`, not
  direct method calls. 37 frontend tests cover the api client, `useTodos`, and
  all components. Coverage is appropriate; the failure is a code bug the tests
  correctly caught, not a missing test.

---

## Blocking issues

1. **Malformed GUID returns 404 instead of 400 (spec §3.5).**
   `backend/src/TodoApi/Controllers/TodosController.cs` lines 34, 75, 101 use the
   `{id:guid}` route constraint on GET/PUT/DELETE by id. A non-GUID segment fails
   the constraint and falls through to a bare `404`, violating §3.5's "malformed
   ids return `400`". Reproduced by three failing tests. This is the sole reason
   for rejection.
   Suggested fix (engineer's call): drop the `:guid` constraint, bind `id` as
   `string`, `Guid.TryParse` it, and return `BadRequest(...)` (ideally a
   `ProblemDetails`) on parse failure; or install a routing/exception translation
   that converts the constraint miss into a 400. Keep the existing 404-when-not-
   found behavior for well-formed-but-unknown GUIDs (that path already passes).

Because this is a localized controller change — not a data-model, API-contract,
or infrastructure change — the correct stage to redo is **implementation**
(engineer), followed by a re-run of the existing test suite. No re-architecture
and no additional tests are required; the tests that must go green already exist.

---

## Non-blocking suggestions (do not block approval)

1. **No real Postgres / Docker E2E in this environment.** Tests substituted EF
   Core InMemory (backend) and a mocked API (frontend) because Docker Desktop's
   engine was not running. This is an environmental limitation, honestly
   disclosed by both engineer and tester, and consistent with the spec's testing
   guidance. It does mean the `InitialCreate` migration, `timestamptz`/`now()`
   defaults, `varchar` length enforcement, and `Ssl Mode=Require` behavior were
   verified by code inspection only, not exercised against a live server. Not a
   blocker for this cycle, but a human should run `docker compose up -d` +
   `dotnet run` once to confirm the schema and CRUD against real Postgres before
   any production use.
2. When fixing issue #1, consider returning a `ValidationProblemDetails`-shaped
   400 for the malformed id so the frontend's existing `extractErrorMessage`
   (which reads `.errors` / `.title`) surfaces a useful message consistently.
3. Frontend `npm audit` reports 2 dev-server-only advisories (esbuild/vite).
   Correctly scoped out of the nginx-served production bundle; fine to defer.

---

# Review — Todo Application, Cycle 2

Reviewer: Review agent (read-only gate)
Date: 2026-07-23

## VERDICT: APPROVED

The single blocking defect from cycle 1 (malformed GUID returning 404 instead of
the spec §3.5-mandated 400) is fixed correctly, completely, and without
regressing anything approved in cycle 1. Verified by reading the actual source,
not by trusting changes.md/tests.md.

---

## What I verified independently

- **The fix is in the code and is correct.**
  `backend/src/TodoApi/Controllers/TodosController.cs`:
  - `GetTodo` (line 34), `UpdateTodo` (line 81), `DeleteTodo` (line 112) now
    declare `[Http*("{id}")]` — the `:guid` route constraint is gone from all
    three. The parameter is `string id`. Each action's first statement is
    `if (!Guid.TryParse(id, out var todoId)) return MalformedId(id);` (lines
    40-43, 90-93, 118-121), so a non-GUID segment now reaches the action and
    produces a 400 instead of falling through to a routing 404.
  - The `MalformedId` helper (lines 143-147) returns
    `BadRequest(new ValidationProblemDetails(ModelState))` with an `errors`
    entry keyed by `"id"`. `ValidationProblemDetails` derives from
    `ProblemDetails`, so this satisfies spec §3.5's "Errors use `ProblemDetails`"
    requirement, and the `errors`/`title` shape is exactly what the frontend's
    existing `extractErrorMessage` reads (this also adopts cycle 1 non-blocking
    suggestion #2).
  - The well-formed-but-unknown-GUID path (`FindAsync` -> `NotFound()`) is
    unchanged, so 404 for a valid-shape-but-nonexistent id still works
    (lines 45-49, 95-99, 123-127). No regression to the 404 behavior cycle 1
    approved.
  - `CreateTodo`'s `CreatedAtAction(nameof(GetTodo), new { id = todo.Id }, ...)`
    (line 77) is untouched and still resolves against the now-unconstrained
    `{id}` template.

- **The fix is completely scoped.** I grepped `backend/src` for `:guid`: the
  only remaining occurrence is inside an explanatory code comment
  (`TodosController.cs:138`), not a route. No other id-based route was missed.
  The only other route is `/health`, which has no id segment.

- **Tests genuinely cover the requirement, not just exist.** In
  `backend/tests/TodoApi.Tests/TodosControllerTests.cs`, the three
  `*_WithMalformedId_ShouldReturn400PerSpec` tests (GET/PUT/DELETE, lines 239,
  277, 288) assert `HttpStatusCode.BadRequest`, and the two new cycle-2
  hardening tests — `GetTodo_WithWhitespaceOnlyId_Returns400` (line 253,
  `%20%20%20`) and `GetTodo_WithExtremelyLongGarbageId_Returns400` (line 266,
  2000-char garbage) — also assert `BadRequest`. These are real assertions on
  the exact status code the spec requires.

- **changes.md and tests.md claims match reality.** changes.md's Cycle 2 section
  accurately describes the route/param/`Guid.TryParse`/`MalformedId` changes and
  the before/after behavior table. tests.md's Cycle 2 section's claims (29/29
  backend, 37/37 frontend, new tests, no other `:guid` routes) are consistent
  with the source I read. No overstated claims found.

- **No new security issues.** The fix adds no secrets, no raw SQL, no new input
  sinks; it makes input validation stricter (malformed ids are now explicitly
  rejected). Interpolating the offending id into the ProblemDetails message is
  safe — it is JSON-encoded in the response body, not reflected as HTML/executed.

## Blocking issues

None.

## Non-blocking suggestions (carried forward, do not block approval)

1. No real Postgres / Docker E2E was possible in this environment (Docker Desktop
   engine not running); the `InitialCreate` migration, `timestamptz`/`now()`
   defaults, `varchar` length enforcement, and `Ssl Mode=Require` remain verified
   by code inspection only. A human should run `docker compose up -d` + `dotnet
   run` once against real Postgres before production use. Unchanged from cycle 1.
2. Frontend `npm audit` reports 2 dev-server-only advisories (esbuild/vite),
   correctly scoped out of the nginx-served production bundle. Fine to defer.

---

# CI/CD cycle review — CI/CD + Infrastructure-as-Code extension (specs §11)

Reviewer: Review agent (read-only gate)
Date: 2026-07-24
Branch: `pipeline/todo-app-azure-cicd` (off `main`). Review cycle 1 of up to 3
for the extended CI/CD + IaC scope.

## VERDICT: APPROVED

The delivery layer (Bicep IaC + GitHub Actions CI/CD + Azure Pipelines equivalent)
is complete, correct, and independently verified against spec §11 and the four
human-approved decisions. I read every Bicep module, both workflows, and
`azure-pipelines.yml` in full rather than trusting `infra.md`/`infra-proposal.md`;
the self-reports match the authored artifacts. No secrets are committed, no
provisioning was performed by any agent, and exactly one CI/CD system
auto-triggers. The known environment limitation (no git remote / no `gh` CLI, so
no PR exists yet) is an environment constraint, not a deliverable defect, and per
the review brief is noted rather than penalized.

---

## Compliance with the four human-approved decisions (all followed)

1. **Postgres tier `Standard_B1ms` (not B2s).** `infra/modules/postgres.bicep`
   line 29 defaults `skuName = 'Standard_B1ms'` with `tier: 'Burstable'`
   (line 41); `main.parameters.json` does not override it. No `B2s`/`D`-series
   anywhere (grep clean). Correct.
2. **ACA Consumption, ACR Basic, frontend min 1, backend min 0/max 3.**
   - ACA: `acaEnvironment.bicep` has no `workloadProfiles` block → default
     Consumption (comment lines 30-31); no Dedicated. Correct.
   - ACR: `registry.bicep` defaults `sku = 'Basic'`, `main.bicep` passes
     `sku: 'Basic'`. Correct.
   - Replicas: `main.parameters.json` sets `webMinReplicas=1/webMaxReplicas=2`
     and `apiMinReplicas=0/apiMaxReplicas=3`, matching §5.6 / the approved table.
     Correct.
   - Sizing: `containerApp.bicep` invoked with `cpu:'0.25'`, `memory:'0.5Gi'`
     for both apps. Correct.
3. **Bicep is resource-group-scoped; human pre-creates the RG.**
   `main.bicep` line 23 `targetScope = 'resourceGroup'`; no
   `Microsoft.Resources/resourceGroups` resource exists. `infra/README.md`
   Phase 1 step 1 documents `az group create` as a manual human action. Correct.
4. **`AcrPull` role assignments are a manual bootstrap step, NOT in the CD Bicep.**
   Grepping the entire `infra/*.bicep` tree for `roleAssignment` /
   `Microsoft.Authorization/roleAssignments` returns only explanatory comments —
   there is no role-assignment resource. `containerApp.bicep` enables the
   system-assigned identity and wires ACR pull via `registries[].identity:
   'system'` (lines 79-84) but does not create the grant. `infra/README.md`
   Phase 3 places the `AcrPull` grants (and the CI SP's `AcrPush`) after the
   first infra deploy, run by a human with User Access Administrator/Owner.
   Correct — and this exactly resolves the `Contributor`-cannot-write-role-
   assignments friction the PROPOSE-mode Open Q2 raised.

## Secrets: none committed (verified)

- `main.bicep` takes `postgresAdminPassword` as an `@secure()` param (lines
  58-60); it is threaded into `postgres.bicep` (also `@secure()`) and into the
  assembled connection string (line 118), which is surfaced only as the backend
  Container Apps secret `todo-db-connection` → env `ConnectionStrings__TodoDb`.
  It is never emitted as an output.
- `main.parameters.json` contains only non-secret values and an explicit comment
  that the password is supplied at deploy time; the password is absent.
- `cd.yml` sources the password from `secrets.PGADMIN_PASSWORD`, scoped as a
  step-level `env:` only on the Bicep-deploy step (lines 82-83). `azure-pipelines.yml`
  references it as `$(PGADMIN_PASSWORD)` (documented as a secret pipeline
  variable). OIDC client/tenant/subscription IDs are GitHub **variables**, not
  secrets (no credential material) — matches §11.6.
- A repo-wide grep for `Password=`/secret-like literals found only: the local
  throwaway dev credential `todo` (in `.env.example`,
  `appsettings.Development.json`, the design-time and test DbContext factories —
  all documented local-dev values per §6.3), the spec's `<PWD>` placeholder, and
  variable references (`$PGPWD`, `$PGADMIN_PASSWORD`). No production secret,
  connection string, or key is committed. `git ls-files` shows no `.env`,
  `.tfstate`, `.pem`/`.pfx`, or key material tracked.

## No agent provisioning / credential creation (verified as far as possible)

`infra.md` and `infra-proposal.md` both state that only read-only validation
(`az bicep build`/`lint`, YAML parse) was run and nothing was provisioned. This
is consistent with: no Azure credentials in the environment, no deployment/state
artifacts on disk, no role-assignment resources in the Bicep, and the identity/
RBAC/ACR-grant/GitHub-secret steps all living as human-run scripts in
`infra/README.md`. PROPOSE→APPLY mode boundaries (§11.8) were respected — the
agent authored files and documented commands but created no app registration,
federated credential, role assignment, or GitHub secret/variable.

## CD backend-then-frontend ordering (correctly encoded, not just claimed)

`.github/workflows/cd.yml` sequences: (2) idempotent Bicep infra deploy → (3)
`az acr build` backend `todo-api:<sha>` → (4) `az containerapp update` backend +
read back its FQDN into `steps.api.outputs.fqdn` → (5) `az acr build` frontend
with `--build-arg VITE_API_BASE_URL="https://${{ steps.api.outputs.fqdn }}"`
(lines 109-116) → (6) update frontend → (7) reconcile backend CORS to the
frontend FQDN. The backend FQDN is genuinely produced before and consumed by the
frontend build — the §4.4/§5.7 build-time-bake constraint is honored, not merely
asserted. `azure-pipelines.yml` encodes the identical order (lines 144-163).
Within Bicep, `todo-web` is declared before `todo-api` so the backend's
`Cors__AllowedOrigins__0` can reference `todoWeb.outputs.fqdn` (main.bicep lines
120-172), resolving the CORS chicken-and-egg inside a single deploy.

## CI is credential-free on PRs (verified)

`.github/workflows/ci.yml` triggers on `pull_request` → `main` +
`workflow_dispatch`, declares `permissions: contents: read` only, and contains
**no** `azure/login`, OIDC token request, or secret reference. The `iac` job runs
`az bicep install` + `az bicep build`/`lint`, none of which require Azure auth.
Jobs run the real build/test commands (`dotnet restore/build/test` on
`TodoApi.sln`; `npm ci` + `npm run build` + `npm test`; `docker build` of both
images with a dummy `--build-arg`). Safe on fork PRs, per §11.4.

## Exactly one system auto-triggers (verified)

GitHub Actions is the auto-triggering system: `ci.yml` on PR, `cd.yml` on push to
`main`. `azure-pipelines.yml` is `trigger: none` / `pr: none` (manual-only) with
an inline comment explaining it is kept behavior-equivalent to avoid double-builds
and racing two deploys onto the same Container App revision. So exactly one of
{GitHub Actions, Azure Pipelines} fires automatically — neither both nor neither.

## Flagged deviations from the §11.6 draft — all reasonable, none need re-architecture

`infra/README.md` lists four deliberate differences from the specs §11.6 draft; I
assessed each:
1. **Adds `az group create rg-todo-demo`** — required because the Bicep is
   RG-scoped (approved Open Q1). Consistent with the human decision.
2. **Adds a federated credential for the `environment:production` subject** (and
   optionally `pull_request`), not just `main` — this is technically *necessary*,
   not merely reasonable: `cd.yml` runs its deploy job under
   `environment: production`, so the GitHub OIDC token's `sub` claim becomes
   `repo:<org>/<repo>:environment:production`. Without that federated-credential
   subject, `azure/login` would fail. Good catch by devops. The `pull_request`
   subject is correctly marked optional (only if what-if on PRs is later enabled).
3. **Moves the ACR role grants to after the first infra deploy** — necessary
   because ACR and the app managed identities do not exist until the template is
   deployed once; the draft's ordering (granting `AcrPush` before ACR existed)
   was not runnable. Correct.
4. **Adds `AcrPull` grants for each app's managed identity as a manual step** —
   the approved Open Q2 resolution; excluded from the repeatable CD Bicep because
   the CD SP holds only `Contributor`.

None of these contradict anything the human approved; two directly implement the
approved Open Q1/Q2 resolutions, and the other two are technical necessities.
They do not need to go back to the architect.

## Other cross-checks

- **Bicep validity:** `infra.md` reports `az bicep build` + `az bicep lint` clean
  (Bicep 0.45.15). The templates read as valid — module wiring, `items()`-based
  secret array, `existing` LA reference with inline `listKeys()` (so the shared
  key is never a param/output), `@secure()` on the secrets object, and all
  outputs (`acrLoginServer`, `todoApiFqdn`, `todoWebFqdn`, principalIds, etc.)
  are consistent with what `cd.yml` consumes (`.acrName.value`,
  `.acrLoginServer.value`). I could not re-run `az bicep build` here (no Azure
  CLI/Bicep in this review environment), so I relied on source inspection plus
  the reported validation; nothing in the source contradicts a clean build.
- **Resource inventory** matches §5.3/§11.9: ACR Basic (admin user disabled),
  Log Analytics PerGB2018 30-day, ACA Consumption env, Postgres B1ms/PG16/32 GiB/
  no HA/LRS backup/public access + `0.0.0.0` firewall + required TLS, `tododb`,
  and the two Container Apps with system-assigned identities and external ingress
  on 8080. No Key Vault, VNet, private endpoint, NAT, or Dedicated profile.
- **Security posture:** connection string uses `SslMode=Require;Trust Server
  Certificate=true` — Npgsql accepts `SslMode` as an alias for `SSL Mode`, and
  this matches the spec §5.4/§5.5 example verbatim (TLS required; cert trust is
  the documented demo simplification). No injection/auth-bypass surface (auth
  out of scope). ACR admin user is disabled in favor of managed-identity pull.
- **PCI DSS:** not in scope — this is a no-auth Todo demo that processes no
  payment or cardholder data. No PAN/CVV/track data is stored, logged, or handled
  anywhere. No payment flow exists. Nothing to flag.

## Blocking issues

None.

## Non-blocking suggestions (do not block approval)

1. **Production Environment approval gate is not yet enforced.** `cd.yml` and
   `azure-pipelines.yml` both target a `production` environment specifically so a
   required-reviewer rule can be attached, but that rule is a GitHub/ADO
   Environment setting a human must configure — currently the only pre-deploy
   gate is branch protection + PR review (also not yet enforced; see #2). This
   matches spec §11.7 (single-environment demo; approval gate called out as a
   future enhancement), so it is not blocking, but a human should add required
   reviewers on the `production` environment before this deploys anything real.
2. **Branch protection on `main` is documented but not enforced** (no remote/`gh`
   in this environment; `infra.md` and `infra/README.md` both disclose this). The
   "human reviews the PR" gate is currently only a convention. A human must apply
   the documented branch-protection rules (require PR + CI status check + ≥1
   approval, no force-push/delete) once a GitHub remote exists. Environment
   limitation, not a deliverable defect — noted, not penalized.
3. **The new artifacts are authored on disk but not yet committed** (`git status`
   shows `infra/`, `.github/`, `azure-pipelines.yml`, and the two new
   `.pipeline/*.md` as untracked). They must be committed and pushed for CI/CD to
   run — expected to happen when a remote/`gh` is available. Not a defect.
4. **`Trust Server Certificate=true`** encrypts but skips server-cert
   verification (MITM-susceptible). It matches the approved spec for the demo;
   production should move to `SslMode=VerifyFull` with the Azure Postgres CA, as
   the spec already notes under production hardening. Defer.

## Which stage (if any) needs to redo work

None. The CI/CD + IaC deliverable satisfies every §11 acceptance criterion
(§11.11) and all four human-approved decisions. This cycle is APPROVED. The
outstanding items (branch protection, production approval reviewers, committing/
pushing the artifacts, first human bootstrap of the OIDC identity + ACR grants)
are human one-time actions explicitly assigned to humans by spec §11.6/§11.7 and
gated by the no-remote/no-`gh` environment limitation — not agent rework.

---

# Observability + Identity Policy cycle review — App Insights tracing (§12) + managed-identity-first policy (§13)

Reviewer: Review agent (read-only gate)
Date: 2026-07-28
Branch: `pipeline/appinsights-tracing` (off `main`, the live already-deployed app).
Review cycle 1 of up to 3 for the combined tracing + identity/secrets scope.

## VERDICT: APPROVED

Both features are implemented correctly and independently verified against specs
§12 and §13, the architecture-memory 2026-07-27 entries, and all five
human-approved decisions. I read the actual backend code, every changed Bicep
module, `main.bicep`, `main.parameters.json`, the README cutover section, and the
three new test files in full — not just the self-reports. The self-reports
(`changes.md`, `tests.md`, `infra-proposal.md`, `infra.md`) match what is actually
on disk. This cycle **removes** stored secrets (net security improvement), commits
none, and no agent provisioned anything. The one durable-decision area worth a
careful human eye — cutover sequencing on a live app — is documented loudly and
correctly; my only reservations are non-blocking.

---

## The five human-approved decisions — each verified in the actual files

1. **Postgres Entra administrator = the human's own Entra user.** VERIFIED.
   `infra/main.parameters.json` sets `postgresEntraAdminObjectId`
   = `ee31140f-3164-439c-8fa9-9f7e5dbd1b2c` and `postgresEntraAdminTenantId`
   = `6ce2ff72-209a-447d-bf53-9579c52c03f5` (both exact matches to the brief),
   `principalType: User`, and the UPN
   `ing.david.mata.az_outlook.com#EXT#@ingdavidmataazoutlook.onmicrosoft.com`.
   `infra/modules/postgres.bicep` consumes these: the `flexibleServers/
   administrators` child resource (lines 96-104) uses the objectId as its resource
   name (required by ARM) and passes `principalType`/`principalName`/`tenantId`.
   `main.bicep` threads all four params through (lines 60-75, 157-160). Correct.

2. **100% sampling as a backend-code default, no ARM property.** VERIFIED and the
   devops claim is credible/well-reasoned, not blind trust.
   - The reasoning holds: `Microsoft.Insights/components` does expose
     `SamplingPercentage`, but it drives the classic/legacy ingestion-sampling
     path, not the `Azure.Monitor.OpenTelemetry.AspNetCore` exporter. The distro's
     sampling is controlled in code via `AzureMonitorOptions.SamplingRatio`, whose
     default is `1.0` (= 100%). Setting the ARM property would misleadingly imply
     it governs the OTel path.
   - Cross-checked against the engineer's code:
     `backend/src/TodoApi/Observability/TelemetryRegistration.cs` calls
     `otel.UseAzureMonitor(options => { options.Credential = ...; })` and
     **deliberately does not set `SamplingRatio`**, so it inherits the `1.0`
     default = 100%. So 100% sampling is genuinely honored, in the right place
     (code, not ARM). `appinsights.bicep` (lines 13-23) documents exactly this and
     leaves `SamplingPercentage` unset. Correct and internally consistent.

3. **Manual `Monitoring Metrics Publisher` grant, not Bicep (AcrPull convention).**
   VERIFIED. `infra/modules/appinsights.bicep` creates only the component — there
   is no `Microsoft.Authorization/roleAssignments` resource anywhere in
   `infra/` (the only occurrences of the role name are comments). `infra/README.md`
   Phase 4 step C documents the `az role assignment create --role "Monitoring
   Metrics Publisher" --assignee <todoApiPrincipalId> --scope <appInsightsId>` as a
   human-run step, and `main.bicep` emits `appInsightsId`/`todoApiPrincipalId`
   outputs to feed it. This matches the established manual `AcrPull` Phase-3
   convention exactly. Correct.

4. **Dual-auth Postgres (both enabled, password NOT disabled).** VERIFIED.
   `infra/modules/postgres.bicep` lines 73-77: `authConfig` sets
   `activeDirectoryAuth: 'Enabled'` AND `passwordAuth: 'Enabled'` with the
   `tenantId`. `administratorLoginPassword` is still set from the `@secure()`
   param (line 69). The break-glass password path is retained, not disabled.
   Correct.

5. **1 GB/day ingestion cap — the human's override of the devops "no cap"
   recommendation was actually applied.** VERIFIED, and specifically not silently
   reverted. `infra/modules/loganalytics.bicep` declares
   `param dailyQuotaGb int = 1` and applies it via
   `workspaceCapping: { dailyQuotaGb: dailyQuotaGb }` (lines 16, 28-30).
   `main.bicep`'s `logAnalytics` module call (lines 116-123) passes only `name`/
   `location`/`retentionInDays` and does **not** override `dailyQuotaGb`, so the
   effective value is the module default `1` — i.e. the cap is on at 1 GB/day. The
   proposal §3e had recommended no cap; `infra.md` explicitly records this as a
   user override, and the code reflects the override, not the recommendation.
   Correct. (The documented tradeoff — telemetry silently drops for the rest of a
   UTC day once 1 GB is hit — is accurately disclosed in the module and README.)

## Backend implementation vs. specs §12 / §13 (read the code, not the changelog)

- **Tracing wiring is real (§12.2/§12.3/§12.5).**
  `TelemetryRegistration.AddTodoTelemetry` registers OpenTelemetry
  unconditionally; when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present it calls
  `UseAzureMonitor(...)` (which brings ASP.NET Core + HttpClient instrumentation +
  the exporter), otherwise it adds `AddAspNetCoreInstrumentation()` +
  `AddHttpClientInstrumentation()` so in-process tracing still flows. `AddNpgsql()`
  is added once in both branches (line 76) so DB calls become child spans under
  one trace. The deviation from the literal §12.3 snippet (conditional
  `UseAzureMonitor`) is documented in `changes.md` and is spec-*compatible* — §12.5
  requires a clean no-op when the connection string is unset, and §13.5 requires
  the managed-identity credential never to be constructed locally. `Program.cs`
  wires this via `AddTodoTelemetry` / `AddTodoDbContext` and leaves controllers,
  Swagger, CORS, `/health`, and the guarded startup `Migrate()` untouched.
- **Log↔trace correlation is automatic, no enrichment (§12.4).** No Serilog, no
  custom enricher; correlation rides `Activity.Current` → `ILogger`. Proven by the
  new `Request_ProducesValidTraceId_AndCorrelatesLogRecordToSameTrace` test, not
  merely asserted.
- **Postgres managed-identity path is correct (§13.4).**
  `TodoDbContextRegistration.AddTodoDbContext` branches on `Postgres:UseEntraAuth`.
  The Entra branch builds an `NpgsqlDataSource` via
  `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` (periodic refresh, 50 min /
  5 s retry — not a one-shot fetch), acquiring a token via `DefaultAzureCredential`
  for the exact scope `https://ossrdbms-aad.database.windows.net/.default`, and
  registers the data source as a **singleton** (shared pool/token cache, DI
  disposes it). `DefaultAzureCredential` (permitted by §13.5 as an alternative to
  `ManagedIdentityCredential`) is only constructed inside the active branch, so it
  is never invoked locally.
- **`Postgres__UseEntraAuth` genuinely defaults off (confirmed by reading code,
  not the changelog).** `appsettings.json` sets `"Postgres": { "UseEntraAuth":
  false }` explicitly, and `AddTodoDbContext` reads it via
  `configuration.GetValue<bool>(...)`, which returns `false` when the key is
  absent. So local docker-compose dev and CI take the unchanged
  `UseNpgsql(connectionString)` password path. `appsettings.Development.json`
  carries only the throwaway local `todo/todo` credential (spec §6.3) and no Entra
  flag. Local dev is unaffected.
- **App Insights connection string is non-secret config (§13.5).** Read only from
  the flat env var `APPLICATIONINSIGHTS_CONNECTION_STRING` (no `__` section
  syntax, per §12.5); it is absent from `appsettings*.json`. `csproj` pins
  `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0, `Npgsql.OpenTelemetry` 10.0.3,
  `Azure.Identity` 1.21.0 at `net10.0`.

## IaC vs. specs / infra.md (read the Bicep, not the self-report)

- **`todo-db-connection` secret is genuinely GONE, not renamed.** In `main.bicep`
  the `todoApi` module (lines 213-246) passes **no** `secrets` and **no**
  `secretEnvVars`; all four values (`Cors__AllowedOrigins__0`,
  `ConnectionStrings__TodoDb`, `Postgres__UseEntraAuth=true`,
  `APPLICATIONINSIGHTS_CONNECTION_STRING`) are plain `envVars`. The connection
  string (line 173) is passwordless — `Host=...;Username=todo-api;Ssl
  Mode=Require;...` with no `Password=`. A grep of `infra/` for `todo-db-connection`
  finds only comments documenting the removal, and for `secretref`/`Password=`
  finds only doc/`@secure()`-param references — no committed secret value. The
  `containerApp.bicep` module still *supports* secrets generically, but `todo-api`
  now supplies none: zero Container Apps secrets on the backend. Matches §13.3's
  "zero application runtime secrets" outcome.
- **App Insights module is workspace-based, reuses `log-todo-demo`, local auth
  off.** `appinsights.bicep`: `kind: web`, `Application_Type: web`,
  `WorkspaceResourceId` = `logAnalytics.outputs.id` (no second workspace),
  `IngestionMode: LogAnalytics`, `DisableLocalAuth: true`. `connectionString` is a
  module output consumed as the plain env var. Matches §12.6 / §13.5.
- **Username/role single source of truth.** `main.bicep` sets `var todoApiName =
  'todo-api'` and reuses it as the Container App name, the connection-string
  `Username=`, and (per README Phase 4 step B) the `pgaadauth_create_principal`
  argument — one consistent value, removing the mismatch-fails-auth risk the
  proposal flagged.

## Cutover sequencing on the live app (the genuine outage risk)

`infra/README.md` Phase 4 carries a prominent "⚠️ CUTOVER SEQUENCING — READ BEFORE
APPLYING" callout with the correct strict order: (1) deploy the Postgres
Entra-auth server change (idempotent, does not disturb the running password path)
→ (2) `pgaadauth_create_principal('todo-api', ...)` + schema/DDL grants in-DB → (3)
grant `Monitoring Metrics Publisher` → (4) only then flip `todo-api`'s env
(`UseEntraAuth=true`, passwordless connection string, AI env var, drop the secret).
It explicitly names the concrete outage scenario ("Doing step 4 before step 2")
and correctly warns that a single full-template apply performs steps 1 AND 4
together, so steps 2 and 3 must precede the first full apply. The startup-
`Migrate()`-on-cold-replica failure mode is called out. This is clear enough that
a careful human will not blindly flip the env first. See non-blocking suggestion
#1 for the one rough edge (the template has no toggle to defer step 4, so the
"split the deploy" instruction lacks an exact command).

## Tests (44/44) — real coverage, not just green

Read `ObservabilityTests.cs`, `TelemetryRegistrationTests.cs`, and
`TodoDbContextRegistrationTests.cs` in full. They exercise the app's own wiring,
not tautologies: the Activity-capturing processor is hooked into the app's *own*
`TracerProviderBuilder` via `ConfigureOpenTelemetryTracerProvider`, and a marker
`ILogger` line is injected via `IStartupFilter` without touching `Program.cs`, so
the correlation assertion (same 32-hex non-zero `TraceId` on the log record and on
a Server-kind exported Activity) genuinely tests §12.8's core requirement. The
env-var-unset and env-var-set branches are both covered (§12.5). §13.4 branching
is proven both ways — the false/absent path asserts **no** `NpgsqlDataSource`
singleton and that the resolved context's connection string is exactly the
password-based one (real regression proof), and the true path constructs the
data source/token provider without network I/O. The guard-throws cases survived
the refactor. The flakiness root cause the tester found and fixed is legitimate
and correctly handled: OpenTelemetry `ActivitySource` listeners are process-wide
statics, so cross-test contamination under xUnit parallelism was real; the fix
(`DisableTestParallelization` + asserting the specific known `TraceId` rather than
"first Server activity") is defense-in-depth, not a band-aid over a product bug.
The end-to-end "real token against a real Entra-enabled Postgres" gap is correctly
scoped out (no live Azure) and matches §12.8's "no live-Azure integration test
required."

## Security / PCI DSS

- No payment or cardholder data anywhere (unchanged from prior cycles); no
  PAN/CVV/track data stored, logged, or handled. PCI DSS not in scope. No SAD in
  logs — tracing correlates by `traceId`/`spanId`; the Entra token is passed as
  the Npgsql password and is not emitted to spans or logs.
- **Removing the Postgres password in favor of Entra/managed-identity is a net
  security improvement** — it eliminates a stored, long-lived credential and the
  `todo-db-connection` Container Apps secret; the token is short-lived and
  auto-refreshed. Disabling AI local auth demotes the instrumentation key to a
  non-credential resource identifier. This strengthens the posture.
- **Dual-auth is a partial, justified concession, not a defect.** Keeping
  `passwordAuth: Enabled` retains a break-glass path and avoids a hard cutover on
  a live app; §13.4 lists Entra-only as *optional* hardening, and the proposal
  §3d + infra.md both document it as a deliberate, time-boxed risk acceptance. For
  a demo mid-cutover this tradeoff is reasonable. Entra-only is the natural future
  hardening step (which would also let `PGADMIN_PASSWORD` be dropped).
- No Key Vault provisioned — correct: after this cycle no application credential
  reaches the "cannot use managed identity" branch, so provisioning a vault to
  hold nothing would only add cost. The pattern stays specified for future secrets
  (§13.6).

## No agent provisioning (verified as far as the environment allows)

`infra.md` and `infra-proposal.md` both state only read-only validation
(`az bicep build`/`lint`) ran; nothing was provisioned, no identity/role
assignment/GitHub secret created, and the in-DB `pgaadauth_create_principal` step
was not run. Consistent with: no Azure credentials in the environment, no
role-assignment resources in Bicep, and all provisioning/RBAC/SQL steps living as
human-run commands in `infra/README.md` Phase 4. PROPOSE/APPLY-authoring
boundaries respected. I cannot cryptographically prove a negative from a read-only
seat, but nothing on disk contradicts the claim.

## Blocking issues

None.

## Non-blocking suggestions (do not block approval)

1. **The monolithic template can't defer the app env-flip, so the "split the
   deploy" instruction lacks an exact command.** Steps 1 and 4 of the cutover both
   live in one `main.bicep`, and `Postgres__UseEntraAuth=true` + the passwordless
   connection string are hard-wired with no parameter to keep the old
   password/secret path on a first apply. A careful human can work around it
   (enable the Entra admin out-of-band, do Phase 4 B/C, then apply), and the README
   warns loudly — but consider adding a boolean param (e.g. `apiUseEntraAuth`,
   default matching the current live state) so the first apply provisions the
   Postgres server change + AI component *without* flipping the app, then a second
   apply flips it after B/C. This would make the safe order enforceable by
   parameter rather than by careful reading. Not blocking for a demo with a
   single, well-warned operator.
2. **`Trust Server Certificate=true`** remains in the passwordless connection
   string — it encrypts but skips CA verification (MITM-susceptible). Pre-existing,
   out of this cycle's scope, already flagged in prior reviews/`changes.md`;
   production should move to `SslMode=VerifyFull` with the Azure Postgres CA.
   Defer.
3. **`Monitoring Metrics Publisher` grant vs. `DisableLocalAuth: true` ordering.**
   If the full template (which sets `DisableLocalAuth: true`) is applied before the
   manual role grant propagates, telemetry ingestion 403s silently (app keeps
   running; traces are just lost for a few minutes). This is telemetry-only, not an
   app-availability risk, and README Phase 4 step C already documents it. Fine to
   accept.
4. **End-to-end managed-identity auth is unverified against live Azure** (no
   Entra-enabled Postgres / live App Insights in this environment). A human should
   confirm real token auth after running the Phase 4 B bootstrap and the devops
   Bicep apply. Correctly disclosed by both engineer and tester; expected for this
   environment.

## Which stage (if any) needs to redo work

None. The backend implementation satisfies §12.10 and §13.11 acceptance criteria;
the IaC satisfies §12.6 and §13.10 and all five human-approved decisions; the
tests (44/44) genuinely cover the requirements including a real flakiness fix. The
outstanding items (the manual Phase 4 bootstrap, the role grant, branch
protection, and end-to-end live-Azure verification) are human one-time actions
explicitly assigned to humans by the spec, not agent rework. APPROVED.
