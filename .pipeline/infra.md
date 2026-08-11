# Infrastructure — dev environment + dev→prod promotion pipeline

**Cycle:** specs.md §15, branch `pipeline/dev-environment`
**Mode:** APPLY (SKU choices approved) · **Date:** 2026-08-07
**Supersedes** the previous version of this file in full.

> **Nothing was provisioned by this run.** No Azure resource was created,
> modified or deleted. No identity, federated credential, role assignment,
> GitHub secret/variable/environment, branch-protection rule, or in-database
> principal was created. Validation was dry-run only (`az bicep build`,
> `az bicep lint`, `az deployment group what-if`). Every command in the
> "human runs" sections below was **authored, not executed**.

---

## 0. Approvals this cycle stands on

| Decision | Approved by | Detail |
|---|---|---|
| **Cost — dev Postgres Option A** | **User, in chat, 2026-08-07** | Dedicated `pg-todo-demo-dev-cus01`, `Standard_B1ms` / 32 GiB / Central US, always on. **~+$19–25/month**, of which **~$18.18 is an unavoidable always-on floor** (Postgres Flexible Server cannot scale to zero). Options B (stop/start) and C (second DB on the prod server) were presented in `.pipeline/infra-proposal.md` §2 and explicitly not chosen. |
| **CD trigger — Option A of proposal §6(a)** | **User, in chat, 2026-08-07** | The **single push-triggered workflow** (`push: [develop, main]`) per specs §15.9.2, **not** my standing merged-PR-only default and not the split-workflow alternative. Rationale and the caveat this creates are in §5 below. |
| **`azure-pipelines.yml` stays manual-only** | **User, in chat, 2026-08-07** | Restructured to the three-stage shape for parity, but `trigger: none` / `pr: none`. Only one system may auto-trigger. |
| **Branch protection on `main` is part of THIS rollout** | **User, in chat, 2026-08-07 — an explicit elevation beyond what the proposal asked** | It is no longer a deferred follow-up. It is sequenced as **Phase E1, the first step of the first phase**, with the exact `gh api -X PUT` command written inline in `infra/README.md`. See §7. |
| Architecture (own ACA env, own Postgres, own App Insights; shared workspace + ACR + RG) | Architect, specs §15 + `architecture-memory.md` 2026-08-06 | Not re-litigated here. |
| SKU choices for everything else | Not a decision — see §2 | ACA Consumption, workspace-based App Insights, 0.25 vCPU/0.5 GiB, min0/max1: each has exactly one defensible answer. |

---

## 1. ⛔ Blocker found by validation — read this first

`az deployment group what-if` on the new template **fails preflight**:

```
MaxNumberOfRegionalEnvironmentsInSubExceeded — The subscription
'9ba6633f-058c-4269-8b0f-086ab331ef26' cannot have more than 1 Container App
Environments in East US.
```

and, when retried in another region as a diagnostic:

```
MaxNumberOfGlobalEnvironmentsInSubExceeded — The subscription
'9ba6633f-058c-4269-8b0f-086ab331ef26' cannot have more than 1 Container App
Environments.
```

Confirmed directly against the quota API:

```
Microsoft.App/locations/eastus/usages -> ManagedEnvironmentCount: currentValue 1, limit 1
subscriptionPolicies.quotaId          -> FreeTrial_2014-09-01  (spendingLimit: On)
```

**This is a subscription quota, not a template defect.** The cap is
**subscription-global**, so relocating `cae-todo-demo-dev` to another region does
not work either — the per-region `usages` endpoint reports `limit: 1` for
`centralus`/`eastus2`/`westus2` too, and the global error fires regardless.

**Consequence: Phase A cannot run today.** The IaC, the workflows and the runbook
are all complete and validated; provisioning is blocked on a subscription change
that only a human can make. Resolution paths, in order of preference:

1. **Upgrade the subscription from Free Trial to Pay-As-You-Go, then request a
   `ManagedEnvironmentCount` increase.** Keeps the approved architecture exactly
   as designed. Bonus: the same upgrade re-enables **ACR Tasks** (`az acr build`),
   which is disabled on this subscription for the same reason and is why both
   pipelines carry a `docker build`/`docker push` workaround.
2. Dev in a **second subscription** — breaks the "same resource group" directive
   (§15.15.1); an architecture change, back to the architect.
