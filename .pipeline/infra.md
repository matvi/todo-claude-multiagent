# Infrastructure — Entra External ID end-user auth (specs §16)

**Two passes are recorded here.** Branch `pipeline/entra-external-id-auth`.

| Pass | Date | What it produced |
|---|---|---|
| **INFRA-APPLY** (early) | 2026-08-12 | Key Vault module + five `AzureAd__*` env vars in Bicep, validated by dry-run. Everything from "Resources planned" through "Estimated cost" below. |
| **PIPELINE-APPLY** (late) | 2026-08-13 | CI/CD only — `ci.yml`, `cd.yml`, `azure-pipelines.yml`, using the real build/test commands from `changes.md`/`tests.md`. **No file under `infra/` was touched** (see Drift notes). |

This **overwrites** the pre-§16 `infra.md`, which described the already-merged
§13/§14 observability + passwordless-Postgres cycle.

**Nothing here has been applied.** IaC was authored and validated with read-only
dry-runs only (`what-if` + `validate`). Applying it is a deliberate human step.
The late pass ran no Azure command that creates, modifies or deletes anything;
its only Azure-shaped output is workflow YAML.

---

## ⚠️ Read first — two gates that are NOT enforced today

1. **Branch protection on `main` could not be verified** (the `gh` CLI on this
   machine is not authenticated). Until someone confirms it, the
   human-reviews-the-PR approval gate this whole pipeline depends on is a
   convention, not an enforced rule. See **Branch protection status** below for
   the exact check and the exact fix.
2. **The five `AzureAd__*` env vars are not on the running `todo-api`**, and
   `cd.yml` deliberately does not set them. Deploying the auth-aware backend
   before a human runs the imperative step below produces a revision that never
   goes healthy. CD now *refuses to deploy* in that state rather than breaking
   the app — see **CI/CD → preflights**.

Approved SKU decisions (user, 2026-08-12, from `.pipeline/infra-proposal.md`):

| Decision | Approved value |
|---|---|
| Key Vault | **Standard**, RBAC auth, soft-delete 7d, purge protection **off**, public network, **no** role assignments or secret values in IaC |
| Entra External ID tenant | **Subscription-linked, Core tier, United States** (trial tenant = fallback only) |
| `infra/bootstrap/externalid-tenant.bicep` | **Skipped** (a Bicep file that must never be deployed is a trap) |
| `AzureAd__*` Bicep params | **Empty-string defaults** |

---

## Resources planned

Only **one new resource** and **five new plain env vars**. Everything else in the
resource group is untouched — Postgres, ACR, Log Analytics, App Insights, the ACA
environment and `todo-web` are not modified by this cycle.

| # | Resource | SKU / tier | New? | Why this SKU |
|---|---|---|---|---|
| 1 | ACR `acrtododemo3bkqjv23abtua` | Basic | unchanged | — |
| 2 | Log Analytics `log-todo-demo` | PerGB2018, 30d, 1 GB/day cap | unchanged | — |
| 3 | App Insights `appi-todo-demo` | workspace-based, local auth off | unchanged | — |
| 4 | ACA environment `cae-todo-demo` | Consumption | unchanged | — |
| 5 | Postgres `pg-todo-demo-cus01` | Standard_B1ms, centralus | **unchanged** | §16 explicitly does not touch §14's DB auth or the server. The `users` table arrives via an EF migration applied by the existing startup `Migrate()`. |
| 6 | `todo-web` Container App | 0.25 vCPU / 0.5 GiB, min1/max2 | unchanged | — |
| 7 | `todo-api` Container App | 0.25 vCPU / 0.5 GiB, min0/max3 | **changed — config only** | Five new plain env vars. **No** SKU/scale/replica change: each authenticated request adds one indexed single-row `SELECT`, which is noise at demo scale (§16.10). |
| 8 | **Key Vault `kv-todo-3bkqjv23abtu`** | **Standard** | **NEW** | See below. |
| — | Container Apps **secrets** on `todo-api` | — | **still zero** | §16 adds none. The zero-runtime-secrets invariant (§13.3) holds exactly. |

### Why Key Vault Standard, and why the vault exists at all

