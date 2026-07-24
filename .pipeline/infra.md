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