3. **Share `cae-todo-demo`** — discards the network/identity isolation that is the
   entire justification of §15.4.1. Requires a superseding
   `architecture-memory.md` entry. **Do not adopt silently.**

This is recorded prominently at the top of `infra/README.md`'s rollout section
too, so nobody discovers it mid-deploy.

---

## 2. Resources planned

Four new Azure resources. Everything else is shared and referenced as `existing`.

| Resource | New/shared | SKU / tier | Why this choice |
|---|---|---|---|
| **`pg-todo-demo-dev-cus01`** — PostgreSQL Flexible Server, **Central US** | **NEW** | **Burstable `Standard_B1ms`**, 32 GiB, PG16, 7-day LRS PITR, no HA, no geo-redundancy | **The only real SKU decision, and the entire cost delta.** Verified against the live price list: the complete Burstable ladder in Central US starts at `B1MS` — there is nothing below it, 32 GiB is the minimum provisionable storage, 7 days the minimum retention. Dev is already at the absolute floor of the service. The decision was therefore "dedicated server or not", and **Option A (dedicated) was approved**: `pgaadauth` principals are **server-global**, so a shared server would leave the dev identity a principal on the *production* server with `CONNECT` on prod's database by PostgreSQL default. Isolation would rest on a human remembering a `REVOKE` with no IaC representation and no drift detection. Central US is forced — this subscription has a Postgres offer restriction in East US. |
| **`cae-todo-demo-dev`** — Container Apps Environment, East US | **NEW** | **Consumption only** (`workloadProfiles` unset) | **$0 standing charge**, which is precisely why dev can afford its own security/network boundary. The alternative — a workload-profiles environment — adds **$0.10/hr (~$73/mo)** the moment a Dedicated profile is attached, to enable VNet scenarios §15.14 puts out of scope. **No decision to make.** ⛔ blocked by the quota in §1. |
| **`appi-todo-demo-dev`** — Application Insights | **NEW** | **Workspace-based**, no SKU, `DisableLocalAuth: true` | Workspace-based components have **no SKU and no standing fee**; all billing flows through the shared workspace's existing `PerGB2018`. The only "choice" is which workspace, and the standing constraint mandates `log-todo-demo`. **No decision to make.** |
| **`todo-api-dev`**, **`todo-web-dev`** — Container Apps | **NEW** | 0.25 vCPU / 0.5 GiB (the ACA minimum), **min 0 / max 1** each | Minimum allocation, matching prod. Replica counts fixed by §15.10 and deliberately cheaper than prod — dev scales to **zero** when idle, which is why dev *compute* costs ~$0 at rest. Cold start is absorbed by `verify-dev`'s retry/backoff. **No decision to make.** |
| `acrtododemo3bkqjv23abtua` — Container Registry | **SHARED**, `existing` | Basic (unchanged) | specs.md already designates this ACR. One registry across both environments is **what makes artifact promotion possible at all**: `deploy-dev` builds `todo-api:<sha>` once, `deploy-prod` re-points production at that identical tag. **$0 delta.** |
| `log-todo-demo` — Log Analytics | **SHARED**, `existing` | `PerGB2018`, 30d, **1 GB/day cap** (unchanged) | Standing constraint: never create a second workspace. Dev/prod logs separate cleanly by the `EnvironmentName_s` column. **$0 fixed delta.** ⚠️ inherited risk below. |
| `rg-todo-demo` | **SHARED** | — | User directive; no new resource group. |
| `cae-todo-demo`, `todo-api`, `todo-web`, `pg-todo-demo-cus01`, `appi-todo-demo` | **UNTOUCHED** | — | Production is byte-for-byte unchanged by this cycle. `infra/main.bicep` is not modified. |

### ⚠️ Inherited risk, accepted and restated: the shared 1 GB/day ingestion cap

`log-todo-demo` caps ingestion at 1 GB/day, and **dev now shares that cap**. If
dev ingestion exhausts it, **production telemetry silently stops for the rest of
that UTC day**. Accepted because dev traffic is manual and tiny. Levers, in order:

1. **Detect:**
   ```kusto
   Usage
   | where TimeGenerated > ago(7d) and IsBillable == true
   | summarize IngestedGB = sum(Quantity) / 1000 by bin(TimeGenerated, 1d), DataType
   | order by TimeGenerated desc
   ```
