# Infrastructure proposal — 2026-08-12

Feature cycle: **End-user authentication with Microsoft Entra External ID (specs
§16)**, branch `pipeline/entra-external-id-auth`. **INFRA-PROPOSE mode** — nothing
here is provisioned and **no file under `infra/` has been touched**. This document
exists to get SKU/tier sign-off before any Bicep is written (that happens in the
INFRA-APPLY pass).

> **This replaces the previous `infra-proposal.md`**, which covered the
> already-merged observability/§13 cycle. Everything that cycle proposed
> (Application Insights, Postgres Entra admin, dropping the `todo-db-connection`
> secret) is deployed reality on `main` and is **not** re-proposed here.

Grounded against the **actual IaC on disk**, not against the narrative in the stale
`.pipeline/infra.md`. Verified by reading:

- `infra/main.bicep` — RG-scoped; provisions ACR (Basic), Log Analytics (30d),
  App Insights (workspace-based), ACA managed environment, Postgres Flexible
  Server + `tododb` + firewall rule, and the two Container Apps.
- `infra/main.parameters.json` — non-secret dev/demo params;
  `postgresAdminPassword` is `@secure()` and supplied at deploy time.
- `infra/modules/` — `registry.bicep`, `loganalytics.bicep`, `appinsights.bicep`,
  `acaEnvironment.bicep`, `postgres.bicep`, `containerApp.bicep`.
  **There is no `keyvault.bicep`.**
- `frontend/Dockerfile` — has exactly one build arg, `VITE_API_BASE_URL`.
- `.github/workflows/cd.yml` — logs in with OIDC, builds/pushes images and runs
  `az containerapp update`. It does **not** run `az deployment group create`, so
  Bicep changes do not reach the running apps on their own (specs 16.12 item 6).

---

## 0. The diff — specs §16 vs. what `infra/` actually provisions today

| Resource / setting | State on disk | This cycle |
|---|---|---|
| ACR (Basic), Log Analytics, App Insights, ACA environment, `todo-web` | deployed, in Bicep | **unchanged** |
| Postgres Flexible Server `pg-todo-demo-cus01` (B1ms, centralus) | deployed, in Bicep | **unchanged** — §16 explicitly does not touch §14's DB auth or the server |
| `todo-api` Container App (0–3 replicas, 0.25 vCPU / 0.5 GiB) | deployed, in Bicep | **changed — config only.** Five new *plain, non-secret* env vars (`AzureAd__Instance`, `__TenantId`, `__ClientId`, `__Audience`, `__Scopes`). No SKU/scale/replica change. |
| **Azure Key Vault** | **does not exist** (no `keyvault.bicep`, no reference in `main.bicep`) | **NEW** — custodial store for the Google OAuth client secret (§16.6). **SKU decision required — §1 below.** |
| **Entra External ID tenant** (customer directory) | **does not exist** — the only Entra usage today is workforce-tenant managed identities + CI/CD OIDC | **NEW** — human-created, but it carries a billing link to the subscription. **Tier/linking decision required — §2 below.** |
| `frontend/Dockerfile` build args | one (`VITE_API_BASE_URL`) | **changed** — four more `VITE_ENTRA_*` / `VITE_API_SCOPE` args. No cost. |
| `ci.yml` / `cd.yml` / `azure-pipelines.yml` | build-arg plumbing for one var | **changed** — pass the four new build args from GitHub repo *variables* (public identifiers, not secrets). No cost. |
| Container Apps **secrets** on `todo-api` | zero | **still zero** — §16 adds none. Invariant preserved. |
| App runtime secrets anywhere | zero | **still zero** — the one new secret (Google client secret) is consumed by Entra External ID and read by humans only. |

**Two decisions need approval: the Key Vault SKU (§1) and the External ID tenant
tier/linking model (§2).** Everything else in the table is config plumbing with no
SKU choice and no cost delta.

---

## 1. Azure Key Vault (new)

