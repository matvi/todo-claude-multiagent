# Infrastructure proposal — 2026-08-06

Cycle: **Dev environment + dev→prod promotion pipeline (specs.md §15)**, branch
`pipeline/dev-environment`. **PROPOSE mode — nothing is provisioned, and no file
under `infra/`, `.github/workflows/` or `azure-pipelines.yml` has been touched.**
This document exists to get one number signed off before any IaC is written.

Inputs read in full: `.pipeline/specs.md` §15 (lines 2090–3147),
`.pipeline/architecture-memory.md` (2026-08-06 entry),
`.pipeline/changes.md` (2026-08-06 entry — engineer confirmed **zero app-code
changes needed**), `.pipeline/tests.md` (tester **PASS**, 118/118 green baseline),
plus the deployed IaC: `infra/main.bicep`, `infra/main.parameters.json`,
`infra/modules/*.bicep`, `.github/workflows/cd.yml`.

> **The architecture is already decided.** §15 and the architecture-memory entry
> settle *what* gets built (own ACA environment, own Postgres server, own App
> Insights; shared Log Analytics, shared ACR). This proposal does **not**
> re-litigate any of that. It prices it, checks whether any SKU lever actually
> exists, and lists what I would author in APPLY mode.

---

## 0. What is new vs. the IaC that exists today

`infra/main.bicep` provisions the production stack only and, per §15.5, is **not
modified this cycle**. Everything below is additive.

| Resource | State today | This cycle |
|---|---|---|
| `cae-todo-demo`, `todo-api`, `todo-web`, `pg-todo-demo-cus01`, `appi-todo-demo`, `log-todo-demo`, ACR | deployed, production | **unchanged, byte-for-byte** |
| **`cae-todo-demo-dev`** — 2nd `Microsoft.App/managedEnvironments`, East US | does not exist | **NEW** |
| **`todo-api-dev`, `todo-web-dev`** — Container Apps, min 0 / max 1 | do not exist | **NEW** |
| **`pg-todo-demo-dev-cus01`** — 2nd Postgres Flexible Server, Central US | does not exist | **NEW — the entire cost delta** |
| **`appi-todo-demo-dev`** — 2nd App Insights component | does not exist | **NEW** |
| `log-todo-demo` workspace | deployed | **SHARED** — referenced as `existing`, not duplicated |
| `acrtododemo3bkqjv23abtua` (Basic) | deployed | **SHARED** — referenced as `existing`, not duplicated |
| `rg-todo-demo` | deployed | **SHARED** — user directive, no new RG |

Four new resources. **Only one of them costs money.**

---

## 1. Cost — the primary thing needing sign-off

### 1a. Pricing method

Prices retrieved **2026-08-06** from the Azure Retail Prices API
(`prices.azure.com/api/retail/prices`), filtered to the **exact regions already
in use** — `centralus` for Postgres (forced by the East US Postgres offer
restriction on this subscription, §15.2) and `eastus` for everything else.
USD, pay-as-you-go list price, `type: Consumption`, tax excluded. These are
**estimates**: actual spend varies with usage, and list prices change.

Verified meter values (not assumed):

| Meter | Region | Verified price |
|---|---|---|
| PostgreSQL Flexible Server Burstable `B1MS` vCore | centralus | **$0.01921 / hr** |
| PostgreSQL Flexible Server storage (Data Stored) | centralus | **$0.13 / GB-month** |
| PostgreSQL Flexible Server backup storage LRS | centralus | $0.108 / GB-month (free up to 100% of provisioned storage) |
| Container Apps `Standard` (Consumption) vCPU active | eastus | $0.000024 / vCPU-s |
| Container Apps `Standard` vCPU **idle** | eastus | $0.000003 / vCPU-s |
| Container Apps `Standard` memory | eastus | $0.000003 / GiB-s |
| Container Apps **Dedicated** plan management | eastus | $0.10 / hr — *no equivalent meter exists for Consumption, confirming the $0 standing charge for `cae-todo-demo-dev`* |
| Log Analytics `Analytics Logs` data ingestion | eastus | **$2.30 / GB** |
| Container Registry Basic registry unit | eastus | $0.1666 / day (~$5.07/mo, includes 10 GiB) — **shared, $0 delta** |

### 1b. Refined delta