2. **Respond:** raise to 2 GB/day — `dailyQuotaGb: 2` in
   `infra/modules/loganalytics.bicep` **and** the imperative
   `az monitor log-analytics workspace update -g rg-todo-demo -n log-todo-demo --quota 2`
   (CD does not apply Bicep, so both are needed).
3. **Escalate only if recurring:** a dedicated `log-todo-demo-dev` workspace
   contradicts a standing constraint and needs a superseding
   `architecture-memory.md` entry first.

---

## 3. IaC files

| Path | Status | What it provisions / contains |
|---|---|---|
| `infra/main.dev.bicep` | **NEW** | RG-scoped dev entry point. ACR + `log-todo-demo` as `existing` (creates neither). Four module calls, all reusing `infra/modules/*` **unchanged**: `acaEnvironment.bicep` → `cae-todo-demo-dev` (eastus); `appinsights.bicep` → `appi-todo-demo-dev`; `postgres.bicep` → `pg-todo-demo-dev-cus01` (centralus, db `tododb`); `containerApp.bicep` ×2 → `todo-web-dev` **first** (its FQDN feeds CORS), then `todo-api-dev`. Outputs both `principalId`s, `appInsightsDevId`, both FQDNs, and the Postgres name/FQDN — all consumed by the manual Phase B/C/D grants. |
| `infra/main.dev.parameters.json` | **NEW** | **Non-secret parameters only**: location, postgresLocation, resource names, `postgresAdminUser`, the four `postgresEntraAdmin*` values, replica counts. `postgresAdminPassword` is `@secure()` and is **not here and never will be**. |
| `infra/main.bicep`, `infra/main.parameters.json` | **UNCHANGED** | Production. Deliberately not modified — see §3.1. |
| `infra/modules/*.bicep` (all 6) | **UNCHANGED** | Reused verbatim by both templates. This is what actually guarantees "dev has the same infrastructure as prod". |
| `infra/README.md` | **MODIFIED** | Phases A–F human runbook, with branch protection as Phase E1. |

### 3.1 Why a separate template rather than parameterizing `main.bicep`

Production's live configuration has **permanently drifted** from `main.bicep` by
design, because §14.2 established that CD does not apply Bicep — it rolls images
imperatively with `az containerapp update`. Turning `main.bicep` into a
two-environment template would make **every dev bootstrap a potential production
deployment** that silently reverts that drift. The accepted cost is duplication
between two top-level templates; the mitigation is that both call the **same
modules** (where all real resource shape lives) and CI lints **both**. A little
duplication is far cheaper than an accidental prod deploy.

### 3.2 Secret hygiene — verified, not assumed

- **No `Password=` and no `Passfile=`** in any connection string. `todo-api-dev`
  authenticates to Postgres with an Entra token from its managed identity; the
  application has no password path at all since §14.
- **Zero Container Apps secrets, zero application runtime secrets, no Key Vault.**
  Both dev env vars (`ConnectionStrings__TodoDb`,
  `APPLICATIONINSIGHTS_CONNECTION_STRING`) are **plain, non-secret** values.
- **`Postgres__UseEntraAuth` does not appear anywhere.** That key was deleted from
  the application in §14; reintroducing it would be dead config implying a
  password fallback the app cannot perform.
- `postgresAdminPassword` is `@secure()`, supplied only at deploy time from
  `PGADMIN_PASSWORD_DEV`, and **must differ from production's** password.
- The `postgresEntraAdmin*` values in the parameters file are an object ID and a
  UPN — identity metadata, **not credentials**.
- The Log Analytics shared key is read inline via `law.listKeys()` inside
  `modules/acaEnvironment.bicep`: never a parameter, never an output, never
  committed.
- The `todoApiUsesAcr` / `todoWebUsesAcr` guard is reused **verbatim**: the ACR
  `registries` entry is only attached when the image actually comes from that
  ACR, otherwise the placeholder bootstrap deploy hangs waiting for an `AcrPull`
  grant that does not exist yet (Phase B grants it *after* Phase A).

---

## 4. Validation result (dry-run only — nothing applied)

### 4a. Bicep compile + lint — **clean, both templates**

```
$ az bicep build --file infra/main.bicep     --stdout > /dev/null   # OK
$ az bicep lint  --file infra/main.bicep                            # clean, no diagnostics
$ az bicep build --file infra/main.dev.bicep --stdout > /dev/null   # OK
$ az bicep lint  --file infra/main.dev.bicep                        # clean, no diagnostics
(Bicep CLI 0.45.15, Azure CLI 2.63.0)
```

