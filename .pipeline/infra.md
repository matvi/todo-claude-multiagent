# Infrastructure — APPLY-mode result (2026-07-24, cycle 1 of 3)

Branch: `pipeline/todo-app-azure-cicd`. Mode: **APPLY**. Files authored on disk;
**nothing was provisioned** — no `az create/deploy/update`, no identities, no role
assignments, no GitHub secrets/variables. Only local read-only validation
(`az bicep build/lint`, YAML parse) was run.

This supersedes the PROPOSE-mode `.pipeline/infra-proposal.md`.

## Approved SKU choices (user, in chat, 2026-07-24)

| Decision | Approved value |
|---|---|
| Postgres tier | **Standard_B1ms** (Burstable, spec default — confirmed, not B2s) |
| ACA plan | Consumption workload profile |
| ACR | Basic |
| `todo-web` (frontend) replicas | min **1** / max **2** |
| `todo-api` (backend) replicas | min **0** / max **3** |
| Open Q1 — Bicep scope | **RG-scoped**; human pre-creates `rg-todo-demo`; Bicep does not create the RG |
| Open Q2 — `AcrPull` grant | **manual bootstrap step**, NOT a role-assignment resource in the CD Bicep (CD SP has only `Contributor`) |

## Resources planned (8, matching specs §5.3 / §11.9)

| # | Resource | Name | SKU / tier | Why |
|---|---|---|---|---|
| 1 | Container Registry | `acrtododemo<suffix>` | **Basic**, admin user disabled | Cheapest ACR; two small images, low pull volume. Pull via managed identity. |
| 2 | Log Analytics workspace | `log-todo-demo` | **PerGB2018, 30-day** | Backs ACA logs; tiny ingestion; retention held at spec's 30 days. |
| 3 | Container Apps environment | `cae-todo-demo` | **Consumption** | Pay-per-use, scale-to-zero capable; no standing charge. |
| 4 | PostgreSQL Flexible Server | `pg-todo-demo-<suffix>` | **Burstable Standard_B1ms**, PG16, 32 GiB, no HA, LRS backup 7d, public access + "allow Azure services" firewall + required TLS | Cheapest usable managed Postgres; the one always-on cost center. |
| 5 | Postgres database | `tododb` | — | Single logical DB. |
| 6 | Postgres firewall rule | `AllowAllAzureServicesAndResourcesWithinAzureIps` (0.0.0.0) | — | Lets ACA egress reach Postgres without pinning IPs. |
| 7 | Container App — backend | `todo-api` | **Consumption**, 0.25 vCPU / 0.5 GiB, **min 0 / max 3**, system-assigned identity, external ingress :8080 | Scale-to-zero backend; DB connection string as ACA secret; CORS origin = frontend FQDN. |
| 8 | Container App — frontend | `todo-web` | **Consumption**, 0.25 vCPU / 0.5 GiB, **min 1 / max 2**, system-assigned identity, external ingress :8080 | Always-on single replica for instant SPA load. |

Resource group `rg-todo-demo` (region `eastus`) is **created out-of-band by the
human** (RG-scoped Bicep, per approved Open Q1) — it is not one of the 8.

Secrets: the Postgres admin password is an `@secure()` parameter (never committed);
the assembled connection string is built in `main.bicep` from that secure param and
surfaced only as the backend Container App secret `todo-db-connection` → env
`ConnectionStrings__TodoDb`. Nothing secret is emitted as an output.

## IaC files

- `infra/main.bicep` — RG-scoped entry point; wires all modules; assembles the
  connection string; sets backend CORS origin 0 to the frontend FQDN; emits
  `acrLoginServer`, `acrName`, `todoApiFqdn`, `todoWebFqdn`, principalIds, etc.
- `infra/main.parameters.json` — dev/demo **non-secret** params (location,
  names, replica counts). Secret password supplied at deploy time.
- `infra/modules/registry.bicep` — ACR (Basic).
- `infra/modules/loganalytics.bicep` — Log Analytics (30-day PAYG).
- `infra/modules/acaEnvironment.bicep` — ACA managed environment (reads LA shared
  key inline via `listKeys()`; never passed as a param or output).
- `infra/modules/postgres.bicep` — Postgres server + `tododb` + firewall rule.
- `infra/modules/containerApp.bicep` — reusable Container App (identity, ingress,
  secrets, scale). No `AcrPull` role-assignment resource (per approved Open Q2).
- `infra/README.md` — authoritative one-time human bootstrap (supersedes the
  specs §11.6 draft) + manual deploy commands.

## Validation result (actual, local, read-only)

- `az bicep build --file infra/main.bicep` → **exit 0, no warnings, no errors**
  (Bicep CLI 0.45.15, az 2.63.0). Compiled ARM confirms `Standard_B1ms` +
  `Burstable`, ACR `Basic`, `PerGB2018`, PG `16`, 32 GiB, the 0.0.0.0 firewall
  rule, `SystemAssigned` identities, and `tododb`.
