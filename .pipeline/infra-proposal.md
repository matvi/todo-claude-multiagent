# Infrastructure proposal — 2026-07-24

Mode: **PROPOSE** (no IaC, workflow YAML, or `azure-pipelines.yml` written this
run — see §11.8). Branch: `pipeline/todo-app-azure-cicd`.

This is a **greenfield** infra run: there is no existing `infra/`,
`.github/workflows/`, or `azure-pipelines.yml` in the tree yet (confirmed), so
**every resource below is NEW**. Nothing is "changed" or "unchanged" relative to
prior IaC — there is no prior IaC to diff against.

The app itself (backend .NET 10 API, frontend Vite/React SPA, EF Core migrations,
Dockerfiles, tests) is already built and green (29/29 backend, 37/37 frontend per
`tests.md`). What is missing and being proposed here is only the **delivery layer**:
the Azure resource SKUs the Bicep will provision, and the identity strategy for the
pipelines that will deploy them.

The spec (§11.9) and `architecture-memory.md` already **pre-commit** the SKU choices
for a cost-conscious demo. This proposal therefore does two things:
1. Restates each concrete resource + SKU + one-line rationale, tied to the "simple
   demo app, cost-conscious" guidance, with a monthly cost estimate.
2. Surfaces the handful of places where a **second reasonable choice exists**, as
   explicit approve/choose options for the human — because that is the point of
   PROPOSE mode. Where I recommend the spec's default, I say so; where I think a
   spec detail is worth a second look, it is in **Open questions** (§5), not baked
   silently into the table.

---

## 1. Proposed Azure resources (all NEW)

Region baseline for every estimate below: **East US** (stated explicitly per the
task; ACA + Postgres Flexible Server are both broadly available there, matching
spec §5.3 default). Prices are **rough estimates**, USD, and Azure pricing changes
over time and varies by region — treat every number as indicative, not a quote.