### 4b. `az deployment group what-if` — **blocked by the §1 quota**

```
$ az deployment group what-if -g rg-todo-demo -f infra/main.dev.bicep \
    -p infra/main.dev.parameters.json -p containerTargetPort=80 \
    -p postgresAdminPassword=<throwaway>

ERROR: InvalidTemplateDeployment — 'Microsoft.App/managedEnvironments (2024-03-01)'
reported preflight validation errors.
  MaxNumberOfRegionalEnvironmentsInSubExceeded — the subscription cannot have
  more than 1 Container App Environments in East US.
```

Preflight aborts on the first failing resource, so this run validated nothing
downstream of the ACA environment.

### 4c. Diagnostic what-if — **`Succeeded`, everything else is clean**

To establish that the *rest* of the template is correct (and that it does not
touch production), I re-ran `what-if` against a **scratch copy** in which the
`cae-todo-demo-dev` module was swapped for an `existing` reference to the
production environment. The scratch file was deleted immediately after; it was
never committed and the committed template is unchanged and still targets
`cae-todo-demo-dev` in East US.

Result: `"status": "Succeeded"`, `"error": null`.

| changeType | Resource |
|---|---|
| **Create** | `Microsoft.App/containerApps/todo-api-dev` |
| **Create** | `Microsoft.App/containerApps/todo-web-dev` |
| **Create** | `Microsoft.DBforPostgreSQL/flexibleServers/pg-todo-demo-dev-cus01` |
| **Create** | `…/pg-todo-demo-dev-cus01/administrators/ee31140f-…` |
| **Create** | `…/pg-todo-demo-dev-cus01/databases/tododb` |
| **Create** | `…/pg-todo-demo-dev-cus01/firewallRules/AllowAllAzureServicesAndResourcesWithinAzureIps` |
| **Create** | `Microsoft.Insights/components/appi-todo-demo-dev` |
| Ignore | `containerApps/todo-api`, `containerApps/todo-web` |
| Ignore | `managedEnvironments/cae-todo-demo` |
| Ignore | `flexibleServers/pg-todo-demo-cus01` |
| Ignore | `components/appi-todo-demo`, `actiongroups/Application Insights Smart Detection` |
| Ignore | `registries/acrtododemo3bkqjv23abtua`, `workspaces/log-todo-demo` |

**Seven creates, every one of them `-dev` or a child of the dev server; every
production resource `Ignore`.** That is the concrete evidence that applying
`main.dev.bicep` cannot perturb production, and that the shared ACR and workspace
are genuinely referenced rather than re-declared.

### 4d. What was NOT run

`az deployment group create` was **not run**, on either template. Applying the
IaC is Phase A — a deliberate human step, outside this pipeline.

---

## 5. CI/CD

| File | Trigger | Auto-triggering? |
|---|---|---|
| `.github/workflows/ci.yml` | `pull_request` → `[main, develop]`, plus `workflow_dispatch` | **Yes** |
| `.github/workflows/cd.yml` | `push` → `[develop, main]`, plus `workflow_dispatch` | **Yes — the sole auto-deploying system** |
| `azure-pipelines.yml` | `trigger: none` / `pr: none` | **No — manual only, by design** |

**Only GitHub Actions auto-triggers.** `azure-pipelines.yml` is restructured to
the same `DeployDev` → `VerifyDev` → `DeployProd` shape so the two systems cannot
diverge into different architectures, but its triggers stay off. specs §15.9.7
asks for it to trigger on `develop`/`main`; it does not, because two
auto-triggering systems would double every build and — far worse, now that there
is a promotion pipeline — **race two deployments onto the same Container App
revision and the same shared ACR tags**. The ready-to-enable trigger block is
present as a comment, alongside the instruction to disable the GitHub workflows
in the same change if the team ever standardizes on Azure DevOps.

### 5a. `cd.yml` — the promotion job graph

```
deploy-dev  ──▶  verify-dev  ──▶  deploy-prod
(develop+main)   (no creds)      (main only, human-approved)
```

| Job | Environment | Gate | Concurrency |
|---|---|---|---|
| `deploy-dev` | `dev` | none — dev should be fast | group `deploy-dev`, `cancel-in-progress: false` |
| `verify-dev` | **none** | **no Azure login, no credential of any kind** — public HTTPS probes only | none needed (read-only) |
| `deploy-prod` | `production` | `needs: [deploy-dev, verify-dev]` + `if: github.ref == 'refs/heads/main'` + required reviewers | group `deploy-prod`, `cancel-in-progress: false` |