- `az bicep lint --file infra/main.bicep` → **clean, no findings**.
- YAML parse (PyYAML `safe_load_all`) of `ci.yml`, `cd.yml`, `azure-pipelines.yml`
  → **all OK**.
- `az deployment group validate` / `what-if` were **not** run: no Azure
  credentials in this environment and the target RG does not exist yet (it's a
  human bootstrap step). Documented in `infra/README.md` for the human to run.

## CI/CD

- `.github/workflows/ci.yml` — **auto-triggering** (primary system). Trigger:
  `pull_request` → `main` + `workflow_dispatch`. Credential-free; no Azure login.
  Jobs: backend (`dotnet restore/build/test` on `TodoApi.sln`, Release),
  frontend (`npm ci` + `npm run build` + `npm test`/vitest, Node 22),
  docker (build both images, no push), iac (`az bicep build` + `az bicep lint`).
  No `npm audit` gate (esbuild/vite 5 dev-only advisories per changes.md would
  fail a naive high gate). No lint job — no `npm run lint` script exists.
- `.github/workflows/cd.yml` — **auto-triggering**. Trigger: `push` to `main`
  (i.e. on merge) + `workflow_dispatch`; `concurrency: cd-main`; `environment:
  production`; permissions `id-token: write` + `contents: read`. OIDC via
  `azure/login@v2` using the `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/
  `AZURE_SUBSCRIPTION_ID` **variables** — no client secret. Flow: read current
  images → deploy Bicep (idempotent, images preserved) → `az acr build` backend
  → update `todo-api` → read its FQDN → `az acr build` frontend with
  `--build-arg VITE_API_BASE_URL=https://<api FQDN>` → update `todo-web` →
  reconcile backend CORS. Honors the backend-before-frontend ordering constraint.
- `azure-pipelines.yml` — **manual-only** (`trigger: none`, `pr: none`). Azure
  DevOps parity (WIF ARM service connection, same CI + CD logic, CD gated to
  `refs/heads/main`). Kept behavior-equivalent and ready to flip on if the team
  standardizes on Azure DevOps; documented inline how to enable.

**Only GitHub Actions auto-triggers.** Azure Pipelines is deliberately manual-only
so the two systems never double-build or race a deploy onto the same Container App
revision.

## Note on §11.5 trigger wording vs. this pipeline's fixed CD gate

specs §11.5 specifies CD on `push` to `main` (merge). That is what `cd.yml`
implements. The generic pipeline convention of `pull_request: closed` +
`merged == true` was **not** used because, with branch protection requiring PRs
(no direct pushes to `main`), a `push` to `main` only occurs via a merged PR — so
the two are equivalent here, and specs §11.5 is explicit about `push`. Target is
Container Apps (matches specs) — no architecture mismatch.

## One-time identity setup required (human runs, once — NOT run here)

Full copy-pasteable script is in `infra/README.md`. Summary:
1. `az group create --name rg-todo-demo --location eastus` (Bicep is RG-scoped).
2. `az ad app create` + `az ad sp create` for `gh-todo-demo-cicd`.
3. `az ad app federated-credential create` for subjects: `ref:refs/heads/main`,
   `environment:production` (and optionally `pull_request`). No client secret.
4. `az role assignment create --role Contributor --scope <RG>` for the SP.
5. `gh variable set` `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` /
   `AZURE_SUBSCRIPTION_ID` (repository **variables** — not secret, OIDC stores no
   credential material) and `gh secret set PGADMIN_PASSWORD`.
6. **First infra deploy** (once) to create ACR + the app managed identities.
7. **ACR role grants** (after step 6): `AcrPush` for the CI SP, `AcrPull` for each
   Container App's system-assigned identity — all scoped to the ACR. These are
   excluded from the repeatable CD Bicep (approved Open Q2) because the CD SP has
   only `Contributor` and cannot create role assignments.

## Branch protection status on `main`

**Could not be verified — and is currently NOT enforced.** This repo has **no git
remote configured** and `gh` is not installed in this environment, so
`gh api repos/{owner}/{repo}/branches/main/protection` could not be run. There is
no GitHub repository wired up yet. Until a remote exists and protection is
configured, the "a human reviews and merges the PR" gate this pipeline relies on is
only a convention the agents follow — **not** enforced by GitHub. Steps to enable
are in `infra/README.md` (Settings → Branches → require PR, ≥1 approval, require the
CI status check, up-to-date branch, no force-push/delete, no admin bypass).

## To actually deploy the infra itself (human runs)

```bash
read -rs PGPWD; export PGPWD
az deployment group create \
  --resource-group rg-todo-demo \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD"
```
Dry-run first with `az deployment group validate` / `az deployment group what-if`
(same args). See `infra/README.md`.

