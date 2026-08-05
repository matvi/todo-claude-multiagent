# Infrastructure proposal — 2026-07-28

Feature cycle: **Observability / distributed tracing (§12) + managed-identity-first
identity & secrets policy (§13)**, branch `pipeline/appinsights-tracing` (off
`main`). PROPOSE mode — nothing here is provisioned; this is authored + costed for
SKU/decision sign-off before any Bicep is written.

> This is a **new proposal for a new cycle**, not an amendment. The resources in
> the prior proposal (ACR Basic, ACA Consumption env, Postgres B1ms, two Container
> Apps, Log Analytics `log-todo-demo`) are **already-deployed production reality on
> `main`** and are NOT re-proposed here. Only the *delta* this feature introduces is
> below. Everything not listed as **new** or **changed** stays exactly as deployed.

Grounded against the actual deployed IaC: `infra/main.bicep`,
`infra/main.parameters.json`, and `infra/README.md` (bootstrap Phases 1–3) as they
stand today — not the original spec draft.

---

## 0. What this cycle changes vs. the deployed infra (the diff)

| Resource / setting | State | This cycle |
|---|---|---|
| ACR (Basic), ACA env, Log Analytics `log-todo-demo`, `todo-web` | deployed | **unchanged** |
| **Application Insights `appi-todo-demo`** | does not exist | **NEW** — workspace-based, backed by existing `log-todo-demo` |
| **Role assignment: `todo-api` MI → `Monitoring Metrics Publisher` on `appi-todo-demo`** | does not exist | **NEW** |
| **Postgres Flexible Server `pg-todo-demo-cus01`** | password auth only | **CHANGED** — enable Entra auth + set Entra administrator (server side, Bicep) |
| **`todo-api` Container App secret `todo-db-connection`** | present, `ConnectionStrings__TodoDb` = `secretref` | **DROPPED** — becomes a passwordless, non-secret plain env var |
| **`todo-api` App Insights connection** | did not exist as infra | **NEW, non-secret** — `APPLICATIONINSIGHTS_CONNECTION_STRING` as a plain env var (NOT a Container Apps secret) |
| **`todo-api` env `Postgres__UseEntraAuth`** | absent (backend defaults to `false`) | **NEW** — set to `true` so the backend takes its Entra-token DB path (backend code already merged, §13.4) |
| **Key Vault** | never provisioned | **still not provisioned** — see §6, no application secret requires it |

Net secret count on the deployed system after this cycle: **zero application runtime
secrets** (the `todo-db-connection` Container Apps secret is removed and nothing
replaces it). The only remaining credential anywhere is `PGADMIN_PASSWORD`, which is
a **deploy-time CI/CD secret**, not an app-runtime secret (§13.9) — out of scope, unchanged.

---

## 1. New / changed resources — concrete SKU/tier + rationale

### 1a. Application Insights `appi-todo-demo` — **NEW**

- **Type / "SKU":** `Microsoft.Insights/components`, `kind: web`,
  `Application_Type: web`, **workspace-based** (`IngestionMode: LogAnalytics`,
  `WorkspaceResourceId` → the **existing** `log-todo-demo` workspace's resource id).
  Workspace-based App Insights has **no independent pricing SKU** of its own — it has
  no fixed monthly fee; all billing flows through the backing Log Analytics
  workspace's tier, which is already `PerGB2018` (pay-as-you-go) at 30-day retention.
  So the only "tier" decision is *reuse the existing workspace*, which the spec
  (§12.6) mandates.
- **`DisableLocalAuth: true`** — per §13.5, this turns off shared-key (instrumentation
  key) ingestion so the embedded key in the connection string stops being a usable
  credential; ingestion then requires an Entra identity with the publisher role
  (1b below). This is the single setting that makes "connection string = non-secret"
  true rather than aspirational.
- **Region:** `eastus` (same as `log-todo-demo` / the ACA env). App Insights follows
  its workspace's region; this is independent of the Postgres `centralus`
  region-restriction (§12.6) — **no contradiction** with architecture-memory.
- **Rationale:** one new resource satisfies the whole §12 tracing requirement; reusing
  the single existing workspace keeps the "single of everything" posture, one queryable
  telemetry store, and adds no second retention setting or cost center.
- **IaC shape (to be authored in Mode 2, not now):** new `infra/modules/appinsights.bicep`
  taking the workspace id in and emitting `connectionString` as an output; wired in
  `main.bicep` off `logAnalytics.outputs.id`. **No second Log Analytics workspace.**

### 1b. Role assignment: `todo-api` MI → `Monitoring Metrics Publisher` on `appi-todo-demo` — **NEW**

- **Role:** `Monitoring Metrics Publisher`
  (`3913510d-42f4-4e42-8a64-420c390055eb`), scoped to the `appi-todo-demo` component,
  assigned to `todo-api`'s **existing system-assigned managed identity** (its
  `principalId` is already a `main.bicep` output). This is what lets the backend's
  `DefaultAzureCredential` publish telemetry once local auth is disabled (1a).