Job-level concurrency groups, **not** workflow-level: a single workflow-wide
group would make a `develop` run and a `main` run block each other end to end.
The two environments now serialize independently.

**`deploy-prod` never rebuilds `todo-api`.** It asserts the artifact exists and
re-points production at it:

```bash
az acr repository show -n "$ACR_NAME" --image "todo-api:${{ github.sha }}" -o none \
  || { echo "::error::todo-api:<sha> not in ACR — dev deploy did not produce this artifact."; exit 1; }
az containerapp update -n todo-api -g "$RESOURCE_GROUP" --image ".../todo-api:${{ github.sha }}"
```

Production therefore runs the **identical image digest** that `verify-dev`
exercised minutes earlier.

`verify-dev`'s four checks, all retried with backoff (~120 s budget, because both
dev apps sit at `minReplicas: 0` and the first request pays a cold start):

| Check | Proves |
|---|---|
| `GET /health` → 200 + `"status":"ok"` | the revision is live |
| `GET /api/todos` → 200 + JSON array | **the important one** — the dev managed identity actually authenticated to the dev Postgres server over Entra, and startup `Migrate()` succeeded |
| `POST` then `DELETE` → 201 then 204 | write path + the dev `pgaadauth` role's DML/DDL rights |
| `GET /` on the dev frontend + follow its JS bundles | the correct `VITE_API_BASE_URL` was baked in |

> Implementation note on the last check: specs §15.9.3 says "the response body
> contains the dev API FQDN". Taken literally against `index.html` that check can
> never pass — Vite inlines `VITE_API_BASE_URL` into the **hashed JS bundle**, not
> into `index.html`. The implementation follows `index.html`'s script references
> and greps those, which is what the spec actually intends.

### 5b. Deviation from my standing CD default — approved, with its caveat stated

My standing default is that `cd.yml` is triggered **only** by a merged PR into
`main` (`pull_request: types: [closed]` + `merged == true`). This cycle uses
specs §15.9.2's `push: branches: [develop, main]` instead — **the user approved
Option A of proposal §6(a) explicitly**.

The default genuinely cannot express this cycle's requirement: `deploy-dev` must
fire on `develop`, where no PR-into-`main` exists, and the whole point is a
**single** workflow whose job graph proves the SHA reaching prod is the SHA dev
validated. Two mitigations make this equivalent or stronger:

1. With branch protection enforced on `main`, a `push` to `main` **only ever
   occurs as the result of a merged PR** — the two triggers converge.
2. The human gate is the `production` environment's required reviewers, which
   pauses `deploy-prod` before any step runs. That is independent of, and
   stronger than, the trigger shape.

**The caveat is real and load-bearing:** mitigation 1 holds *only if branch
protection is actually enabled*. That is exactly why the user elevated branch
protection into this rollout — see §7.

### 5c. `ci.yml` — credential-free, and now guards the promotion path

- Trigger extended to `pull_request: branches: [main, develop]`.
- `iac` job now runs `az bicep build` + `az bicep lint` on **both** templates —
  the mitigation for the two-template drift risk.
- New **`guard-promotion-source`** job fails any PR into `main` whose head branch
  is not `develop` or `hotfix/*`. GitHub branch protection cannot express "only
  merge from `develop`", so this status check is the **only** enforcement of the
  promotion path. It must be a required check or it is decorative.
- **No Azure login, no `what-if`, no push.** `what-if` would require credentials
  and be unsafe on fork PRs; Bicep compile + lint is the credential-free
  validation boundary.