## Estimated cost (rough, East US, idle-to-light demo — ESTIMATE ONLY)

Dominated by the always-on Burstable Postgres. Region/usage-dependent, not a quote.

| Resource | Est. monthly |
|---|---|
| ACR Basic | ~$5 |
| Log Analytics (PAYG, 30d) | ~$0–2 (first 5 GB/mo free) |
| ACA environment | $0 standing |
| `todo-api` (min 0) | ~$0–2 (scale-to-zero + free grant) |
| `todo-web` (min 1) | ~$4–12 (one always-on 0.25 vCPU replica) |
| Postgres B1ms + 32 GiB | ~$16–18 |
| **Total** | **~$25–37 / month** (idle floor ~$21–25) |

---

# Infrastructure — APPLY-mode result (2026-07-28, cycle 1 of 3) — Observability + managed-identity-first

Branch: `pipeline/appinsights-tracing` (off `main`, the live already-deployed app).
Mode: **APPLY**. Files authored/updated on disk; **nothing was provisioned** — no
`az deployment/create/update`, no identity/credential/role assignment, no in-DB
principal. Only local read-only validation (`az bicep build` + `az bicep lint`) was
run. This section is the delta on top of the 2026-07-24 section above (which stays
as-is); it does not supersede it.

## Approved decisions (user, in chat, 2026-07-28)

Applied against `.pipeline/infra-proposal.md` (2026-07-28). Deviations from that
proposal are called out explicitly in "Deviations" below.

| # | Decision | Approved value |
|---|---|---|
| — | App Insights | Workspace-based, reuses `log-todo-demo`, `kind: web`, `DisableLocalAuth: true`. No independent SKU/fee. |
| 1 | Postgres Entra administrator | The human's own Entra user — objectId `ee31140f-3164-439c-8fa9-9f7e5dbd1b2c`, UPN `ing.david.mata.az_outlook.com#EXT#@ingdavidmataazoutlook.onmicrosoft.com`, type `User`, tenant `6ce2ff72-209a-447d-bf53-9579c52c03f5`. (Proposal §3b offered a group; user chose their own user.) |
| 2 | Telemetry sampling | **100%** — backend OTel exporter default (`AzureMonitorOptions.SamplingRatio = 1.0`), a code-level setting. **No ARM property set** (see below). |
| 3 | `Monitoring Metrics Publisher` role placement | **Manual** human grant (Phase 4 step C), NOT Bicep — matches the existing `AcrPull` manual-grant convention. |
| 4 | Postgres auth mode | **Dual-auth** — `activeDirectoryAuth: Enabled` + `passwordAuth: Enabled`. Admin password kept as break-glass, NOT disabled. |
| 5 | Log Analytics daily ingestion cap | **1 GB/day** (`workspaceCapping.dailyQuotaGb: 1`) — **user OVERRODE the proposal's "no cap" default**. Hard cost circuit-breaker; accepted tradeoff = telemetry silently drops once the cap is hit that UTC day. |

## What was authored (files touched)

| File | Change |
|---|---|
| `infra/modules/appinsights.bicep` | **NEW.** Workspace-based `Microsoft.Insights/components@2020-02-02`, `kind: web`, `Application_Type: web`, `WorkspaceResourceId` → existing `log-todo-demo`, `IngestionMode: LogAnalytics`, `DisableLocalAuth: true`. Outputs `connectionString`, `id`, `name`. |
| `infra/modules/loganalytics.bicep` | Added `workspaceCapping.dailyQuotaGb` (new `dailyQuotaGb` param, default **1**). |
| `infra/modules/postgres.bicep` | Added 4 Entra-admin params; set `authConfig` (dual-auth + `tenantId`); added the `flexibleServers/administrators` child resource (name = admin objectId). |
| `infra/main.bicep` | New `appInsightsName` param + 4 `postgresEntraAdmin*` params; wired the `appinsights` module off `logAnalytics.outputs.id`; passed Entra-admin params to `postgres`; **dropped the `todo-db-connection` secret + `secretEnvVars`**; `ConnectionStrings__TodoDb` now a passwordless plain env var (`Username=todo-api`), plus new plain env vars `Postgres__UseEntraAuth=true` and `APPLICATIONINSIGHTS_CONNECTION_STRING`; added `appInsightsName`/`appInsightsId` outputs. `todo-web` unchanged. |
| `infra/main.parameters.json` | Added the 4 `postgresEntraAdmin*` non-secret param values (approved values). |
| `infra/README.md` | New **Phase 4** (App Insights + MI Postgres auth) with steps A/B/C, the **CUTOVER SEQUENCING** callout, an ad-hoc-deploy caveat, and Files-table updates. |

**Managed-identity name / Postgres role / `Username=` — single source of truth:**
`todo-api` (the Container App name == its system-assigned MI name == the Postgres
role created in Phase 4 B == `Username=` in the passwordless connection string).