| Item | Basis | Monthly delta |
|---|---|---|
| **Dev Postgres compute** — `Standard_B1ms`, always on (Postgres cannot scale to zero) | $0.01921 × 730 hr | **$14.02** |
| **Dev Postgres storage** — 32 GiB (the minimum provisionable) | 32 × $0.13 | **$4.16** |
| **Dev Postgres backup** — 7-day PITR, LRS | ≤ provisioned size is included; dev DB is tiny | **$0** |
| **Dev Container Apps compute** — both apps min 0 / max 1, 0.25 vCPU / 0.5 GiB | ~40 active hr/mo across both apps. All-active worst case ≈ $2.16; mostly-idle realistic ≈ $0.70. Subscription free grant (180k vCPU-s / 360k GiB-s) is **already fully consumed by prod's `minReplicas: 1` `todo-web`**, so dev bills from second one | **~$1–2** (range $0–5; **$0 fully idle**) |
| **Dev ACA Environment** `cae-todo-demo-dev` | Consumption plan has no standing charge (verified — see meter table) | **$0** |
| **Dev App Insights** `appi-todo-demo-dev` | Workspace-based components have no standing fee | **$0** |
| **Extra Log Analytics ingestion** (dev console logs + dev traces) | ~0.2–1 GB/mo × $2.30/GB, inside the shared 1 GB/day cap | **$0–2.30** |
| Shared ACR / RG / identities / role assignments / OIDC | free or already paid | **$0** |
| **TOTAL DELTA** | | **≈ +$19–25 / month** |

**Refinement vs. the spec's estimate.** §15.13 estimated **+$18–22**. The real
figure is **slightly higher, ~+$19–25**, for two reasons the spec got wrong in
opposite directions:

- Postgres is **more** expensive than assumed. The spec used ~$0.017/hr and
  ~$0.115/GiB; Central US actually bills **$0.01921/hr** and **$0.13/GB**. Dev
  Postgres is **$18.18/mo, not the $16–17 the spec quoted.**
- Log Analytics is **cheaper** than assumed — **$2.30/GB** in East US, not the
  $2.76 the spec used. This partly offsets the above.

**The number that actually matters is the floor, not the range.**

> ### $18.18 / month is a hard, always-on floor.
> The dev Postgres server bills 24×7 **whether or not anyone ever uses the dev
> environment**. Postgres Flexible Server cannot scale to zero. Dev's
> `minReplicas: 0` design makes dev *compute* idle at $0 — it does nothing for
> the database. So **73–95% of this delta is unavoidable recurring spend** from
> the moment Phase A runs.
>
> This is a **recurring** cost, and reversing it later means **deleting a
> database server**, not flipping a setting. That is why it needs conscious
> sign-off now rather than after provisioning.