- The `iac` job's **display name is deliberately unchanged** (`IaC (bicep build +
  lint)`) even though it now lints two templates: GitHub matches required status
  checks on the check-run **name**, not the job id, so renaming it would silently
  invalidate any existing required-check configuration and leave PRs stuck on
  *"Expected — waiting for status to be reported."*

### 5d. Artifact promotion — the one honest caveat

| Artifact | Promotable? | Mechanism |
|---|---|---|
| `todo-api` | **Yes — true promotion** | Built once in `deploy-dev` as `todo-api:<sha>`; `deploy-prod` re-points prod at the same tag. Identical digest. |
| `todo-web` | **No — rebuilt per environment** | `VITE_API_BASE_URL` is a **build-time** constant and the dev/prod API FQDNs differ, so one image physically cannot serve both. Built twice from the same source SHA, same Dockerfile and same lockfile, differing only in that one build-arg: `todo-web:<sha>-dev` and `todo-web:<sha>-prod`. |

**Residual risk, plainly:** the frontend image reaching production is *not* the
exact image validated in dev. Low risk, not zero (a non-reproducible dependency
resolution or a base-image tag moving between the two builds could diverge them).
The correct fix — making the frontend read its API base URL **at runtime** — is
an application change, out of this cycle's scope, and would also remove the
backend-before-frontend ordering constraint that has shaped this project since
day one.

---

## 6. One-time identity setup required (human runs these — I did not)

The CD service principal (`gh-todo-demo-cicd`) and the `production` federated
credential already exist from the production bootstrap. **Only one new federated
credential is needed**, and it is the single most likely first-run failure.

```bash
REPO="matvi/todo-claude-multiagent"
APP_ID=$(az ad app list --display-name gh-todo-demo-cicd --query "[0].appId" -o tsv)

# GitHub's newer repos use the IMMUTABLE subject format, which embeds numeric IDs:
OWNER_ID=$(gh api "users/$(echo "$REPO" | cut -d/ -f1)" --jq .id)
REPO_ID=$(gh api "repos/$REPO" --jq .id)
GH_SUBJECT_REPO="$(echo "$REPO" | cut -d/ -f1)@${OWNER_ID}/$(echo "$REPO" | cut -d/ -f2)@${REPO_ID}"

# NEW — required by deploy-dev's `environment: dev`
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-env-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':environment:dev",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

> **Why this is the likely first failure:** declaring `environment: dev` on a job
> changes GitHub's OIDC **subject claim** from the branch form to
> `…:environment:dev`. Azure matches the subject as an **exact string**. Without
> this credential, `deploy-dev` dies at `azure/login` with an opaque
> `AADSTS700213: No matching federated identity record found`. Fastest fix if it
> happens: the failure message contains the exact subject GitHub presented — paste
> it into the credential above.

For reference, the original app/SP/RBAC creation (already done, not re-run):

```bash
APP_ID=$(az ad app create --display-name "gh-todo-demo-cicd" --query appId -o tsv)
az ad sp create --id "$APP_ID"
az role assignment create --assignee "$APP_ID" --role Contributor \
  --scope "/subscriptions/$SUB_ID/resourceGroups/rg-todo-demo"
```

**No new RBAC is needed for the CD service principal** — it already holds
`Contributor` on `rg-todo-demo`, and every dev resource lives there. The only new
grants are the two `Contributor` cannot write (Phases B and C in
`infra/README.md`): `AcrPull` ×2 for the dev app identities, and `Monitoring
Metrics Publisher` ×1 for `todo-api-dev` on the **dev** App Insights component
only.

### GitHub repository **variables** (not secrets — no credential material)

| Variable | Value | Status |
|---|---|---|
| `AZURE_CLIENT_ID` | the `gh-todo-demo-cicd` appId | already set, unchanged |
| `AZURE_TENANT_ID` | `6ce2ff72-209a-447d-bf53-9579c52c03f5` | already set, unchanged |
| `AZURE_SUBSCRIPTION_ID` | `9ba6633f-058c-4269-8b0f-086ab331ef26` | already set, unchanged |

These are identifiers, not credentials. **With OIDC, no credential material is
stored in GitHub at all.**

### GitHub **secret** and **environments**

| Item | Status |
|---|---|
| Secret `PGADMIN_PASSWORD_DEV` | **NEW** — `gh secret set PGADMIN_PASSWORD_DEV --repo "$REPO"`. **Must differ from production's `PGADMIN_PASSWORD`.** Deploy-time only; never an app runtime secret, never in any `.md`/`.json`/`.bicep`. |
| Environment `dev` | **NEW** — `gh api -X PUT "repos/$REPO/environments/dev"`, no protection rules. |
| Environment `production` | Exists. **Required reviewers must be configured** — that rule *is* the promotion gate. |

---

## 7. Branch protection status on `main` — UNVERIFIED, and now a required rollout step

**Current state: still unknown, and by every available indication still not
configured.** I could not check it:

```
$ command -v gh
gh: NOT INSTALLED
```