**Net secret posture after this cycle:** the `todo-api` Container App has **zero
Container Apps secrets**. All three of its config values are non-secret plain env
vars. Only remaining credential anywhere is `PGADMIN_PASSWORD` (deploy-time CI/CD
secret / break-glass), out of app-runtime scope. No Key Vault provisioned (§13.6).

## Sampling: why NO ARM property was set (verified, not fabricated)

`Microsoft.Insights/components` *does* expose a `SamplingPercentage` property, but
it governs the **classic/legacy ingestion-sampling** path and does **not** drive the
`Azure.Monitor.OpenTelemetry.AspNetCore` exporter. The requested 100% sampling is the
exporter's own default (`AzureMonitorOptions.SamplingRatio = 1.0`), configured in
**backend code** (already merged), not in ARM. Setting the ARM property would be
misleading (it would imply it controls the OTel path). So it is intentionally left
unset, with a note in `modules/appinsights.bicep` and README Phase 4. No fabricated
property was added.

## CUTOVER SEQUENCE the human MUST follow (live app — wrong order = DB outage)

`main` is a running production app; `todo-api` runs `Migrate()` at startup, so a
cold replica can fail to start if it cannot reach the DB. Strict order (full detail
+ the exact SQL/`az` commands are in `infra/README.md` Phase 4):

1. **Deploy the Postgres server change** — enable Entra auth + set the Entra admin
   (idempotent, does not disturb the running password path).
2. **Create the in-DB principal** for todo-api's MI + grant schema/DDL rights:
   connect to the `postgres` DB as the Entra admin and run
   `pgaadauth_create_principal('todo-api', false, false)`, then grant/own `public`
   on `tododb`. (Manual, in-database — cannot be Bicep.)
3. **Grant `Monitoring Metrics Publisher`** to the todo-api MI on the AI component
   (`az role assignment create`, scope = `appInsightsId` output) — before telemetry
   starts, or ingestion 403s silently.
4. **ONLY THEN** roll `todo-api` with the new env (single revision flip):
   `Postgres__UseEntraAuth=true`, passwordless `ConnectionStrings__TodoDb`,
   `APPLICATIONINSIGHTS_CONNECTION_STRING`, and the `todo-db-connection` secret
   dropped.

Because the Bicep now embeds all of step 4 (env vars + no secret), a single
full-template apply does steps 1 AND 4 together — so **run steps 2 and 3 before the
first full apply**, or split the deploy (server first, app last). Doing step 4
before step 2 is the concrete outage scenario.

> Note: per the established image/ordering model (README), **CD does not deploy
> Bicep** — it only rolls images imperatively and (confirmed via grep) does NOT set
> any env var or secret. So these new env vars take effect only on a **manual**
> `az deployment group create`, which IS cutover step 4. No `cd.yml` /
> `azure-pipelines.yml` change was needed this cycle.

## Validation result (actual, local, read-only)

- `az bicep build --file infra/main.bicep` → **exit 0, no errors, no warnings**
  (Bicep CLI **0.45.15**).
- `az bicep lint  --file infra/main.bicep` → **exit 0, clean, no findings**.
- Compiled ARM (transient `main.json`, since removed) inspected to confirm the delta
  landed: `DisableLocalAuth: true`, `WorkspaceResourceId`, `dailyQuotaGb`,
  `authConfig.activeDirectoryAuth: Enabled` + `passwordAuth: Enabled`, the
  `flexibleServers/administrators` resource, env vars `Postgres__UseEntraAuth` +
  `APPLICATIONINSIGHTS_CONNECTION_STRING`, and **zero** occurrences of
  `todo-db-connection` (secret successfully dropped).
- `az deployment group validate` / `what-if` were **not** run — no Azure credentials
  in this environment, and running them would touch the live subscription. They are
  documented in `infra/README.md` for the human to run as the dry-run before applying.

## Manual human bootstrap this cycle adds (NOT run by any agent)

Full copy-pasteable commands are in `infra/README.md` **Phase 4**. Summary:
- **A** — Postgres Entra admin + dual-auth: baked into Bicep params (applied on
  template deploy; no separate command). Non-secret identity metadata.
- **B** — `pgaadauth_create_principal('todo-api', false, false)` on the `postgres`
  DB as the Entra admin + grant/own `public` schema on `tododb`. In-database SQL.
- **C** — `az role assignment create --role "Monitoring Metrics Publisher"
  --assignee <todoApiPrincipalId> --scope <appInsightsId>`. Requires `User Access
  Administrator`/`Owner`.

## Branch protection status on `main`

Unchanged from the 2026-07-24 section and **still not verifiable here**: no git
remote is configured and `gh` is not available in this environment, so
`gh api repos/{owner}/{repo}/branches/main/protection` cannot be run. Until a remote
exists and protection is enabled, the "a human reviews and merges the PR" gate is
only a convention, not GitHub-enforced. Enable steps remain in `infra/README.md`
(Settings → Branches → require PR, ≥1 approval, require the CI status check,
up-to-date branch, no force-push/delete, no admin bypass).