- **Placement — decision point, see §3c.** Recommendation: apply it as a **manual
  human bootstrap step**, consistent with how the existing `AcrPull` grants are
  handled in this repo (excluded from the CD-runnable Bicep because a `Contributor`
  principal can't write role assignments — `infra/README.md` Phase 3). The spec
  (§13.10) says "Bicep, AcrPull pattern already there," but in *this* repo the AcrPull
  pattern is a manual Phase-3 grant, not Bicep — so following the real convention means
  a manual grant. Cost: **free**.

### 1c. Postgres `pg-todo-demo-cus01` — enable Entra auth + set Entra administrator — **CHANGED**

- **No SKU/tier change.** Stays Burstable `Standard_B1ms`, PG16, 32 GiB, no HA, LRS
  backup, `centralus`, public access + "allow Azure services" firewall + TLS — all
  exactly as deployed. Immutable properties (name `pg-todo-demo-cus01`, `centralus`)
  are untouched (they must stay pinned per `main.parameters.json`).
- **What changes (server side, in Bicep):**
  - Set `properties.authConfig` to **enable Entra (Azure AD) authentication**
    alongside password auth: `activeDirectoryAuth: 'Enabled'`,
    `passwordAuth: 'Enabled'` (dual-auth — keep the admin password as break-glass;
    Entra-only is the optional hardening in §3d, not recommended for this demo).
  - Add the `Microsoft.DBforPostgreSQL/flexibleServers/administrators` child resource
    to set the **Entra administrator** (an Entra user or group — the identity that then
    runs the manual `pgaadauth_create_principal` step). Requires `objectId`,
    `principalName`, `principalType`, and `tenantId` — these are **inputs a human must
    supply** (see §3b).
- **Rationale:** §13.4 mandate — `todo-api` authenticates to Postgres with its managed
  identity via an Entra token (backend code already merged), which requires the server
  to accept Entra auth and to have an admin who can create the MI's DB role. Cost: **free**.
- **NOT in Bicep (cannot be):** the in-database `pgaadauth_create_principal` + grants —
  those run *inside* Postgres as the Entra admin and are a manual human step (§4).

### 1d. `todo-api` Container App env/secret wiring — **CHANGED**

- **DROP** the `todo-db-connection` Container Apps secret entirely, and the
  `Password=…` from the connection string. `ConnectionStrings__TodoDb` becomes a
  **plain (non-secret) env var**, passwordless, with `Username=` set to the managed
  identity's Postgres role name (not `todoadmin`):
  `Host=<server>.postgres.database.azure.com;Port=5432;Database=tododb;Username=<mi-role-name>;Ssl Mode=Require;Trust Server Certificate=true`.
- **ADD** `APPLICATIONINSIGHTS_CONNECTION_STRING` as a **plain (non-secret) env var**
  (value = `appinsights.outputs.connectionString`). Flat name, no `__` (§12.5). NOT a
  Container Apps secret, NOT Key Vault.
- **ADD** `Postgres__UseEntraAuth=true` plain env var so the merged backend code takes
  its Entra-token DB path in Azure (it defaults to `false` = local password path).
- **No change to `todo-web`** — it holds no credentials.
- **Cost:** none (env var changes are free); the `@secure() postgresAdminPassword`
  param stays only for server *provisioning*, not app runtime.

---

## 2. Cost estimate for the NEW resource (Application Insights ingestion)

**Fixed monthly cost added by this cycle: ~$0.** Workspace-based App Insights has **no
fixed fee**; the role assignment and managed-identity auth are **free**; enabling
Entra auth on Postgres is **free**; dropping a Container Apps secret is **free**. This
cycle is cost-neutral-to-negative on the §11.9 posture (it removes a stored secret and
provisions no Key Vault).

**The only variable is telemetry ingestion**, billed usage-based through the existing
`log-todo-demo` workspace at the Log Analytics per-GB rate:

- **Free grant: ~5 GB/month** before ingestion is billed (per the spec §12.9 framing;
  region/account-dependent). A low-volume single-user demo with scale-to-zero backend
  is expected to sit **well inside** the free grant → **real-world cost ≈ $0/month**.
- **Beyond the grant:** roughly **~$2.30–$3.00 per GB** ingested (region-dependent;
  **estimate only** — confirm against the Azure Monitor pricing page for the actual
  region). At, say, a hypothetical 10 GB/month you'd pay for ~5 GB over the grant ≈
  **~$12–15/month** — not expected at demo traffic, shown only to make the pricing
  model explicit rather than silently assumed.
- **Retention cost: $0.** The workspace keeps its existing **30-day** retention, which
  is within Log Analytics' included ~31-day retention — no added retention charge, and
  we do **not** raise it (§11.9).

**Pricing model, stated so it isn't assumed:** you pay per **GB ingested** into the
workspace, not per request/trace; the levers that control that volume are **sampling**
(§3a) and an optional **daily ingestion cap** (§3e). If projected ingestion ever
becomes non-trivial, that gets flagged in `changes.md` per the §11.9 "no silent
costlier choices" rule.

---

## 3. Decision points (more than one reasonable choice exists)

### 3a. Azure Monitor exporter sampling percentage
- **Options:** 100% (capture every trace) · fixed sampling (e.g. 25–50%) · adaptive sampling.
- **Recommendation: 100%** for this low-traffic demo — full fidelity, and volume is
  trivially inside the free grant, so no reason to drop traces. **The lever exists**:
  if traffic ever grows enough that ingestion approaches the free grant, switch to fixed
  or adaptive sampling on the exporter (a backend `TelemetryRegistration` change, not an
  infra change) to cap ingestion cost. Note this is a **backend-code** knob, not Bicep —
  called out here so the reviewer knows where the cost dial lives.

### 3b. Postgres Entra administrator identity (human input required)
- **Options:** an individual **Entra user** (simplest for a demo) · an **Entra group**
  (better — survives staff changes, lets several people administer without editing infra).
- **Recommendation: an Entra group** if one is readily available, else the deploying
  human's own user. Either way the human must supply `objectId` / `principalName` /
  `principalType` (`User` or `Group`) / `tenantId` as Bicep params — these are **not
  guessable** and are a required input before Mode 2 can author/validate the Postgres change.

### 3c. Where the `Monitoring Metrics Publisher` role assignment lives
- **Options:** (a) authored in `main.bicep` (spec §13.10's literal wording) · (b) a
  **manual human Phase-3 grant** like the existing `AcrPull` grants.
- **Recommendation: (b), manual grant.** It matches this repo's actual established
  convention (`infra/README.md` deliberately keeps role assignments out of the
  CD-runnable Bicep because a `Contributor` principal can't create them). Putting it in
  Bicep would only be safe if the *infra-deploy* identity holds `User Access
  Administrator`/`Owner`; since infra here is human-deployed and role grants are already
  a documented manual Phase 3, adding one more `az role assignment create` there is the
  consistent, lowest-surprise choice. Flagging the spec-vs-repo discrepancy explicitly.

### 3d. Postgres dual-auth vs. Entra-only
- **Options:** **dual** (`passwordAuth: Enabled` + `activeDirectoryAuth: Enabled`) ·
  **Entra-only** (`passwordAuth: Disabled`).
- **Recommendation: dual-auth.** Keeps the admin password as a break-glass path and
  avoids a hard cutover for a live app (§13.4 lists Entra-only as *optional* hardening).
  Entra-only would also strand `PGADMIN_PASSWORD` and any password-based break-glass —
  defer it to a later hardening cycle.

### 3e. Optional daily ingestion cap on `log-todo-demo`
- **Options:** no cap (default) · a small daily cap (e.g. 1 GB/day) as a cost circuit-breaker.
- **Recommendation: no cap** for the demo (a cap can *drop* telemetry once hit, which is
  worse than a few dollars). Noted as an available guardrail if ingestion ever spikes.

---

## 4. Manual / human-only bootstrap steps this cycle adds

These are **run once, by a human** with the right privileges — **never by an agent**
(PROPOSE mode, §11.8 / §13.10). They extend the existing `infra/README.md` Phase 3 and
follow the exact same "manual, human-run" convention as the OIDC/RBAC and `AcrPull`
bootstrap already documented there. In Mode 2 these get written verbatim into
`infra/README.md`; listed here for sign-off.

**(A) Postgres Entra admin — supply inputs, then the Bicep server-side change sets it.**
A human provides the Entra admin `objectId` / `principalName` / `principalType` /
`tenantId` (§3b) as deploy params; the Bicep `administrators` child resource + `authConfig`
do the server-side enablement. (Bicep-authored, human-applied — same as all infra here.)

**(B) Create the managed identity's Postgres role + grant it schema/DDL rights
(cannot be done in Bicep — runs inside the database).** Connect to the **`postgres`**
database **as the Entra administrator** and run:
```sql
-- run as the Entra admin, connected to the `postgres` database
select * from pgaadauth_create_principal('<todo-api-managed-identity-name>', false, false);
-- then, connected to tododb, grant the new role what startup Migrate() needs:
--   CONNECT on tododb, USAGE/CREATE on the schema, and table DDL/DML rights
--   (schema ownership is simplest for the demo's auto-migrate-at-startup path).
```
`<todo-api-managed-identity-name>` is the name of `todo-api`'s system-assigned managed
identity (this becomes the `Username=` in the passwordless connection string, §1d).
This mirrors the §11.6 OIDC bootstrap in style and labeling: **manual, run once, by a human.**

**(C) Grant `Monitoring Metrics Publisher` (per §3c recommendation — manual Phase-3 grant):**
```bash
# Human with User Access Administrator/Owner, after the AI component + app identity exist:
az role assignment create \
  --assignee "<todo-api-managed-identity-principalId>" \
  --role "Monitoring Metrics Publisher" \
  --scope "<appi-todo-demo resource id>"
```

---

## 5. Open questions / risks

1. **Dead config cleanup — the old password path becomes orphaned.** Once `todo-api`
   cuts to Entra auth, the `todo-db-connection` Container Apps secret and the
   password-based connection string are **dead config**. They must be actively removed
   from the `todo-api` Container App (not just left dangling) so a stale
   password-bearing secret doesn't linger in the deployed app. The admin password itself
   stays only for server provisioning/break-glass (dual-auth, §3d); it is no longer an
   app runtime credential.

2. **Cutover sequencing on a LIVE app (highest risk).** This is a running production app
   on `main`, not a fresh bootstrap. If the env flips to `Postgres__UseEntraAuth=true`
   **before** the in-DB principal exists and is granted (step 4B), the backend loses DB
   connectivity — and because migrations run at startup (§3.4), a cold-started replica
   could fail its startup `Migrate()`. Safe order:
   **(i)** deploy the Postgres Entra-auth server change + set the Entra admin →
   **(ii)** run `pgaadauth_create_principal` + grants (4B) →
   **(iii)** grant `Monitoring Metrics Publisher` (4C) + provision the AI component
   with `DisableLocalAuth: true` →
   **(iv)** *only then* update `todo-api`: set `Postgres__UseEntraAuth=true`, swap
   `ConnectionStrings__TodoDb` to the passwordless value, add
   `APPLICATIONINSIGHTS_CONNECTION_STRING`, and drop the `todo-db-connection` secret.
   Doing (iv) before (ii) is the concrete outage scenario to avoid. A single revision
   flip with all of (iv) at once (rather than piecemeal) is cleanest.

3. **`DisableLocalAuth: true` ordering.** If local auth is disabled on the AI component
   **before** the `Monitoring Metrics Publisher` grant (4C) propagates, telemetry
   ingestion **silently fails** (403) — the app still runs (exporter errors are
   non-fatal) but no traces land. Grant the role first (or accept a brief gap while RBAC
   propagates, which can take a few minutes). This is telemetry-only risk, not an app-availability risk.

4. **`Trust Server Certificate=true` remains** in the connection string — it encrypts
   but skips CA verification (MITM-susceptible). Unchanged from today and **out of this
   cycle's scope**; the production hardening (`SslMode=VerifyFull` + Azure Postgres CA)
   is already flagged in prior review/`changes.md`. Noted so it isn't assumed fixed here.

5. **Postgres role name coupling.** The passwordless `Username=` must exactly match the
   principal name created in 4B (the MI's name). A mismatch fails auth. Whoever runs 4B
   and whoever sets the connection string must agree on the exact managed-identity name —
   worth a single documented source of truth in `infra/README.md`.

6. **Key Vault — deliberately NOT proposed (§6 below).** Flagging only so a reviewer who
   expected one knows its absence is intentional, not an omission.

---

## 6. Key Vault — not provisioned (agree with the spec)

I **agree** with §13.6's conclusion: after this cycle, every application credential is
covered by managed identity / Entra (Postgres via MI token; App Insights via MI + local
auth disabled; ACR pull via MI; CI/CD via OIDC). **Zero application runtime secrets
remain**, so nothing reaches the "cannot use managed identity" branch that would require
Key Vault. Provisioning `kv-todo-demo` now would add a resource and per-operation cost to
hold nothing. The Key Vault pattern stays **specified for the future** (§13.6) — the
first future secret that genuinely cannot use MI triggers it — but it is correctly **not
instantiated** this cycle. No disagreement.

---

## Status

**PROPOSE mode — proposal ready, waiting on SKU/decision sign-off.** No file under
`infra/`, `.github/workflows/`, or `azure-pipelines.yml` was created or modified, and
no Azure resource, role assignment, or credential was touched. On approval (App Insights
workspace-based reuse of `log-todo-demo`; the four decision points in §3 — recommendations:
100% sampling, Entra **group** admin, **manual** role grant, **dual-auth** Postgres, **no**
daily cap), Mode 2 will author `infra/modules/appinsights.bicep`, the Postgres Entra-auth +
`todo-api` env changes, the `Monitoring Metrics Publisher` grant, and the `infra/README.md`
manual-bootstrap additions, then validate with `az deployment group what-if` / `validate`
only (never apply).