**Why this service:** the Google OAuth client secret (§16.5.5) is issued by Google,
a third party with no Entra relationship. It cannot be replaced by a managed
identity or workload-identity federation, so it is exactly the "genuinely cannot
authenticate via Entra" case in which §13.6 mandates Key Vault as the fallback.
Note *who consumes it*: **Entra External ID**, once, at IdP configuration time. No
application of ours ever reads it — per §16.6 neither `todo-api`'s nor `todo-web`'s
managed identity gets any role on this vault. The vault is **custodial only**: it
gives the value a rotation-safe home instead of a chat message or a sticky note.

> Cost figures below are **rough estimates**, US-East list prices in USD as of
> 2026-08, and are region- and usage-dependent. Verify against the Azure pricing
> calculator before sign-off.

| Option | Est. monthly cost | Suited for | Limits |
|---|---|---|---|
| **Key Vault Standard** (software-protected) | **~$0.00** — $0.03 per 10,000 secret operations; this vault sees a handful of operations *per year* (a human writes the secret once, Entra never re-reads it from here). Effectively free. | Secrets and certificates that don't need FIPS 140-2 Level 3 hardware protection. Exactly our case: one string, written by a human, read by a human. | No HSM-backed keys. Public endpoint unless you add a private endpoint (extra cost + a VNet we don't have). Soft-delete retention 7–90 days. |
| **Key Vault Premium** | **~$0.00 for our usage** — same $0.03/10k operation price; the premium delta is **$1.00 per HSM-protected key per month**, and we are storing a **secret**, not a key. So Premium would cost the same *and* deliver nothing. | Workloads needing HSM-backed *cryptographic keys* (FIPS 140-2 L3), BYOK, key-wrapping for storage/SQL CMK. | Premium's HSM protection applies to **keys only — secrets are stored identically to Standard**. Buying Premium for a secret is a pure no-op. |
| **Azure Key Vault Managed HSM** | **~$3,000–3,700/month** (dedicated HSM pool, ~$4.85/hr for the standard pool, billed whether used or not) | Regulated workloads needing single-tenant, FIPS 140-2 L3 HSM with full administrative isolation. | Absurdly out of proportion for one demo secret; minimum 3-partition pool; no scale-to-zero. Listed only to close the option space. |
| *(sub-option)* add a **private endpoint** to any of the above | **+~$7.30/month** per endpoint + ~$0.01/GB processed | Vaults that must not be reachable from the public internet. | Requires a VNet + subnet, which this architecture deliberately does not have (§5.4, §8: no VNet/private endpoints). Would be the single largest line item in the whole demo. |

**Recommendation: Key Vault Standard**, with `enableRbacAuthorization: true`,
`enableSoftDelete: true`, `softDeleteRetentionInDays: 7`,
`enablePurgeProtection: false`, public network access enabled, **no role
assignments and no secret values in Bicep**.

Reasoning tied to the spec's actual requirements, not "pick the middle one":

- The object being protected is a **secret string**, and Premium's only
  differentiator (HSM-protected *keys*) does not apply to secrets. Premium is
  strictly more expensive-per-feature with zero benefit here — choosing it would be
  cargo-culting "more secure = better tier".
- §16.10 budgets this cycle at **≈$0 incremental cost** and §11.9 mandates the
  cheapest reasonable SKU. Standard is the only option that holds that line.
- **RBAC authorization, never legacy access policies** (§16.6): access to the vault
  is itself Entra-authenticated via a human's identity — no vault access key, no
  SAS token, no shared credential.
- **Purge protection off** is a deliberate, documented tradeoff: soft-delete (7 days)
  gives recoverability, while purge protection is irreversible and would make a
  mis-named demo vault impossible to clean up. Revisit for production.
- **Public network access** because there is no VNet in this architecture; a private
  endpoint would cost ~2× the entire rest of the demo's monthly bill and is out of
  scope per §8.
- Name: derived from the existing `uniqueSuffix` pattern, e.g.
  `kv-todo-${take(uniqueSuffix, 12)}` → 20 chars, inside Key Vault's 24-char global
  limit.

**Explicitly *not* proposed** (and must not be added in INFRA-APPLY): any role
assignment in Bicep (§11.6 — humans grant RBAC), any secret *value* in Bicep or a
parameter file, and any grant to `todo-api`/`todo-web`'s managed identity. The API
has no use for the Google secret; granting it would be an exfiltration path and a
least-privilege violation. This is a deliberate, documented deviation from the
literal original request ("API's managed identity gets read permission").