## Deviations from the proposal (with justification)

1. **Daily ingestion cap: 1 GB/day applied** — the proposal §3e **recommended NO
   cap**; the user explicitly overrode this to get a hard cost ceiling, accepting
   that telemetry silently drops once the cap is hit on a given day. Implemented via
   `workspaceCapping.dailyQuotaGb: 1` and documented as a tradeoff in the module +
   README. This is a deliberate, user-directed deviation.
2. **Entra admin = the user's own Entra user**, not the group the proposal §3b
   preferred — the user supplied their own objectId/UPN. Functionally equivalent for
   the bootstrap; a group would survive staff changes but was not chosen.
3. No `SamplingPercentage` ARM property (see the sampling section) — a considered
   non-action, not a deviation from any approved decision; 100% is honored in code.

Everything else follows the proposal's recommendations as approved (manual role
grant, dual-auth Postgres, workspace-based App Insights reusing `log-todo-demo`,
zero app secrets, no Key Vault).

## Estimated cost delta (this cycle)

**~$0 fixed added.** Workspace-based App Insights has no fixed fee; role assignment,
MI auth, and enabling Postgres Entra auth are free; dropping a Container Apps secret
is free. The only variable is telemetry ingestion, billed usage-based through
`log-todo-demo` at the Log Analytics per-GB rate (~5 GB/month free grant; a
low-traffic demo is expected to sit inside it). The new **1 GB/day cap** bounds the
worst case to ~1 GB/day of ingestion (est. **~$2.30–3.00/GB** beyond the free grant,
region-dependent — ESTIMATE ONLY). Steady-state expected added cost ≈ **$0/month**.

---

# Infrastructure — APPLY-mode result (2026-08-06) — Entra-ONLY Postgres auth for the app (specs §14)

Branch: `pipeline/entra-passwordless-connection-string`. Mode: **APPLY**.
Files authored/updated on disk; **nothing was provisioned** — no
`az deployment group create`, no `az containerapp update`, no identity, credential,
role assignment, in-DB principal, GitHub secret or variable. The only Azure commands
run were **read-only / dry-run**: `az account show`, `az bicep build`,
`az bicep lint`, `az deployment group validate`, `az deployment group what-if`.

This section is the delta on top of the two sections above; it does **not**
supersede them. It supersedes `.pipeline/infra-proposal.md` (2026-08-06).

## What §14 actually asked infra for (the delta)

Specs §14.8 is deliberately tiny, and §14 is explicit that **no Azure resource, SKU,
region, server-auth setting or role assignment changes**. **Cost delta: $0.** The
whole infra delta is:

| # | Item | Status |
|---|---|---|
| 1 | `infra/main.bicep` — delete the `Postgres__UseEntraAuth` env var from `todo-api` | **done** |
| 2 | `infra/main.bicep` — correct the two comment blocks that still describe a dual-mode design | **done** |
| 3 | `infra/README.md` — imperative Entra-only cutover runbook (the app-side change is imperative; **CD never applies Bicep**, so the Bicep edit alone changes nothing live) | **done** (Phase 4 step D) |
| 4 | `infra/README.md` — `pgaadauth_list_principals(false)` as the **verify-first** pre-check + `pgaadauth_create_principal_with_oid` as the by-OID repair path | **done** (Phase 4 step B) |
| 5 | `infra/README.md` — note that the **server keeps password auth for humans, intentionally** | **done** (Phase 4 preamble) |
| 6 | `infra/README.md` — fix the 5 stale spots (Files-table "secret", the old cutover callout, the `Postgres__UseEntraAuth=true` step, the "secret DROPPED at cutover" wording, the "First time applying" callout) | **done** |
| 7 | `infra/modules/postgres.bicep`, `infra/main.parameters.json` | **NO CHANGE** (§14.8(2)/§14.12 — dual auth, `todoadmin`, `@secure() postgresAdminPassword`, `PGADMIN_PASSWORD` all stay) |
| 8 | `.github/workflows/cd.yml`, `azure-pipelines.yml` | **NO CHANGE** (§14.8 — verified by grep that neither ever writes `ConnectionStrings__TodoDb` or `Postgres__UseEntraAuth`, so a CD run after the cutover will not revert it) |

The `todoDbConnectionString` **value** needed no change — it has been passwordless
with `Username=todo-api` and free of a Container Apps secret since the 2026-07-28
cycle. The only thing §14 removed from Bicep was the dead flag.

## Approved decisions (user, in chat, 2026-08-06)

Applied against `.pipeline/infra-proposal.md` (2026-08-06). **Two of the three
decisions OVERRIDE that proposal's own recommendation** — called out explicitly
below, because agreeing with the operator against my own recommendation is exactly
the kind of thing a reviewer should be able to see rather than infer.