The standing rule is managed-identity-first: no application runtime secret should
exist, and none does. The **Google OAuth client secret** is the single value that
genuinely cannot authenticate via Entra — it is issued by Google, a third party
with no Entra relationship — which is exactly the §13.6 fallback case.

**Standard, not Premium:** Premium's only differentiator is HSM-protected
**keys**. We store a **secret**, and secrets are stored identically under both
SKUs — Premium would cost more per feature and deliver literally nothing here.
Managed HSM (~$3,000–3,700/month) is absurd for one demo secret. A private
endpoint (+~$7.30/month) would be the single largest line item in the entire demo
and there is no VNet to attach it to (§8).

**The vault is custodial only.** It is read by **Microsoft Entra External ID**,
once, at IdP-configuration time, and by the human who pastes the value in.

> **Neither `todo-api`'s nor `todo-web`'s managed identity is granted any role on
> this vault, and none ever should be.** The original request asked for "the API's
> managed identity gets read permission". The API has no use for a Google
> credential; granting it would create an exfiltration path for zero benefit.
> **This is a deliberate, documented deviation from the literal request** and is
> recorded in `infra/README.md` and in the module header so it is not "fixed"
> later by someone who assumes it was an oversight.

### The Entra External ID tenant is NOT a Bicep deliverable

Approved as **subscription-linked, Core tier, US region**, but that is a
**cost/tier decision only**. The `Microsoft.AzureActiveDirectory/ciamDirectories`
ARM type is preview, creates only an empty directory, and **cannot be deployed by
the CD service principal or a managed identity** — it needs an interactive user
principal with Tenant Creator (§16.13). It is created by a human in the portal
(§16.11). Core is right because every capability §16 needs (self-service sign-up,
phone attribute, email+password, Google federation, PKCE SPA + validated API
audience) is a Core feature, while Premium's differentiators (Identity
Protection, risk-based Conditional Access, adaptive MFA) map 1:1 onto things
§16.15 puts explicitly **out of scope**. Both tiers are $0 under the 50k free MAU
band; Core keeps the future bill ~8× lower.

---

## IaC files

| Path | What it provisions / changed this cycle |
|---|---|
| `infra/modules/keyvault.bicep` | **NEW.** Key Vault: Standard, `enableRbacAuthorization: true`, `accessPolicies: []`, `enableSoftDelete: true`, `softDeleteRetentionInDays: 7`, purge protection off, `publicNetworkAccess: Enabled`. Outputs `id` / `name` / `vaultUri`. **No secret values, no `secrets` child resource, no role assignments, no access policies.** |
| `infra/main.bicep` | **CHANGED.** Adds `keyVaultName` param (`kv-todo-${take(uniqueSuffix, 12)}` → 20 chars, inside the 24-char limit); wires the `keyVault` module; adds four non-`@secure()` params (`externalTenantSubdomain`, `externalTenantId`, `apiClientId`, `apiScopeName`); adds five plain env vars to `todo-api`; adds `keyVaultName`/`keyVaultId`/`keyVaultUri` outputs. |
| `infra/main.parameters.json` | **CHANGED.** Adds the four new params. `apiScopeName` = `Todos.ReadWrite` (known now); the other three are **blank placeholders** — their values do not exist until the human portal bootstrap runs. |
| `infra/README.md` | **CHANGED.** Adds the Key Vault custodial rule, the Phase 5 runbook, and the placeholder-image warning below. |
| `infra/modules/{registry,loganalytics,appinsights,acaEnvironment,postgres,containerApp}.bicep` | **Untouched.** |

### The five env vars, and why they are plain (not secrets)

Added to `todo-api` alongside the existing `Cors__AllowedOrigins__0`,
`ConnectionStrings__TodoDb`, `Postgres__UseEntraAuth` and
`APPLICATIONINSIGHTS_CONNECTION_STRING`, which are left exactly as they were:

| Env var | Source |
|---|---|
| `AzureAd__Instance` | composed: `https://${externalTenantSubdomain}.ciamlogin.com/` |
| `AzureAd__TenantId` | `externalTenantId` |
| `AzureAd__ClientId` | `apiClientId` |
| `AzureAd__Audience` | composed: `api://${apiClientId}` |
| `AzureAd__Scopes` | `apiScopeName` (`Todos.ReadWrite`) |