| # | Resource | Type | Proposed SKU / tier | One-line rationale (demo, cost-conscious) |
|---|----------|------|---------------------|-------------------------------------------|
| 1 | `rg-todo-demo` | Resource group | n/a (free) | One RG per app/env per memory conventions; container for everything, no cost. |
| 2 | `acrtododemo<suffix>` | Azure Container Registry | **Basic** | Cheapest ACR tier; one small repo of two images, no geo-replication or Premium features needed. |
| 3 | `log-todo-demo` | Log Analytics workspace | **Pay-as-you-go, 30-day retention** | Backs the ACA environment's logs; tiny ingestion for a demo, retention kept at the spec-mandated 30 days. |
| 4 | `cae-todo-demo` | Container Apps managed environment | **Consumption** workload profile | Pay-per-use, scale-to-zero capable; the environment resource itself has no standing charge — you pay only for the apps' usage. |
| 5 | `todo-api` | Azure Container App (backend) | **Consumption**, 0.25 vCPU / 0.5 GiB, min **0** / max **3** | Scale-to-zero backend → ~$0 when idle; cold start is acceptable for a demo (§5.6). |
| 6 | `todo-web` | Azure Container App (frontend) | **Consumption**, 0.25 vCPU / 0.5 GiB, min **1** / max **2** | Min 1 so the SPA loads instantly for demos; still the smallest replica size. |
| 7 | `pg-todo-demo<suffix>` | Azure DB for PostgreSQL Flexible Server | **Burstable `Standard_B1ms`**, 32 GiB storage, PG 16, no HA, no read replica, LRS backup | Cheapest usable managed Postgres tier; single-DB demo has no need for General Purpose IOPS or HA. This is the one always-on cost center. |
| 8 | `tododb` | PostgreSQL database (logical) | n/a (inside #7) | Single logical DB per spec §2/§5.3; no extra charge. |

Identity / access notes baked into the plan (not separately billable):
- Each Container App gets a **system-assigned managed identity** with **`AcrPull`**
  on the registry (spec §11.3 preferred path — supersedes the admin-user fallback
  the app-cycle `changes.md` used in the manual runbook).
- Postgres uses **public access + the `0.0.0.0` "allow Azure services" firewall rule
  + required TLS** (spec §5.4). No VNet, no private endpoint, no NAT gateway — each
  of those would add cost and is explicitly out of scope (§8, §11.9).
- Secrets (Postgres connection string) live as **Container Apps secrets**, not Key
  Vault (spec §5.5) — one fewer resource, no standing cost.

---

## 2. Estimated monthly cost (East US, ROUGH ESTIMATE)

> These are estimates for planning only. Actual cost depends on region, real
> traffic, ingestion volume, and current Azure list prices, which change over time.
> Figures assume a genuinely idle-to-light demo (a handful of requests, minimal log
> ingestion), which is the intended usage per spec §7 / §11.9.

| Resource | SKU | Est. monthly cost | Notes on the estimate |
|----------|-----|-------------------|-----------------------|
| Resource group | — | **$0** | No charge for the RG itself. |
| ACR | Basic | **~$5** | Basic is a flat ~$0.167/day standing charge; storage for two small images is within the included quota. |
| Log Analytics | PAYG, 30-day | **~$0–2** | First 5 GB/month ingestion free, then ~$2.76/GB; a demo ingests well under that. First 31 days retention free, so 30-day retention adds nothing. |
| ACA environment | Consumption | **$0** | The environment has no standing charge; usage bills via the apps below. |
| `todo-api` (backend) | Consumption, min 0 | **~$0–2** | Scale-to-zero: no replicas running when idle → mostly covered by the monthly free grant (180k vCPU-s + 360k GiB-s + 2M requests per subscription). |
| `todo-web` (frontend) | Consumption, min 1 | **~$4–12** | Always-on single 0.25 vCPU / 0.5 GiB replica. Mostly billed at the lower **idle** vCPU rate when not serving requests; the free grant offsets part of it. Range reflects idle-vs-active uncertainty. |
| PostgreSQL Flexible Server | Burstable B1ms, 32 GiB | **~$16–18** | Compute ~$13–15/mo (1 vCore burstable, always on) + 32 GiB storage ~$3–4/mo. LRS backup within the free-equal-to-provisioned-storage allowance. **The dominant, always-on cost.** |
| **Total** | | **~$25–37 / month** | Dominated by Postgres; the rest is near-zero when the demo is idle. Lands in the "low single-digit to low-double-digit USD/month" the spec targets (§7, §11.9). |

Cheapest realistic idle floor (backend at zero, frontend barely used, no log
ingestion spike): **~$21–25/month**, essentially Postgres B1ms + ACR Basic.

---

## 3. Decision points (where a second reasonable choice exists)

These are the choices a human should explicitly approve. For each, the **spec's
default is the left/recommended option** and I recommend keeping it for this demo;
the alternatives are documented so the choice is deliberate, not assumed.

### 3.1 PostgreSQL compute tier — **B1ms vs B2s** (this is the meaningful cost lever)

| Option | Est. monthly cost | Suited for | Limits |
|--------|-------------------|------------|--------|
| **Burstable `Standard_B1ms`** (1 vCore, 2 GiB) — spec default | **~$16–18** (compute+storage) | Idle/low-traffic demo, single anonymous user, occasional CRUD | Burstable CPU credits; sustained load will throttle. 2 GiB RAM. No HA. |
| Burstable `Standard_B2s` (2 vCore, 4 GiB) | **~$29–31** | A demo that might get live/interactive traffic during a presentation | Still burstable (credit-limited under sustained load), just a higher baseline. Roughly doubles the DB bill. |
| General Purpose `Standard_D2s_v3` (2 vCore, 8 GiB) | **~$95–125+** | Production-ish steady workloads | Overkill for a demo; ~5–7× the cost. **Not recommended** and would violate §11.9 without an explicit note. |

**Recommendation: `Standard_B1ms`.** The workload is a single anonymous user doing
occasional CRUD against one small table (spec §1/§2), and idle cost is the stated
priority (§7, §11.9). B1ms is the cheapest tier that still runs Postgres 16 with
TLS. If a live demo is expected to have several concurrent users hammering it,
B2s is the clean one-parameter bump — but that is a conscious, cost-doubling choice
for the human to make, not a default.

### 3.2 Container Apps plan — **Consumption vs Dedicated**

| Option | Est. monthly cost | Suited for | Limits |
|--------|-------------------|------------|--------|
| **Consumption workload profile** — spec default | **~$4–14** (mostly frontend always-on) | Bursty/idle demo workloads; scale-to-zero backend | Cold starts on scale-from-zero; per-request billing. |
| Dedicated workload profile (D4, etc.) | **~$150+** (reserved cores, always on) | Steady high-throughput, VNet-heavy, compliance isolation | Standing charge for reserved compute even when idle. **Explicitly forbidden by §11.9.** |

**Recommendation: Consumption.** Spec §11.9 mandates it and forbids Dedicated. No
reason to deviate — the demo benefits from scale-to-zero and has no isolation or
sustained-throughput requirement. Listed only so the choice is on record.

### 3.3 Container Registry — **Basic vs Standard**

| Option | Est. monthly cost | Suited for | Limits |
|--------|-------------------|------------|--------|
| **Basic** — spec default | **~$5** | One project, a couple of small images, low pull volume | 10 GiB included storage, lower throughput, no geo-replication. Plenty for two images. |
| Standard | **~$20** | More repos/images, higher pull throughput, teams | Adds included storage/throughput the demo won't use. |
| Premium | **~$50+** | Geo-replication, private link, content trust | Enterprise features entirely out of scope (§8/§11.9). |

**Recommendation: Basic.** Two small images, single region, low pull volume — Basic
covers it at the lowest cost. Standard/Premium buy throughput and geo features this
demo has no use for.

### 3.4 Frontend Container App — **min replicas 1 vs 0**

| Option | Est. monthly cost impact | Suited for | Limits |
|--------|--------------------------|------------|--------|
| **min 1** — spec default (§5.6) | **~$4–12/mo** (always-on replica) | Live demos where instant SPA load matters | Small standing cost even when nobody's using it. |
| min 0 (scale-to-zero) | **~$0 idle** | Absolute cost minimization; you accept a cold start on the first hit | First visitor waits for a cold start (nginx starts fast, so the penalty is small). |

**Recommendation: min 1** (spec default) for better demo UX — but this is the single
cheapest thing to flip if idle cost must be near-zero. Spec §5.6 explicitly allows 0
"if cost is paramount." Low-stakes, easily reversible either way.

---

## 4. Identity strategy — OIDC federated credentials (summary only; NOT created here)

Per spec §11.6 and `architecture-memory.md` (2026-07-24 entry), CI/CD authenticates
to Azure via **OpenID Connect / workload identity federation — no stored client
secret**. GitHub Actions (`azure/login@v2`) and the Azure DevOps equivalent (a
workload-identity-federation ARM service connection) both mint short-lived,
per-run tokens scoped to the repo/branch/environment.

**This is a manual, one-time, human bootstrap.** No agent — including this one —
creates app registrations, federated credentials, RBAC role assignments, or GitHub
secrets/variables (§8, §11.6, §11.8; hard rule in architecture-memory). This
proposal does **not** re-invent those commands; the exact `az ad app create` /
`az ad sp create` / `az ad app federated-credential create` /
`az role assignment create` / `gh variable set` / `gh secret set` sequence is
already written verbatim in **spec §11.6** and is the single source of truth. When
this stage moves to APPLY, that same block will be copied into `infra/README.md` /
`infra.md`, clearly labelled "manual, run once, by a human."

What the pipelines will **reference** (never create):
- GitHub **variables**: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
  (not secret — no credential material; OIDC stores nothing sensitive in GitHub).
- GitHub **secrets**: `PGADMIN_PASSWORD` (Postgres admin password), and
  `TODO_DB_CONNECTION` if the connection string is assembled outside Bicep — these
  feed the `@secure()` Bicep parameters (§11.3). No secret is ever committed to git
  or hardcoded in IaC/YAML.

RBAC scope (from §11.6): `Contributor` on **`rg-todo-demo`** (not the whole
subscription) + `AcrPush` on the registry — least-privilege for a demo; a reviewer
may tighten further. If `main.bicep` ends up subscription-scoped (to create the RG
itself), the SP would need subscription-scope Contributor OR the human pre-creates
the RG and keeps the SP scoped to it (recommended). **This RG-scope-vs-subscription-
scope Bicep decision is deferred to APPLY and flagged in §5 below.**

**Who does it:** a human with Owner (or User Access Administrator + Contributor) on
the target subscription (spec assumption §9.9), once, before the first CD run.

---

## 5. Open questions / things to reconsider (flagged, NOT baked into the proposal)

These are surfaced deliberately rather than silently decided. None is blocking; all
are for reviewer/human sign-off.

1. **Bicep `main.bicep` scope: resource-group-scoped vs subscription-scoped.**
   Spec §11.3 leaves this to the devops agent ("picks one and documents it"). It
   directly affects the OIDC SP's required RBAC scope (§4 above): subscription-scope
   Bicep that creates the RG needs subscription-level Contributor, which is broader
   than the least-privilege §11.6 intent. **Leaning recommendation for APPLY:**
   human pre-creates `rg-todo-demo` (`az group create`), `main.bicep` is
   **RG-scoped**, SP stays scoped to the RG. Calling it out now so the identity
   bootstrap and the Bicep scope are decided together, not in conflict.

2. **`AcrPull` role assignment inside Bicep vs the deploying SP's privilege.**
   §11.3 wants the Container Apps' managed identities granted `AcrPull` **via Bicep**
   (a role assignment resource). Creating a role assignment requires the *deploying*
   principal to have `Microsoft.Authorization/roleAssignments/write` (e.g. Owner or
   User Access Administrator) — plain `Contributor` (what §11.6 grants the CI SP)
   **cannot create role assignments**. So either the CI SP needs a higher role, or
   the `AcrPull` assignment is done once by the human during bootstrap and left out
   of the CD-run Bicep. This is a real friction point between §11.3 and §11.6 that
   should be resolved before APPLY. **Recommendation:** do the managed-identity
   `AcrPull` grant as part of the one-time human bootstrap, and keep the repeatable
   CD Bicep to Contributor-only operations — worth explicit reviewer agreement.

3. **Backend↔frontend CORS/ordering within a single Bicep deploy.** Spec §11.5/§11.7
   flags that the frontend bakes the backend FQDN at build time and the backend must
   allow the frontend FQDN via CORS — a two-phase apply. The spec offers two paths
   (imperative image updates after one Bicep run, recommended; or run Bicep twice).
   This is an APPLY-stage implementation detail, not a SKU/cost decision, but noting
   it here so it isn't forgotten: it does not change any resource or cost in §1–§2.

4. **Frontend hosting model (Static Web Apps) — settled, not reopening.** Memory and
   §5.2 firmly chose "both on Container Apps." I am **not** proposing to change it;
   noting only that the frontend `todo-web` always-on replica (§3.4) is the second-
   largest line item after Postgres, and Static Web Apps would be cheaper for the SPA
   alone. Per the memory rules this is a settled decision; I flag it only as the
   known cost/architecture tradeoff a future cost-minimization cycle would revisit,
   exactly as §5.2 itself already says. No action requested.

5. **`npm audit` / lint in CI.** Not a SKU/infra decision, but relevant to the CI
   workflow authored at APPLY: `changes.md` warns the Vite 5 dev-toolchain advisories
   (esbuild/vite) would fail a naive `npm audit --audit-level=high` gate. The CI job
   (§11.4) should **not** add such a gate, or must set the level to not fail on those
   known dev-only advisories. Recording it here so it's on the radar for the CI YAML.

---

## Status

Proposal ready. **Waiting on SKU approval** before proceeding to APPLY mode.

Concretely, the human decisions requested are:
- Confirm **Postgres `Standard_B1ms`** (or choose B2s) — §3.1 — this is the main
  cost lever.
- Confirm **ACA Consumption**, **ACR Basic**, **frontend min replicas = 1** (or 0)
  — §3.2 / §3.3 / §3.4 (all spec defaults; confirm or adjust).
- Acknowledge the two OIDC/RBAC frictions in §5 items 1–2 so the manual identity
  bootstrap and the Bicep scope are decided consistently before any APPLY run.

No files under `infra/`, `.github/workflows/`, or `azure-pipelines.yml` were
created or modified, and no Azure resource, credential, or role assignment was
provisioned (PROPOSE mode, §11.8). Next step is a human approving the SKUs above,
after which APPLY mode authors the Bicep + pipelines using the approved choices.