| # | Decision | Approved value | vs. proposal |
|---|---|---|---|
| 1 | When to delete the `todo-db-connection` secret | **DURING the cutover** — same sequence as the env flip (README Phase 4 step D3b, immediately after D3a) | **OVERRIDE.** Proposal §3a recommended *after* at least one healthy Entra revision, to preserve revision rollback. User chose the shorter-secret-exposure option instead and accepted the loss of the rollback net. |
| 2 | `Trust Server Certificate=true` in the connection string | **Leave as-is this cycle.** No change to `VerifyFull`. Recorded as a follow-up note only. | Matches proposal §3b's recommendation. |
| 3 | Where the cutover runbook lives | **Rewrite Phase 4 of `infra/README.md` in place** | **OVERRIDE.** Proposal §3c recommended a *new Phase 5* to preserve the historical 2026-07-28 Phase 4 record. User chose the in-place rewrite (shorter file, one runbook, no ambiguity about which phase is current). The superseded 2026-07-28 narrative remains recoverable from git history and is summarised in the section above. |

### Consequence of decision 1, stated plainly

Deleting `todo-db-connection` inside the cutover sequence means **there is no
revision-rollback safety net.** Once the secret is gone the previous revision's
`secretRef` dangles, so `az containerapp revision activate "$PREV_REV"` will not by
itself restore a working app. `PREV_REV` is still captured **before** the cutover
(README D1) for diagnosis and for the recovery path, but the runbook now says
explicitly that recovery is either **fix-forward** (the usual cause is a step B
principal/OID mismatch, which does not need the secret) or **re-create the secret
with `az containerapp secret set` and then activate `$PREV_REV`** — which only works
because the server still accepts password auth, and only with a pre-§14 image that
still has a password code path.

The runbook therefore requires the operator to run the cutover (D3), the health
check **and a real DB-backed request** (`GET /api/todos`, D4), and the startup-log
check (D5) **inside the same maintenance window**, and to not consider the cutover
done until D4 and D5 both pass. That is written into the README as a one-line
caveat at the top of step D, per the user's instruction.

## Files touched this cycle

| File | Change |
|---|---|
| `infra/main.bicep` | Deleted the `Postgres__UseEntraAuth` env var from the `todoApi` module's `envVars`. Rewrote the comment above `todoDbConnectionString` to the §14.8 invariant (passwordless + non-secret; `Username=` == `pgaadauth` role == Container App name == MI name; the app strips any `Password=`/`Passfile=` and will not fall back). Rewrote the comment above the `todoApi` module: two non-secret plain env vars, not three, and an explicit "there is no `Postgres__UseEntraAuth` flag any more". **No value, resource, SKU or parameter change.** |
| `infra/README.md` | Files-table row for `main.bicep` corrected (plain env var, not a Container Apps secret). **Phase 4 rewritten in place** (decision 3): new title, the `passwordAuth: Enabled` "kept on purpose" note, a rewritten CUTOVER SEQUENCING callout built on "the app has no password path" + "CD does not apply Bicep", step **B** turned into verify-first `pgaadauth_list_principals` with a by-OID repair path and the "do not add the MI as a server admin" warning, and a **new step D** — the full imperative cutover runbook. "First time applying" callout re-pointed. |

Not touched, deliberately: `infra/modules/postgres.bicep`, `infra/main.parameters.json`,
`infra/modules/*` (all others), `.github/workflows/ci.yml`, `.github/workflows/cd.yml`,
`azure-pipelines.yml`.

## Validation result (actual, this session)

Unlike the previous two cycles, Azure credentials **were** available
(`az account show` → subscription `9ba6633f-058c-4269-8b0f-086ab331ef26`, user
`ing.david.mata.az@outlook.com`), so the real dry-runs were executed against the
live RG. **No apply was run.**

- `az bicep build --file infra/main.bicep --stdout` → **exit 0, no errors, no
  warnings** (Bicep CLI **0.45.15**, az CLI **2.63.0**).
- `az bicep lint --file infra/main.bicep` → **exit 0, clean, no findings.**
- `az deployment group validate -g rg-todo-demo -f infra/main.bicep -p infra/main.parameters.json -p postgresAdminPassword=<throwaway>` →
  `{"errors": null, "state": "Succeeded"}`. (A throwaway value was passed for the
  `@secure()` param purely to satisfy the required parameter; `validate` applies
  nothing and no password was changed.)