A tenant GUID, a tenant subdomain, an app-registration client ID and a scope name
are **public identifiers, not credentials** — they ship in the SPA bundle by
necessity. So they are plain env vars: **not** `@secure()`, **not** Container Apps
secrets, **not** Key Vault. Neither app registration has a client secret (the SPA
is a public client using PKCE; the API only validates tokens).

`Instance` and `Audience` are **composed in Bicep** rather than passed whole, so
the authority/audience format lives in one reviewable place. The composition is
guarded with `empty(...)` so a blank input stays blank — composing
unconditionally would turn an unset subdomain into the plausible-looking but
broken `https://.ciamlogin.com/`, hiding a misconfiguration behind a well-formed
URL.

> ### ⚠️ The empty-string defaults are authorable, not safe to deploy
> Approved deliberately, because the real values are produced by the §16.11
> portal bootstrap and cannot exist at authoring time. But `todo-api` validates
> all five `AzureAd__*` settings at startup and **fails fast** (§16.7.1), so
> **applying the template while they are blank rolls a `todo-api` revision that
> never becomes healthy.** Fill `main.parameters.json` in first. This is called
> out in the params file, the Bicep param descriptions, and `infra/README.md`.

---

## Validation result

Both dry-runs run against the live `rg-todo-demo` on 2026-08-12 as
`ing.david.mata.az@outlook.com`, subscription `9ba6633f-058c-4269-8b0f-086ab331ef26`.
**No `create`/`apply` command was run.**

**1. `az bicep build`** (local transpile, no Azure contact) — clean, zero
diagnostics.

**2. `az deployment group validate`** → `provisioningState: Succeeded`,
`error: null`, and the vault appears in `validatedResources`:
`/subscriptions/9ba6633f…/resourceGroups/rg-todo-demo/providers/Microsoft.KeyVault/vaults/kv-todo-3bkqjv23abtu`

**3. `az deployment group what-if`** → `Resource changes: 1 to create, 7 to
modify, 3 no change, 1 to ignore.` The intended change set:

```
+ Microsoft.KeyVault/vaults/kv-todo-3bkqjv23abtu [2023-07-01]
    properties.enableRbacAuthorization:   true
    properties.enableSoftDelete:          true
    properties.softDeleteRetentionInDays: 7
    properties.publicNetworkAccess:       "Enabled"
    properties.sku.name:                  "standard"
    (no enablePurgeProtection property -> off, as approved)

~ Microsoft.App/containerApps/todo-api
    + env 4: AzureAd__Instance      + env 7: AzureAd__Audience
    + env 5: AzureAd__TenantId      + env 8: AzureAd__Scopes = "Todos.ReadWrite"
    + env 6: AzureAd__ClientId
```

Note that four of the five new env vars print **with no `value:` line** — that is
what-if rendering the empty-string defaults, and it is the visible proof of the
hazard flagged above.

### Three things the dry-run exposed that a reviewer must see

These are **pre-existing**, not introduced by this cycle, but they are live
hazards for whoever applies the template.

1. **A plain full apply reverts BOTH apps to the placeholder image.**
   `todoApiImage`/`todoWebImage` default to
   `mcr.microsoft.com/k8se/quickstart:latest` and are not pinned in
   `main.parameters.json` (by design — CD sets real tags imperatively and never
   applies Bicep). what-if reports:
   ```
   ~ image: acrtododemo….azurecr.io/todo-api:d3266c07… => mcr.microsoft.com/k8se/quickstart:latest
   ~ image: acrtododemo….azurecr.io/todo-web:d3266c07… => mcr.microsoft.com/k8se/quickstart:latest
   ```
   i.e. **it takes the running application down and replaces it with the Azure
   quickstart page**, and drops the ACR `registries` block with it. Always pin the
   currently-running tags (`--parameters todoApiImage=… todoWebImage=…`). Now
   documented in `infra/README.md`.