`gh` is not installed in this environment, so the read-only
`gh api repos/matvi/todo-claude-multiagent/branches/main/protection` check could
not be run. A git remote now exists (`git@github.com:matvi/todo-claude-multiagent.git`),
so that earlier blocker is gone — the only remaining obstacle is tooling I do not
have, and repo configuration is outside my remit regardless. **I am stating this
plainly rather than assuming someone set it up**; this file has now recorded it
unverified three cycles running.

**This cycle that changes.** Per the user's explicit direction on 2026-08-07,
branch protection is **part of this rollout, not a deferred follow-up**. It is
sequenced in `infra/README.md` as **Phase E1 — the first step of the first
phase**, in the same phase as the OIDC federated credential, the
`PGADMIN_PASSWORD_DEV` secret and the `dev`/`production` environments, with the
exact command inline:

```bash
gh api -X PUT "repos/matvi/todo-claude-multiagent/branches/main/protection" --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": [
      "Backend (.NET 10 build + test)",
      "Frontend (Vite build + vitest)",
      "Docker build (both images, no push)",
      "IaC (bicep build + lint)",
      "Guard — PRs into main must come from develop or hotfix/*"
    ]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": {
    "required_approving_review_count": 1,
    "dismiss_stale_reviews": true,
    "require_code_owner_reviews": false
  },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_linear_history": true,
  "required_conversation_resolution": true
}
JSON
```

Verify:

```bash
gh api "repos/matvi/todo-claude-multiagent/branches/main/protection" \
  --jq '{admins: .enforce_admins.enabled,
         reviews: .required_pull_request_reviews.required_approving_review_count,
         checks: .required_status_checks.contexts,
         force_push: .allow_force_pushes.enabled}'
```

`develop` gets a deliberately lighter rule (same status checks minus
`guard-promotion-source`, **no** required approving review, no admin
enforcement) so integration stays fast — the full payload is in
`infra/README.md` Phase E2.

**Two things that will bite if not read:**

- **The `contexts` strings must match the check-run names EXACTLY.** GitHub
  matches on the job's `name:` field, not the job id — it is
  `"IaC (bicep build + lint)"`, not `"iac"`. A typo does not error; the check
  simply never reports and every PR sits forever on *"Expected — waiting for
  status to be reported."* After the first CI run:
  `gh api "repos/$REPO/commits/<sha>/check-runs" --jq '.check_runs[].name'`.
- **Branch protection and environment required-reviewers are both unavailable on
  private repositories on the GitHub Free plan.** If this repo is private on
  Free, both E1 and the `production` reviewer rule will fail and the fallback is
  Plan B (specs §15.9.5): split `cd.yml` into `cd-dev.yml` + a
  `workflow_dispatch`-only `cd-prod.yml` taking a dev-validated `image_tag`. Check
  first with `gh repo view "$REPO" --json visibility,isPrivate` and record the
  answer in `changes.md`. **I implemented Plan A**, since Plan A is what the
  approved proposal specifies.

### Why this matters more this cycle than it ever has

Without branch protection, **this pipeline's central safety property does not
exist**. `cd.yml` triggers on `push: [develop, main]`. That is only equivalent to
"on merged PR" if a direct push to `main` is impossible. If it is possible, a
single `git push origin main` walks straight past the review, past
`guard-promotion-source`, and straight into `deploy-prod` — where the only
remaining barrier is the `production` environment approval. The
human-reviews-the-PR gate that this entire pipeline is built around would
otherwise be **a convention the agents happen to follow, not something GitHub
enforces**.

---

## 8. To actually deploy the infra itself (exact commands — a human runs these)

⛔ **Blocked by the §1 Container Apps environment quota until that is resolved.**
Full sequencing, including which phase runs first and why, is in
`infra/README.md`.

```bash
RG=rg-todo-demo
read -rs PGPWD_DEV; export PGPWD_DEV   # PGADMIN_PASSWORD_DEV — NOT production's password

# Dry run first (changes nothing):
az deployment group what-if -g "$RG" \
  --template-file infra/main.dev.bicep \
  --parameters infra/main.dev.parameters.json \
  --parameters containerTargetPort=80 \
  --parameters postgresAdminPassword="$PGPWD_DEV"

# Phase A — the real deploy:
az deployment group create -g "$RG" -n main-dev \
  --template-file infra/main.dev.bicep \
  --parameters infra/main.dev.parameters.json \
  --parameters containerTargetPort=80 \
  --parameters postgresAdminPassword="$PGPWD_DEV"
```