- `az deployment group what-if` (same args) → the `todo-api` delta is exactly the
  intended §14 end state:

  ```
  Modify todo-api
    Delete  properties.configuration.secrets            = [{"name": "todo-db-connection"}]
    Delete  properties.template.containers.0.env.1.secretRef = "todo-db-connection"
    Create  properties.template.containers.0.env.1.value     = "Host=...;Username=todo-api;Ssl Mode=Require;Trust Server Certificate=true"
    Modify  properties.template.containers.0.env.2.name : "Postgres__UseEntraAuth" -> "APPLICATIONINSIGHTS_CONNECTION_STRING"
    Delete  properties.template.containers.0.env.3      (index shift — the array is one shorter now)
  ```

  i.e. the flag is gone, the connection string moves from `secretRef` to a plain
  passwordless value, and the secret is dropped. Every other resource
  (`todo-web`, `cae-todo-demo`, ACR, `pg-todo-demo-cus01`, `tododb`, the firewall
  rule, `log-todo-demo`, `appi-todo-demo`) shows only Azure-side default noise —
  **no SKU, tier, region, replica, storage or auth-config change anywhere.**
  Confirms §14's "cost delta: zero".

- **One thing the what-if surfaced that is worth recording** (pre-existing, not
  introduced here): a full-template apply would also set
  `properties.template.containers.0.image` back to
  `mcr.microsoft.com/k8se/quickstart:latest`, because `todoApiImage`/`todoWebImage`
  default to the bootstrap placeholder and CD does not apply Bicep. That is now
  called out in the README's "Deploy the infra yourself" caveat with the commands
  to read the currently-running tags and pass them through.

## CI/CD (unchanged this cycle)

- `.github/workflows/ci.yml` — **auto-triggering** (primary system). Unchanged.
- `.github/workflows/cd.yml` — **auto-triggering**. Unchanged. Verified by grep
  that it never writes `ConnectionStrings__TodoDb` or `Postgres__UseEntraAuth`; it
  only sets the ACR registry identity, `az containerapp update --image`, and
  `--set-env-vars Cors__AllowedOrigins__0=…` (which replaces only that one
  variable). So a CD run **after** the cutover will not revert the passwordless
  connection string.
- `azure-pipelines.yml` — **manual-only** (`trigger: none`, `pr: none`). Unchanged.

**Only GitHub Actions auto-triggers**, so the two systems never double-build or race
a deploy onto the same Container App revision.

**Standing, pre-existing trigger deviation (not introduced this cycle).** `cd.yml`
triggers on `push: [main]`, not this pipeline's usual `pull_request: closed` +
`merged == true`. That was a deliberate, documented choice (specs §11.5 is explicit
about `push`; with branch protection requiring PRs the two are equivalent) — see the
"Note on §11.5 trigger wording" section above. §14.8 forbids a CD change this cycle,
so it was left alone. **But note the interaction with the branch-protection finding
below: while `main` is unprotected, a direct `git push` to `main` deploys to
production with no PR and no human review.** With the `pull_request: closed` form,
an unprotected `main` would at least not auto-deploy on a direct push. This is the
one place where the deviation is not equivalent, and it is only equivalent once
branch protection is actually on.

## Branch protection status on `main` — NOW ACTUALLY CHECKED

The previous two cycles recorded "could not be verified — no git remote". A real
remote now exists: `git@github.com:matvi/todo-claude-multiagent.git` (public).

**Result: branch protection on `main` is NOT configured.** Verified read-only,
2026-08-06:

- `GET /repos/matvi/todo-claude-multiagent/branches/main` →
  `"protected": false`, `"protection": {"enabled": false,
  "required_status_checks": {"enforcement_level": "off", "contexts": [], "checks": []}}`
- `GET /repos/matvi/todo-claude-multiagent/rulesets` → `[]` (no repository
  rulesets either — so it is not protected by the newer rulesets mechanism instead)

Method caveat, so this is reproducible: `gh` **is** installed
(`C:\Program Files\GitHub CLI\gh.exe`) but is **not authenticated**
(`gh auth status` → "You are not logged into any GitHub hosts"), so
`gh api repos/{owner}/{repo}/branches/main/protection` returned **401**. The repo is
public, so the two unauthenticated endpoints above were used instead; between
`"protected": false` and an empty ruleset list the answer is unambiguous.

**What this means, plainly:** the "a human reviews and merges the PR" gate that this
entire pipeline is built around is **not enforced by GitHub**. It is a convention the
agents happen to follow. Anyone (including any agent with push rights) can push
straight to `main`, which — because `cd.yml` triggers on `push: [main]` — deploys to
production with zero review. **This is the single highest-value unclosed item in
this repo's setup, and it costs $0 and about 60 seconds to fix.**

**To turn it on** (repo admin, GitHub UI: Settings → Branches → Add branch
protection rule, or Settings → Rules → Rulesets):
- Branch name pattern `main`.
- ✅ Require a pull request before merging; **require ≥ 1 approval**; dismiss stale
  approvals on new commits.
- ✅ Require status checks to pass → select the **CI** workflow's jobs; require the
  branch to be up to date.
- ✅ Do not allow bypassing the above settings (**no admin bypass**).
- ✅ Disallow force pushes and branch deletion.
- Squash merge preferred.