2. **Live drift on `todo-api`'s DB auth.** what-if shows the deployed app still
   has `Postgres__UseEntraAuth: "false"` and still carries the
   `todo-db-connection` Container Apps secret, while `main.bicep` already encodes
   `true` + no secret. So the §13/§14 managed-identity cutover is **in the IaC but
   not on the running app**. A full apply would perform that cutover as a side
   effect of this cycle's deploy — which is exactly the outage scenario the Phase
   4 CUTOVER SEQUENCING callout warns about. **This is the strongest argument for
   using the imperative `az containerapp update` path below for this cycle.**

3. **`Microsoft.KeyVault` is NOT registered on this subscription**
   (`az provider show -n Microsoft.KeyVault` → `NotRegistered`). Provider
   registration is a **subscription-scope** operation, so the RG-scoped CD service
   principal (Contributor on `rg-todo-demo` only) **cannot** perform it. A
   subscription Owner must run `az provider register -n Microsoft.KeyVault` once,
   or the first Key Vault deploy fails with `MissingSubscriptionRegistration`.

---

## One-time identity setup required

**A human with sufficient AAD privileges runs these — never an agent, never
CI/CD.** Repo is `matvi/todo-claude-multiagent`; subscription
`9ba6633f-058c-4269-8b0f-086ab331ef26`, tenant `6ce2ff72-209a-447d-bf53-9579c52c03f5`.

### Already done in a previous cycle — verify, don't re-run

The GitHub↔Azure OIDC trust (`gh-todo-demo-cicd` app registration + federated
credentials for `main`, `environment:production`, `pull_request`), the RG-scoped
`Contributor` grant, the `AcrPush`/`AcrPull` grants and the `Monitoring Metrics
Publisher` grant are all in place from Phases 1–4 (`infra/README.md`). For
reference, the OIDC setup — **no client secret is ever created or stored**:

```bash
SUB_ID="9ba6633f-058c-4269-8b0f-086ab331ef26"
TENANT_ID="6ce2ff72-209a-447d-bf53-9579c52c03f5"
RG="rg-todo-demo"; GH_ORG_REPO="matvi/todo-claude-multiagent"

APP_ID=$(az ad app create --display-name "gh-todo-demo-cicd" --query appId -o tsv)
az ad sp create --id "$APP_ID"

# GitHub's immutable subject format embeds numeric owner/repo IDs.
OWNER_ID=$(gh api "users/matvi" --jq .id)
REPO_ID=$(gh api "repos/$GH_ORG_REPO" --jq .id)
GH_SUBJECT_REPO="matvi@${OWNER_ID}/todo-claude-multiagent@${REPO_ID}"

az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]}'
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':environment:production",
  "audiences": ["api://AzureADTokenExchange"]}'

az role assignment create --assignee "$APP_ID" --role Contributor \
  --scope "/subscriptions/$SUB_ID/resourceGroups/$RG"
```

**GitHub repository VARIABLES** (not secrets — with OIDC **no credential material
is stored in GitHub at all**; these are public identifiers):

```bash
gh variable set AZURE_CLIENT_ID       --body "$APP_ID"
gh variable set AZURE_TENANT_ID       --body "6ce2ff72-209a-447d-bf53-9579c52c03f5"
gh variable set AZURE_SUBSCRIPTION_ID --body "9ba6633f-058c-4269-8b0f-086ab331ef26"
```

The one repo **secret** is `PGADMIN_PASSWORD` (feeds the `@secure()`
`postgresAdminPassword`), also already set.

### NEW this cycle — must be run before/with the deploy

```bash
# 0. Register the Key Vault RP (subscription Owner; verified NotRegistered).
az provider register -n Microsoft.KeyVault
az provider show -n Microsoft.KeyVault --query registrationState -o tsv   # wait for "Registered"

# 1. Grant YOURSELF (a human) data-plane access to the vault, AFTER it exists.
#    Agents never create role assignments (§11.6). Needs User Access Admin/Owner.
KV_NAME=$(az deployment group show -g rg-todo-demo -n main \
  --query properties.outputs.keyVaultName.value -o tsv)
KV_ID=$(az keyvault show -n "$KV_NAME" -g rg-todo-demo --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee "$ME" \
  --role "Key Vault Secrets Officer" --scope "$KV_ID"

# 2. Custody the Google client secret. Human only — never an agent, never CI/CD.
az keyvault secret set --vault-name "$KV_NAME" \
  --name google-oauth-client-secret --value "<paste from Google Cloud console>"
```