**`containerTargetPort=80` is for the bootstrap deploy only** — both images
default to the public placeholder, which listens on 80, while the real app images
listen on 8080. Get it wrong and the revision never passes its health probe and
provisioning times out. After CD has replaced both images, deploy without the
override (or simply let CD keep managing images — CD does not apply Bicep).

Then capture the outputs and run Phases B (AcrPull ×2), C (Monitoring Metrics
Publisher), D (the in-database `pgaadauth_create_principal('todo-api-dev')` plus
schema grants) and F — all in `infra/README.md`.

**Production (`infra/main.bicep`) is not re-deployed by this cycle.** It targets a
live, deliberately drifted stack; do not re-apply it casually.

---

## 9. Estimated cost — ESTIMATE ONLY, region/usage-dependent

Retail pay-as-you-go list prices retrieved 2026-08-06 from the Azure Retail
Prices API for the exact regions in use (`centralus` for Postgres, `eastus` for
everything else), USD, tax excluded. **Not a quote.** Actual spend varies with
usage and list prices change.

| Item | Basis | Monthly delta |
|---|---|---|
| Dev Postgres compute — `Standard_B1ms`, **always on** | $0.01921/hr × 730 | **$14.02** |
| Dev Postgres storage — 32 GiB (the minimum) | 32 × $0.13/GB | **$4.16** |
| Dev Postgres backup — 7-day PITR, LRS | ≤ provisioned size is included | **$0** |
| Dev Container Apps compute — both apps min 0/max 1, 0.25 vCPU / 0.5 GiB | ~40 active hr/mo; the subscription free grant is already fully consumed by prod's `minReplicas: 1` frontend, so dev bills from the first second | **~$1–2** (range $0–5; **$0 fully idle**) |
| Dev ACA Environment — Consumption | no standing charge (verified: no Consumption management meter exists; the Dedicated one is $0.10/hr) | **$0** |
| Dev App Insights — workspace-based | no standing fee | **$0** |
| Extra Log Analytics ingestion | ~0.2–1 GB/mo × $2.30/GB, inside the shared 1 GB/day cap | **$0–2.30** |
| Shared ACR / RG / identities / role assignments / OIDC | free or already paid | **$0** |
| **TOTAL DELTA** | | **≈ +$19–25 / month** |

> ### $18.18/month of that is a hard, always-on floor.
> The dev Postgres server bills 24×7 **whether or not anyone uses the dev
> environment** — Flexible Server cannot scale to zero. Dev's `minReplicas: 0`
> makes dev *compute* idle at $0; it does nothing for the database. So **73–95%
> of this delta is unavoidable recurring spend** from the moment Phase A runs,
> and reversing it later means **deleting a database server**, not flipping a
> setting.

**New projected total: ~$44–62/month**, combined idle floor ~$41/month (both
Postgres servers + ACR + prod's always-on frontend replica). Prior baseline was
~$25–37/month.

This refines specs §15.13's +$18–22 estimate upward slightly: Postgres in Central
US actually bills $0.01921/hr and $0.13/GB (the spec assumed ~$0.017 and ~$0.115),
partly offset by Log Analytics being cheaper than assumed ($2.30/GB, not $2.76).

### Cost lever available at any time, adopted by nobody yet

`az postgres flexible-server stop -g rg-todo-demo -n pg-todo-demo-dev-cus01`
suspends compute billing (~$14/mo; storage still bills). **Requires zero IaC
change** — Option A's IaC and Option B's IaC are the same file, so this can be
adopted or abandoned any day. It is **not** automated this cycle, deliberately:
Azure auto-restarts a stopped server after 7 days, and a stopped server makes
`verify-dev` fail, which **silently blocks every promotion to prod**. That failure
mode costs more than the ~$12 saved.

---

## 10. What this run did not do

- **Provisioned nothing.** No `az deployment group create`, on either template.
- **Created no identity, federated credential, role assignment, GitHub
  secret/variable/environment, branch-protection rule, or in-database principal.**
  Those commands are authored for a human with the necessary AAD/GitHub
  privileges to run.
- **Did not commit or push.** The reviewer goes next.
- **Did not touch production**: `infra/main.bicep`, `infra/main.parameters.json`,
  every `infra/modules/*.bicep`, and every live production resource are unchanged.
  The only production-facing change in this cycle is *when* and *how*
  `todo-api`/`todo-web` receive a new image.