**New projected total:** ~$44–62/month (prior baseline ~$25–37 from
`.pipeline/infra.md` + this delta), with a combined idle floor of ~$41/month
(both Postgres servers + ACR + prod's always-on frontend replica).

---

## 2. The only genuine SKU decision: dev PostgreSQL

I checked whether dev could run a **smaller** tier than prod, since it is
dev-only and not customer-facing. **It cannot — dev is already at the absolute
floor of the service.** Verified against the live price list, the complete
Burstable ladder for PostgreSQL Flexible Server in Central US is
`B1MS → B2S → B2ms → B4ms → B8ms → B12ms → B16ms → B20ms`. **There is nothing
below `B1ms`**, 32 GiB is the minimum provisionable storage, 7 days is the
minimum backup retention, and HA/geo-redundancy are already `Disabled` in
`infra/modules/postgres.bicep`. There is no cheaper configuration of a dedicated
Postgres server to propose.

So the decision is not "which tier" — it is **"a dedicated server, or not."**

| Option | Est. monthly cost | Suited for | Limits |
|---|---|---|---|
| **A. Dedicated dev server, always on** — `pg-todo-demo-dev-cus01`, B1ms / 32 GiB, identical to prod *(the spec's design)* | **~$18.18** fixed, +$0 usage | Structural isolation by construction; full prod fidelity; server-level changes (PG version, `authConfig`, params, PITR drills) rehearsable in dev; `verify-dev` always able to run, so promotions never block on infra state | 1 vCPU / 2 GiB shared burst-credit pool; no HA; dev data not recoverable by policy; the ~$18/mo bills even when dev is untouched |
| **B. Dedicated dev server, stopped between work bursts** — same resource, `az postgres flexible-server stop/start` | **~$4.16–7** (storage always bills; compute only while running) | Teams that develop in bursts and are willing to run one command before/after | **Azure auto-restarts a stopped server after 7 days**, so savings are not automatic. **A stopped server makes `verify-dev` fail, which silently blocks every promotion to prod** — the failure mode costs more than the ~$12 saved. Spec Q2 rejects this for this cycle |
| **C. No dev server — second database `tododb_dev` on the prod server** *(spec §15.4.5's rejected fallback)* | **$0** | Only if the cost is genuinely unacceptable | `pgaadauth` principals are **server-global** and `PUBLIC` holds `CONNECT` on every DB by default → the dev identity can reach prod `tododb` unless a human remembers `REVOKE`. Startup `Migrate()` means a wrong `Database=` runs **DDL against prod data** instead of failing closed. One shared B1ms burst pool, so dev load degrades prod. Server-level changes unrehearsable. Requires mandatory hardening SQL **and** a superseding `architecture-memory.md` entry |

**Recommendation: Option A**, as specified.

Not because it is the default, but because the specific thing this cycle exists
to fix is exactly what Option C cannot deliver. The §14 incident — an Entra auth
change that had to be validated in production — is the reason for this work, and
the isolation it needs is *identity* isolation. Option C's dev identity remains a
principal on the production server, with `CONNECT` on prod's database by
PostgreSQL default; isolation would rest on a human remembering a `REVOKE` that
has no IaC representation and no drift detection. That is precisely the
"policy someone has to configure correctly" failure this design rejects.

**Option B is worth knowing about but not adopting now.** It is a pure
operational lever requiring **zero IaC change** — it can be adopted any day after
provisioning, and abandoned just as easily. Option A's IaC and Option B's IaC are
the same file. Adopting B is not a decision that has to be made today.

If the cost is rejected outright, Option C is the fallback, but it is a genuine
architecture reversal (superseding memory entry + hardening SQL) and should come
back through the architect, not be quietly adopted at this stage.

---

## 3. Resources with no genuine SKU choice (stated, not padded)

I am not manufacturing option tables for these. In each case exactly one answer
is defensible, and the reasoning is one line:

- **`cae-todo-demo-dev` (Container Apps Environment)** — **Consumption-only**
  (`workloadProfiles: null`), matching prod's verified shape. The only
  alternative, a workload-profiles environment, adds a **$0.10/hr ($73/mo) plan
  management charge** the moment a Dedicated profile is attached, and exists to
  enable VNet/dedicated-compute scenarios that §15.14 explicitly puts out of
  scope. Consumption is **$0 standing charge**, which is *why* §15.4.1 could give
  dev its own boundary for free. **No decision needed.**
- **`appi-todo-demo-dev` (Application Insights)** — workspace-based components
  have **no SKU and no standing fee**; all billing flows through the backing
  workspace's existing `PerGB2018` tier. The only "choice" is which workspace,
  and §15.4.2 mandates the shared `log-todo-demo`. Reuses
  `infra/modules/appinsights.bicep` unchanged (`DisableLocalAuth: true`).
  **No decision needed.**
- **`todo-api-dev` / `todo-web-dev` (Container Apps)** — 0.25 vCPU / 0.5 GiB is
  the ACA minimum allocation and matches prod. Replica counts are already fixed
  by §15.10 (**min 0 / max 1** for both, deliberately cheaper than prod).
  **No decision needed.**
- **Log Analytics and ACR** — shared, referenced as `existing`. No new resource,
  no SKU, **$0 delta**. One inherited risk carries forward, already accepted in
  §15.4.2: the workspace's **1 GB/day ingestion cap is now shared with dev**, so
  heavy dev ingestion can silently stop **production** telemetry for the rest of
  that UTC day. Detection KQL and the cap-raise lever are in §15.4.2; I will
  restate both in `infra.md` in APPLY mode.

---

## 4. What I would create/modify in APPLY mode

### New files

| Path | Provisions / contains |
|---|---|
| `infra/main.dev.bicep` | `targetScope = 'resourceGroup'`. ACR + `log-todo-demo` as `existing` (creates nothing). Then four module calls, all **reusing `infra/modules/*` unchanged**: `acaEnvironment.bicep` → `cae-todo-demo-dev` (eastus); `appinsights.bicep` → `appi-todo-demo-dev`; `postgres.bicep` → `pg-todo-demo-dev-cus01` (**centralus**, db `tododb`); `containerApp.bicep` ×2 → `todo-web-dev` **first** (its FQDN feeds CORS), then `todo-api-dev`. Passwordless plain env vars only — no `Password=`, no `Passfile=`, no Container Apps secret, no `Postgres__UseEntraAuth`. Reuses the `todoApiUsesAcr`/`todoWebUsesAcr` guard verbatim. Outputs per §15.5 (both `principalId`s, `appInsightsDevId`, both FQDNs, Postgres name/FQDN). |
| `infra/main.dev.parameters.json` | **Non-secret parameters only** — `location: eastus`, `postgresLocation: centralus`, server/app names, `postgresAdminUser: todoadmin`, the four non-secret `postgresEntraAdmin*` values (same human admin as prod, per §15.15 assumption 4), replica counts. **`postgresAdminPassword` is never written here** — it is `@secure()` and supplied at deploy time. |

**Why a separate template rather than parameterizing `main.bicep`** (settled in
§15.5 and architecture-memory; I concur and will not deviate): production's live
configuration has **permanently drifted** from `main.bicep` by design, because
§14.2 established that CD does not apply Bicep. Turning `main.bicep` into a
two-environment template would make every dev bootstrap a potential *production*
deployment that silently reverts that drift. The accepted cost is duplication
between two top-level templates; it is mitigated by both calling the **same
`infra/modules/*`** — where all real resource shape lives — and by CI linting
both. The tradeoff is explicitly the right way round: a little duplication is far
cheaper than an accidental prod deploy.

### Modified files

| Path | Change |
|---|---|
| `.github/workflows/cd.yml` | **Replaced** with the §15.9.3 three-job graph: `deploy-dev` (`environment: dev`) → `verify-dev` (**no Azure login, no credential of any kind** — public HTTPS probes only) → `deploy-prod` (`needs: [deploy-dev, verify-dev]`, `if: github.ref == 'refs/heads/main'`, `environment: production`). Workflow-level `concurrency:` removed in favour of job-level groups `deploy-dev` / `deploy-prod`, both `cancel-in-progress: false`. `deploy-prod` **asserts `todo-api:<sha>` exists in ACR and never rebuilds it**. Preserves the existing `docker build`/`docker push` workaround and its comment (ACR Tasks is disabled on this free-credit subscription — `az acr build` would fail). |
| `.github/workflows/ci.yml` | Trigger extended to `pull_request: branches: [main, develop]`; `iac` job additionally runs `az bicep build`/`az bicep lint` on `main.dev.bicep`; new **`guard-promotion-source`** job failing PRs into `main` whose head is not `develop` or `hotfix/*`. Stays **credential-free** — no Azure login, no `what-if` (which would require credentials and be unsafe on fork PRs). |
| `azure-pipelines.yml` | Restructured to `DeployDev` → `VerifyDev` → `DeployProd` for parity — **but see §6, it stays manual-only**. |
| `infra/README.md` | Phases A–F (§15.11) verbatim, clearly labelled **human-run**, including the OIDC subject table and the ACR housekeeping command. |
| `.pipeline/infra.md` | Overwritten per my standing spec, recording the approved SKU choice, the validation output, branch-protection status, and the human-run deploy commands. |

### Validation I would run in APPLY mode (dry-run only)

`az bicep build` + `az bicep lint` on both templates, and
`az deployment group what-if -g rg-todo-demo -f infra/main.dev.bicep` with a
throwaway `--parameters postgresAdminPassword=...` value. `what-if` is read-only
and does not provision. I will iterate until it is clean before reporting
success. **I will not run `az deployment group create`.**

### Human-run configuration this introduces (I author the commands, never execute them)

| Item | Note |
|---|---|
| GitHub **secret** `PGADMIN_PASSWORD_DEV` | New. **Must be a different value from prod's `PGADMIN_PASSWORD`** — a dev credential leak must not reach production. Deploy-time only; never an app runtime secret, never in any `.md`/`.json`/`.bicep`. |
| GitHub **Environment** `dev` | New, no protection rules. |
| GitHub **Environment** `production` | Exists; **required reviewers must be configured** — this *is* the promotion gate. |
| **OIDC federated credential** `repo:matvi/todo-claude-multiagent:environment:dev` | New, and the **most likely first-run failure**. Declaring `environment: dev` changes the OIDC subject from the branch form to the environment form; without this credential `deploy-dev` dies at `azure/login` with an opaque `AADSTS700213`. |
| Repo **variables** `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | Already exist, unchanged. Not secret — no credential material is stored in GitHub at all. |
| `develop` branch + branch protection on `main` and `develop` | Human/`gh`. **`gh` is still not installed in my environment** (verified this run: `gh: command not found`), so I cannot check protection status myself — `infra.md` will say so plainly rather than assume it. |
| **No new RBAC for the CD service principal** | It already holds `Contributor` on `rg-todo-demo`. Only `AcrPull` ×2 and `Monitoring Metrics Publisher` ×1 for the new dev identities are new manual grants (Phases B–C), because `Contributor` cannot write role assignments. |

---

## 5. Container Apps target check

My standing pipeline requirement is that CD deploys to **Azure Container Apps**.
specs.md §15 targets Azure Container Apps (`Microsoft.App/managedEnvironments` +
`Microsoft.App/containerApps`) for both environments. **No mismatch — nothing to
escalate as a re-architecture issue.**

---

## 6. Two deviations from my standing defaults, flagged rather than applied silently

**(a) CD trigger shape.** My standing default is that `cd.yml` is triggered
*only* by a merged PR into `main` (`pull_request: types: [closed]` +
`merged == true`). specs.md §15.9.2 instead specifies `push: branches:
[develop, main]`, which is also what the currently deployed `cd.yml` does.

I intend to **follow specs.md**, because the default cannot express this cycle's
requirement: `deploy-dev` must fire on `develop`, where no PR-into-`main` exists,
and the whole point of §15.9.1 is a **single** workflow whose job graph proves
the SHA reaching prod is the SHA dev validated. Two honest mitigations make this
equivalent or stronger:

- With branch protection enforced on `main`, a `push` to `main` **only ever
  occurs as the result of a merged PR** — the two triggers converge.
- The human gate is the `production` GitHub Environment's required reviewers,
  which pauses `deploy-prod` before any step runs. That gate is independent of,
  and stronger than, the trigger shape.

**The caveat is real:** the first mitigation holds *only if branch protection is
actually enabled*, and §15.12 records that it has never been verified. I will
state this plainly in `infra.md` rather than assume it. Say so now if you would
rather I keep the merged-PR trigger and split the workflow instead.

**(b) `azure-pipelines.yml` triggers.** §15.9.7 says to trigger it on `develop`
and `main`. My standing rule — and §11.10's "not actively wired" status — say
only one system may auto-trigger, or two systems race to deploy the same
Container App revision. I will restructure it to the three-stage shape for parity
but keep **`trigger: none` / `pr: none`**, with the §15.9.7 branch list present as
a commented block ready to flip on if the team ever standardizes on Azure DevOps.
GitHub Actions remains the sole auto-triggering system.

---

## 7. What this proposal does not do

- **Nothing is provisioned.** No Azure resource is created, modified or deleted
  by this document.
- **In APPLY mode I still will not provision anything.** I author IaC and
  pipeline YAML and validate it with dry-run commands only (`az bicep
  build`/`lint`, `az deployment group what-if`). Running
  `az deployment group create` is Phase A — a deliberate human step, outside this
  pipeline.
- **I will never create identities, federated credentials, role assignments,
  in-database `pgaadauth` principals, GitHub secrets/variables/environments, or
  branch protection rules.** I write the exact commands; a human with the
  necessary AAD/GitHub privileges runs them (§11.6/§11.8, §15.11).
- No production resource, env var or replica count is touched by this cycle.

---

## Sign-off requested

1. **Approve ~+$19–25/month recurring**, of which **~$18.18 is an unavoidable
   always-on floor**, to run **Option A** — a dedicated `pg-todo-demo-dev-cus01`
   (B1ms / 32 GiB / Central US, already the cheapest possible configuration).
2. Confirm you are content with the two §6 deviations (CD trigger from specs.md;
   `azure-pipelines.yml` staying manual-only).

On approval I will proceed to APPLY mode: author `infra/main.dev.bicep`,
`infra/main.dev.parameters.json`, the three-job `cd.yml`, the extended `ci.yml`,
the parity `azure-pipelines.yml`, the Phase A–F human runbook in
`infra/README.md`, dry-run-validate all of it, and overwrite `.pipeline/infra.md`
recording the approved choice and who approved it.