---

## 2. Microsoft Entra External ID tenant (new)

**Why this service:** end users need self-service sign-up, email+password local
accounts, and Google federation. §16.3.3 rejects the alternatives (Azure AD B2C is
legacy/closed to new tenants; workforce-tenant users are the wrong product and would
pollute the directory holding our subscription and managed identities; rolling our
own puts password material in our codebase).

**Important honesty note:** this is **not a devops/Bicep deliverable**. §16.5.1 and
§16.13 establish that the `Microsoft.AzureActiveDirectory/ciamDirectories` ARM type
is preview, creates only an empty directory, and **cannot be deployed by the CD
service principal or a managed identity** — it needs an interactive user principal
with Tenant Creator. It is listed here anyway because it is a **new billable link to
subscription `9ba6633f-…`** and the tier/linking model is a genuine cost decision
that needs the same sign-off as any SKU.

> Estimates again — External ID is MAU-priced and the published rates move; verify
> before sign-off.

| Option | Est. monthly cost | Suited for | Limits |
|---|---|---|---|
| **30-day free trial tenant** | **$0** for 30 days, then the tenant expires | Kicking the tires on External ID with zero subscription linkage | Hard 30-day expiry; **users, app registrations, user flows and the Google IdP config are all lost** when it lapses; not linked to a subscription, so it cannot become the long-lived demo tenant. Only acceptable as the fallback if a Tenant Creator/policy block prevents option 2 (spec assumption A1). |
| **Subscription-linked, External ID "Core" (P1-equivalent)** | **~$0** at this scale — first **50,000 MAU free**, then roughly **$0.03/MAU** for the next band. This demo is expected in single-digit MAU. | Exactly this feature set: self-service sign-up with attribute collection, local email+password accounts with SSPR, Google/Facebook/Apple/OIDC federation, MSAL React + `Microsoft.Identity.Web` support. | No risk-based Conditional Access / Identity Protection. Country/region is **immutable** after creation. Deleting a `ciamDirectories` resource is known to be awkward — create it once, deliberately (§16.16 risk). |
| **Subscription-linked, External ID "Premium" (P2-equivalent)** | **~$0** at this scale (also 50,000 MAU free), then roughly **$0.23/MAU** — ~8× Core beyond the free band | Consumer apps needing Identity Protection, risk-based Conditional Access, and adaptive MFA on customer accounts | Buys nothing this cycle: **MFA and Conditional Access are explicitly out of scope** (§16.15), and the "Stay signed in?" suppression that would need a CA session policy is out of scope too. Pure future-optionality with no current consumer. |

**Recommendation: subscription-linked External ID tenant on the Core tier**, linked
to subscription `9ba6633f-058c-4269-8b0f-086ab331ef26`, resource group
`rg-todo-demo`, country/region **United States**.

Reasoning:

- Every capability §16 actually requires — self-service sign-up, phone as a custom
  collectable attribute, email+password, Google federation, PKCE SPA + validated API
  audience — is a **Core** feature. Premium's differentiators map 1:1 onto things
  §16.15 puts out of scope, so the upgrade would be paying (eventually) for
  capability the design forbids using.
- Both tiers are **$0 at demo scale** thanks to the 50k free MAU band, so this is
  not a cost decision *today* — it is a decision about which tier the tenant is
  provisioned as, and Core keeps the future bill 8× lower if the demo ever grows.
- The trial tenant is rejected as the primary path because the entire §16.11
  bootstrap (tenant, two app registrations, custom attribute, user flow, Google IdP,
  optional claims, admin consent) would have to be redone from scratch in 30 days,
  and the `users` rows keyed on `oid` would be orphaned when the directory dies.
  Keep it as the documented fallback only, per assumption A1.
- Linking to `rg-todo-demo` keeps the one-RG convention and avoids an orphaned
  billable tenant.