Equivalent one-liner once `gh auth login` has been done (human runs this, not an
agent — it is a repo-configuration change):

```bash
gh api -X PUT repos/matvi/todo-claude-multiagent/branches/main/protection \
  --input - <<'JSON'
{
  "required_status_checks": {"strict": true, "contexts": [
    "Backend (.NET 10 build + test)",
    "Frontend (Vite build + vitest)",
    "Docker build (both images, no push)",
    "IaC (bicep build + lint)"
  ]},
  "enforce_admins": true,
  "required_pull_request_reviews": {"required_approving_review_count": 1, "dismiss_stale_reviews": true},
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON
```

(The `contexts` values are the **job display names** from `ci.yml` — `name:` is set
on all four jobs, so those, not the job ids `backend`/`frontend`/`docker`/`iac`, are
what appear as check-run names. Easiest sanity check: open any recent PR's Checks
tab and copy the names verbatim.)

## One-time identity setup (unchanged — nothing new this cycle)

No new identity, federated credential, role assignment or GitHub variable/secret is
required by §14. The Phase 1 OIDC setup (`az ad app create` /
`az ad app federated-credential create` / `az role assignment create --role
Contributor`) and the repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID` (non-secret — OIDC stores no credential material) plus the
`PGADMIN_PASSWORD` repo **secret** are all as documented in `infra/README.md`
Phase 1 and in the 2026-07-24 section above. Still zero stored Azure credentials.

The only human-run steps §14 adds are **in-database SQL** (README Phase 4 step B —
verify with `pgaadauth_list_principals`, repair by OID only if needed) and the
**imperative cutover** (README Phase 4 step D). Neither is an identity or role
creation; per §11.6/§14.7 no agent runs either.

## To actually deploy / cut over (human runs — NOT run here)

Two different things, do not confuse them:

1. **The live cutover** (this is what §14 needs) — `infra/README.md` **Phase 4
   step D**: verify step B, capture `PREV_REV`, confirm the new image is running,
   then
   ```bash
   az containerapp update -g rg-todo-demo -n todo-api \
     --set-env-vars "ConnectionStrings__TodoDb=Host=pg-todo-demo-cus01.postgres.database.azure.com;Port=5432;Database=tododb;Username=todo-api;Ssl Mode=Require;Trust Server Certificate=true" \
     --remove-env-vars Postgres__UseEntraAuth
   az containerapp secret remove -g rg-todo-demo -n todo-api --secret-names todo-db-connection
   ```
   then verify `/health` **and** `GET /api/todos` **and** the `Postgres auth:` log
   line, all in the same window.

2. **Re-applying the Bicep** (only needed on a fresh bootstrap, or if infra itself
   changes) — and if you do it against the live app, pass the running image tags,
   or it reverts both apps to the placeholder image:
   ```bash
   read -rs PGPWD; export PGPWD
   az deployment group what-if -g rg-todo-demo -f infra/main.bicep \
     -p infra/main.parameters.json -p postgresAdminPassword="$PGPWD"   # ALWAYS first
   az deployment group create  -g rg-todo-demo -f infra/main.bicep \
     -p infra/main.parameters.json -p postgresAdminPassword="$PGPWD"
   ```

## Follow-ups explicitly deferred (not forgotten)

1. **`Trust Server Certificate=true`** (approved decision 2) — encrypts but skips
   certificate verification, so MITM-susceptible. Deferred by the user this cycle;
   moving to `Ssl Mode=VerifyFull` is a one-token edit but changes TLS behavior and
   needs its own live verification rather than riding inside a no-fallback cutover.
   Noted in README Phase 4.
2. **Branch protection on `main`** — see above. Highest value, zero cost.
3. **CD drift detection** (specs §14.13 Q2) — now that CD never applies Bicep,
   nothing asserts the live app's env/connection-string shape. Worth a follow-up
   cycle; out of scope here.
4. **`.pipeline/deployment-lessons-learned.md` §5a is UNRESOLVED and not on this
   branch.** §14.9 requires the cutover outcome to be written back into it, but the
   file only exists on the `docs/deployment-lessons-learned` branch (commit
   `011da3d`). Whoever runs the cutover needs to know where to write, or the
   close-out is lost. Flagged, not fixed — moving/merging that file is not this
   cycle's scope.

## Estimated cost delta (this cycle)

**$0/month.** Removing an env var, editing comments, changing a Container App env
var from a `secretRef` to a plain value, and deleting one Container Apps secret are
all free operations on resources that already exist. No SKU, replica count, storage
size, retention setting or region changed — confirmed by the `what-if` above.

Standing total is unchanged (**ESTIMATE ONLY**, region/usage-dependent, not a
quote): **~$25–37/month**, dominated by the always-on Burstable `Standard_B1ms`
Postgres (~$16–18) and the `todo-web` min-1 replica (~$4–12), plus ACR Basic (~$5).