**Do NOT grant `todo-api` or `todo-web` any role on this vault.**

### Human-only, cannot be automated at all (specs §16.11 / §16.13)

Create the **External ID tenant** (subscription-linked, **Core**, **United
States** — region is immutable), the `todo-api-app` and `todo-web-spa` app
registrations, the exposed scope / App ID URI / `accessTokenAcceptedVersion`, the
`SignUpSignIn` user flow, the `phoneNumber` custom attribute, the **Google Cloud
OAuth client**, the Google IdP federation, optional claims, admin consent, and the
Postgres bootstrap-admin promotion. **No agent and no service principal can do any
of this.** Record the resulting subdomain, tenant GUID and API client ID — the
next section needs them.

---

## To actually deploy the infra itself

**You run these. I do not.** Two paths, and for this cycle they are **both**
needed — the Bicep creates the vault, and the imperative update is what actually
puts the env vars on the running app, because **CD does not apply Bicep**.

### Path A — apply the Bicep (creates the Key Vault)

```bash
# Pin the currently-running images, or the apply reverts both apps to the
# mcr.microsoft.com/k8se/quickstart placeholder (see Validation result #1).
CUR_API=$(az containerapp show -g rg-todo-demo -n todo-api \
  --query "properties.template.containers[0].image" -o tsv)
CUR_WEB=$(az containerapp show -g rg-todo-demo -n todo-web \
  --query "properties.template.containers[0].image" -o tsv)

read -rs PGPWD; export PGPWD    # same value as the PGADMIN_PASSWORD repo secret

# ALWAYS dry-run first and read the `image:` and `AzureAd__*` lines.
az deployment group what-if \
  --resource-group rg-todo-demo \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD" \
  --parameters todoApiImage="$CUR_API" todoWebImage="$CUR_WEB"

az deployment group create \
  --resource-group rg-todo-demo \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD" \
  --parameters todoApiImage="$CUR_API" todoWebImage="$CUR_WEB"
```

> **Before running the `create`:** either fill the three blank
> `externalTenantSubdomain` / `externalTenantId` / `apiClientId` values into
> `infra/main.parameters.json`, **or** accept that this apply sets the five
> `AzureAd__*` env vars to empty strings and breaks `todo-api` startup once the
> auth-aware image is running. Also re-read Validation result #2 — a full apply
> performs the §13/§14 DB-auth cutover on the live app as a side effect.
>
> **If you only want the Key Vault right now** and nothing else touched, that is
> the safest first move — but note Bicep has no partial-apply; use Path B for the
> env vars and apply the full template only once the params are filled in.

### Path B — the imperative env-var update (REQUIRED; `cd.yml` never applies Bicep)

Apply **before or with** the image that needs them: `todo-api` fails fast at
startup if any of the five is missing (§16.7.1), so a mis-ordered deploy produces
a revision that never becomes healthy.

```bash
RG=rg-todo-demo; APP=todo-api
EXT_SUBDOMAIN="<tenant-subdomain>"      # from <subdomain>.ciamlogin.com
EXT_TENANT_ID="<external-tenant-guid>"
API_CLIENT_ID="<todo-api-app client id>"

# Capture the rollback target BEFORE changing anything.
PREV_REV=$(az containerapp revision list -g $RG -n $APP \
  --query "[?properties.active]|[0].name" -o tsv); echo "rollback target: $PREV_REV"

az containerapp update -g $RG -n $APP --set-env-vars \
  "AzureAd__Instance=https://${EXT_SUBDOMAIN}.ciamlogin.com/" \
  "AzureAd__TenantId=${EXT_TENANT_ID}" \
  "AzureAd__ClientId=${API_CLIENT_ID}" \
  "AzureAd__Audience=api://${API_CLIENT_ID}" \
  "AzureAd__Scopes=Todos.ReadWrite"
```

**Rollback** (if the new revision misbehaves):

```bash
az containerapp revision activate -g rg-todo-demo -n todo-api --revision "$PREV_REV"
```