**Open decision for the approver (not a SKU, but it belongs with this one):**
§16.5.1 permits devops to author an *optional, reference-only*
`infra/bootstrap/externalid-tenant.bicep` documenting the preview
`ciamDirectories` resource, excluded from CD and labelled "human, `az login` as a
user, run once". **Recommendation: skip it.** It cannot be used by the pipeline, a
failed/orphaned `ciamDirectories` resource is awkward to clean up, and a Bicep file
that must never be deployed is a trap for the next reader. The portal runbook in
§16.11 is the supported path and is already written. Say the word if you'd rather
have it as reference material.

---

## 3. Changed, but no SKU/tier decision to make (listed for completeness)

These are config-only and cost **$0**. No approval needed on tier grounds; they are
here so the diff is complete and nothing is smuggled in during INFRA-APPLY.

| Item | Change | Cost |
|---|---|---|
| `todo-api` Container App | Five new **plain, non-secret** env vars (`AzureAd__Instance`, `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__Audience`, `AzureAd__Scopes`) fed by new non-`@secure()` top-level Bicep params. `ConnectionStrings__TodoDb` and `APPLICATIONINSIGHTS_CONNECTION_STRING` untouched. | $0 |
| `todo-api` scale/size | **No change** — 0–3 replicas, 0.25 vCPU / 0.5 GiB. §16.10 notes each authenticated request adds one indexed single-row `SELECT`, which is noise at demo scale. | $0 |
| `frontend/Dockerfile` | Four new `ARG`/`ENV` pairs (`VITE_ENTRA_TENANT_SUBDOMAIN`, `VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_SPA_CLIENT_ID`, `VITE_API_SCOPE`) | $0 |
| `ci.yml` / `cd.yml` / `azure-pipelines.yml` | Pass the four values as `--build-arg` from GitHub repo **variables** (public identifiers, deliberately not secrets); dummy values are fine in CI's build-validation step | $0 |
| Log Analytics / App Insights | No config change. Slight extra trace volume from the new endpoints and `users` query; well inside demo noise on PAYG 30-day retention. | ~$0 |
| Postgres Flexible Server | **No change at all.** The `users` table arrives via an EF Core migration applied by the existing startup `Migrate()` over the existing passwordless `NpgsqlDataSource`. No new connection string, no new auth path, no server setting. | $0 |

**Estimated incremental cost of this entire cycle: ≈ $0.00/month** (Key Vault
Standard at a handful of operations per year, External ID within the 50k free MAU
band, no compute or SKU change anywhere). Estimate only, region- and
usage-dependent.

---

## 4. Two things the approver should know before INFRA-APPLY

Neither changes the SKU choices; both will shape how the Bicep is written, so
flagging them now rather than discovering them mid-apply.

1. **Bicep alone will not configure the running app.** `cd.yml` builds and pushes
   images and runs `az containerapp update` — it does **not** run
   `az deployment group create`. So the five `AzureAd__*` env vars must ALSO be
   applied imperatively (§16.12 item 6) before or with the image that needs them.
   The backend fails fast at startup if any of the five is missing (§16.7.1), which
   is by design but means a mis-ordered deploy produces a revision that never
   becomes healthy. The INFRA-APPLY pass will put both paths — Bicep *and* the
   imperative `az containerapp update` + rollback command — into `infra.md`.

2. **The Bicep params for those env vars have no values until the human bootstrap
   runs.** The external tenant ID, subdomain and API client ID are produced by
   §16.11 steps 1–3, which happen in the portal. `main.parameters.json` therefore
   cannot be filled in at authoring time. I intend to give the new params
   empty-string defaults and document that the Bicep must not be applied with them
   blank — but flagging it because it is the kind of thing that silently deploys a
   broken revision if nobody notices. If you would rather the params be **required**
   (no default), so a blank deploy fails loudly at ARM validation instead of at
   container startup, say so and I'll author it that way.

---

## Approval requested

- [ ] **Key Vault: Standard** (RBAC auth, soft-delete 7d, purge protection off,
      public network, no role assignments or secret values in IaC)
- [ ] **Entra External ID: subscription-linked, Core tier, US region**, trial
      tenant as fallback only
- [ ] Skip the reference-only `infra/bootstrap/externalid-tenant.bicep` (or ask for
      it explicitly)
- [ ] New Bicep params for the `AzureAd__*` values: **empty-string defaults**
      (or ask for **required, no default**)

Nothing under `infra/` will be created or edited until these are confirmed.
