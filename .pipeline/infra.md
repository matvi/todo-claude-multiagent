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