Then backfill the same three values into `infra/main.parameters.json` so the next
full apply does not wipe what Path B set.

---

## Estimated cost

**Incremental cost of this entire cycle: ≈ $0.00/month.** Rough estimate, US list
prices as of 2026-08, region- and usage-dependent — verify against the Azure
pricing calculator.

| Item | Est. monthly | Basis |
|---|---|---|
| Key Vault Standard | **~$0.00** | $0.03 per 10,000 operations; this vault sees a handful of operations **per year** (written once by a human, never read by an app) |
| Entra External ID (Core) | **$0.00** | First 50,000 MAU free; this demo is single-digit MAU. Beyond that band ~$0.03/MAU (Core) vs ~$0.23/MAU (Premium) |
| `todo-api` env vars | $0.00 | Config only — no SKU, scale or replica change |
| Postgres / ACR / ACA / Log Analytics / App Insights | $0.00 delta | Untouched. Slight extra trace volume from the new endpoints, well inside demo noise under the 1 GB/day cap |
| *(rejected)* Key Vault private endpoint | *+~$7.30* | Not provisioned — no VNet exists (§8), and it would be the largest single line item in the demo |
| *(rejected)* Managed HSM | *+~$3,000–3,700* | Not provisioned — listed only to close the option space |

---

## CI/CD (PIPELINE-APPLY, 2026-08-13)

| File | Role | Triggers |
|---|---|---|
| `.github/workflows/ci.yml` | **CI** — build/test/lint validation only. No Azure login, no push, no deploy. | **Auto**: every `push` and every `pull_request`, **any branch**, + `workflow_dispatch`. |
| `.github/workflows/cd.yml` | **CD** — build/push images, roll both Container Apps. | **Auto**: `pull_request` `types: [closed]` on `branches: [main]`, job-gated by `if: github.event.pull_request.merged == true`. Nothing else. |
| `azure-pipelines.yml` | Azure DevOps equivalent of both stages, pointed at `matvi/todo-claude-multiagent`. | **Manual-only** — `trigger: none`, `pr: none`. |

**Only GitHub Actions auto-triggers.** `azure-pipelines.yml` is kept
behaviour-equivalent and ready to flip on (the exact 3-step switchover is in its
header comment, including "disable the GitHub Actions workflows in the same
change"). Both auto-triggering would double every build and, worse, race two
deploys onto the same Container App revision.

### The CD gate, confirmed

`cd.yml` deploys **only** on `pull_request` `closed` into `main` **with**
`merged == true`. Consequences, all intended:

- Closing a PR **without** merging deploys nothing (the job `if` is false).
- A direct push to `main` deploys nothing — which is only possible at all while
  branch protection is off, and is exactly why the check below matters.
- `workflow_dispatch` was **removed** from `cd.yml` (it was there before). There
  is now no way to deploy from GitHub without a merged PR. Out-of-band redeploys
  and rollbacks are the documented `az containerapp update` / `az containerapp
  revision activate` commands a human runs — already in this file.
- **Image tag = `github.event.pull_request.merge_commit_sha`**, not `github.sha`.
  On a `pull_request` event `github.sha` is the ephemeral test-merge commit,
  which is not what landed on `main`; checkout pins the same sha.
- **OIDC still works across this trigger change.** The job declares
  `environment: production`, so the token subject is
  `repo:matvi/todo-claude-multiagent:environment:production` — the
  `gh-todo-demo-env-production` federated credential already created in Phase 1
  (see "One-time identity setup"). No new credential, no stored secret. The
  `ref:refs/heads/main` credential is now unused by CD but harmless.

### The four frontend build args (specs §16.12 item 4 — the engineer's open item)

`frontend/Dockerfile` already declares `ARG`/`ENV` for
`VITE_ENTRA_TENANT_SUBDOMAIN`, `VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_SPA_CLIENT_ID`
and `VITE_API_SCOPE` (engineer, §16.12 item 3). Until now nothing passed them, so
a deployed SPA would render the "sign-in is not configured" screen
(`frontend/src/authConfig.ts` → `isAuthConfigured`). Now wired:

| Where | Source | Value |
|---|---|---|
| `cd.yml` frontend build | GitHub repository **variables** | `vars.ENTRA_TENANT_SUBDOMAIN`, `vars.ENTRA_TENANT_ID`, `vars.ENTRA_SPA_CLIENT_ID`; `VITE_API_SCOPE` is **composed** as `api://${ENTRA_API_CLIENT_ID}/${API_SCOPE_NAME}` |
| `ci.yml` frontend `npm run build` **and** docker build | workflow `env:` constants | dummy but syntactically valid (`ci-placeholder`, all-zero / all-one GUIDs) — sanctioned by `infra-proposal.md` and specs §16.12 item 4 |
| `azure-pipelines.yml` CI stage | `ci*` pipeline variables | same dummies |
| `azure-pipelines.yml` CD stage | plain (non-secret) pipeline variables of the same four names | same composition |

`API_SCOPE_NAME` defaults to `Todos.ReadWrite` and is overridable via a
`ENTRA_API_SCOPE_NAME` repo variable; it must stay equal to `apiScopeName` in
`infra/main.parameters.json` and to `todo-api`'s `AzureAd__Scopes`.

**These are repository VARIABLES, never secrets.** A tenant subdomain, a tenant
GUID, a public-client id and a scope string are published in the SPA bundle by
necessity; the SPA is a public client using PKCE and has no client secret at all.
Marking them secret would only make CI logs harder to read while protecting
nothing. Set them once, after the §16.11 human bootstrap:

```bash
gh variable set ENTRA_TENANT_SUBDOMAIN --body "<subdomain>"     # from <subdomain>.ciamlogin.com
gh variable set ENTRA_TENANT_ID        --body "<external-tenant-guid>"
gh variable set ENTRA_SPA_CLIENT_ID    --body "<todo-web-spa client id>"
gh variable set ENTRA_API_CLIENT_ID    --body "<todo-api-app client id>"
```

### Preflights — CD refuses to ship a known-broken deploy

Two read-only checks run before anything is built or rolled, mirrored in
`azure-pipelines.yml`:

1. **The four repo variables are set.** Missing ⇒ hard fail naming them, instead
   of silently shipping an SPA that shows "sign-in is not configured".
2. **`todo-api` already carries all five non-blank `AzureAd__*` env vars**
   (`az containerapp show`, read-only). Missing ⇒ hard fail pointing at *Path B*
   below. **CD does not set them itself** and must not: they come from the
   human-run §16.11 tenant bootstrap, CD never applies Bicep (§16.12 item 6), and
   `todo-api` fails fast at startup without all five (§16.7.1) — so deploying
   into that state would roll a revision that never becomes healthy.

So the answer to "who runs the imperative env-var step, and when": **a human,
before or with the first CD run that ships the auth-aware backend image.** Path A
(Bicep, creates the Key Vault) and Path B (`az containerapp update
--set-env-vars`) are both still required and both still human-run; CD only
enforces that Path B has happened.

CD also now captures both apps' active revision names **before** rolling and
prints the paired rollback commands into the job summary — backend and frontend
must roll back together (§16.16): the moment the auth-aware backend is live, an
older frontend image 401s on every call. The summary also reminds the operator
that `https://<todo-web FQDN>` must be a registered SPA redirect URI on
`todo-web-spa`, or sign-in fails with `AADSTS50011` — nothing in CD can do that.

### The commands in CI are the ones that actually ran

Taken verbatim from `.pipeline/tests.md` §6 / `.pipeline/changes.md` — nothing
invented:

```bash
# backend  (164/164 passing)
dotnet restore TodoApi.sln
dotnet build   TodoApi.sln -c Release --no-restore
dotnet test    TodoApi.sln -c Release --no-build

# frontend (60/60 passing)
npm ci && npm run build && npm test      # build = tsc && vite build, test = vitest run
```

**There is deliberately no frontend lint step**: `frontend/package.json` defines
no `lint` script, and the 2026-07-24 CI-readiness pass in `changes.md` recorded
that decision explicitly. Inventing one here would be a command nobody has ever
run. Type-checking is still enforced, because `npm run build` is `tsc && vite
build`. Likewise **no `npm audit` gate** — the known esbuild/vite-5 dev-toolchain
advisories would fail a naive `--audit-level=high` gate while affecting only the
dev server, never the nginx-served bundle.

The `iac` job (`az bicep build` + `az bicep lint`) is unchanged and needs no
Azure credentials.

**CI trigger note:** a PR branch inside this repo fires both a `push` run and a
`pull_request` run for the same commit. The `concurrency` key includes
`github.event_name` on purpose so the two do not cancel each other — a cancelled
run would surface as a cancelled *required status check* on the PR. Both runs are
credential-free validation, so the duplicate costs runner minutes and nothing else.

---

## Branch protection status on `main`

**UNKNOWN — could not be verified, and that is a finding, not a formality.**

```
$ gh api repos/matvi/todo-claude-multiagent/branches/main/protection
To get started with GitHub CLI, please run:  gh auth login
```

`gh` (v2.97.0) is installed but not authenticated on this machine, and the check
requires an authenticated token even to read. I did not authenticate — that is a
credential action for the human, not an agent. Re-run it yourself:

```bash
gh auth login
gh api repos/matvi/todo-claude-multiagent/branches/main/protection   # read-only
```

`404 Branch not protected` means it is **off**.

**If it is off, this pipeline's approval gate does not exist.** Every safeguard
described above — CD only on a merged PR, a human reviewing before merge — is
just a convention the agents happen to follow: anyone (including a workflow with
`contents: write`) could push straight to `main`. Turn it on:

1. Settings → Branches → Add branch protection rule, pattern `main`.
2. **Require a pull request before merging**, with **≥ 1 approval**.
3. **Require status checks to pass**, and select the CI checks by name:
   `Backend (.NET 10 build + test)`, `Frontend (Vite build + vitest)`,
   `Docker build (both images, no push)`, `IaC (bicep build + lint)`.
   Tick *Require branches to be up to date before merging*.
4. **Do not allow bypassing the above settings** (i.e. no admin bypass).
5. Leave force-push and branch deletion disabled.

Optionally also add a required reviewer on the `production` GitHub Environment —
`cd.yml` already targets that environment, so it can be added without touching
the workflow file.

---

## Drift notes (PIPELINE-APPLY step 2)

**No architecture-level drift found.** Everything `changes.md` implements maps
onto what specs §16 called for and what the early pass scaffolded: one Key Vault,
five plain env vars on an existing Container App, build-arg plumbing, and an
additive EF migration applied by the existing startup `Migrate()`. No new Azure
service, no SKU or scale change, no new secret. Specs §16 targets **Azure
Container Apps**, which is what `cd.yml` deploys to — no target mismatch.
`frontend/nginx.conf` already has the SPA fallback (`try_files … /index.html`)
that the new client-side `/admin` route needs, so no infra change there either.

Four smaller things a reviewer should still see:

1. **`infra/main.bicep` still sets `Postgres__UseEntraAuth=true`, which the
   application deleted in §14.** It is dead config, not a live defect (a test
   pins that the app ignores it). It was **left alone deliberately**: specs
   §16.12 item 2 says the `pipeline/dev-environment` branch removes it and that
   this cycle must not duplicate or revert that change. Flagging it so it is not
   forgotten if that branch is abandoned.
2. **`main.parameters.json` still has three blank identity params**
   (`externalTenantSubdomain`, `externalTenantId`, `apiClientId`) — unavoidable
   until the human §16.11 bootstrap produces them, and already flagged in the
   early pass. The same values become the GitHub repository variables above; fill
   both, or a later full Bicep apply will wipe what Path B set.
3. **`Microsoft.KeyVault` is still `NotRegistered` on the subscription**
   (early-pass finding #3). A subscription Owner must
   `az provider register -n Microsoft.KeyVault` or the first Key Vault deploy
   fails; the RG-scoped CD principal cannot do it.
4. **Live drift on `todo-api` unrelated to this cycle** (early-pass finding #2):
   the running app still has the old `todo-db-connection` secret and
   `Postgres__UseEntraAuth=false`, while the IaC already encodes the §13/§14
   cutover. Unchanged by this pass, and another reason a full Bicep apply is not
   a routine operation here.

Nothing in this pass edited `infra/`; the early pass's validation result still
stands unmodified.
