# Todo app — infrastructure (`infra/`)

Bicep Infrastructure-as-Code for the Todo demo. **Resource-group scoped**:
`main.bicep` deploys into an existing `rg-todo-demo`; it does **not** create the
resource group (the human creates it during bootstrap — see below).

Approved SKU decisions (user, 2026-07-24):
Postgres **Standard_B1ms** · ACA **Consumption** · ACR **Basic** ·
`todo-web` **min 1 / max 2** · `todo-api` **min 0 / max 3** · 0.25 vCPU / 0.5 GiB each.

## Files

| Path | Provisions |
|---|---|
| `main.bicep` | RG-scoped entry point; wires the modules; assembles the DB connection string as a backend Container Apps secret; emits outputs. |
| `main.parameters.json` | Dev/demo **non-secret** parameter values. The `@secure()` `postgresAdminPassword` is **not** here — it is supplied at deploy time from a secret. |
| `modules/registry.bicep` | Azure Container Registry (Basic, admin user disabled). |
| `modules/loganalytics.bicep` | Log Analytics workspace (PerGB2018, 30-day retention, **1 GB/day ingestion cap** — cost circuit-breaker). |
| `modules/appinsights.bicep` | **Application Insights** component (workspace-based, reuses `log-todo-demo`; `DisableLocalAuth: true`). Outputs the connection string. |
| `modules/acaEnvironment.bicep` | Container Apps managed environment (Consumption). |
| `modules/postgres.bicep` | Postgres Flexible Server (Burstable B1ms, PG16, 32 GiB, no HA, LRS backup) + `tododb` database + "allow Azure services" firewall rule + **dual-auth (`activeDirectoryAuth` + `passwordAuth`) and the Entra `administrators` child resource**. |
| `modules/containerApp.bicep` | Reusable single Container App (system-assigned identity, ingress, secrets, scale). Used for `todo-api` and `todo-web`. |

## Image / ordering model

`main.bicep`'s `todoApiImage` / `todoWebImage` default to a **public placeholder**
(`mcr.microsoft.com/k8se/quickstart:latest`) so the one-time bootstrap deploy
(Phase 2 below) succeeds before any app image exists in ACR.

**CD does not deploy Bicep at all.** Infra is provisioned once, by hand (Phases
1–3 below), and only re-applied manually — via `az deployment group create`, see
"Deploy the infra yourself" below — if the infra itself actually changes (a SKU,
a scale setting, a new resource). Every `cd.yml` / `azure-pipelines.yml` run just
looks up the already-existing ACR (`az acr list -g rg-todo-demo`), builds the real
images, and imperatively updates the two Container Apps in place, honoring the
backend-before-frontend ordering (the frontend bakes `VITE_API_BASE_URL=<backend
FQDN>` at build time). This is approach **(a)** from specs §11.5, minus the
Bicep-on-every-deploy part — running the same infra deploy on every merge added no
value once the apps exist, and it was also the thing that made the bootstrap
deploy fragile (see the "bootstrap deploy fixes" and "OIDC immutable subject
claims" commits for what that fragility actually looked like in practice).

---

# One-time human bootstrap (MANUAL — run once, by a human)

> **No agent runs any command in this section.** These require an Entra admin
> (Owner, or `User Access Administrator` + `Contributor`) on the subscription.
> This block is the **authoritative** version and supersedes the draft in
> specs §11.6. Differences from that draft, all deliberate:
> 1. adds `az group create rg-todo-demo` (Bicep is RG-scoped and will not create the RG);
> 2. adds federated credentials for the PR and `production`-environment subjects, not just `main`;
> 3. moves the ACR role grants (`AcrPush` for CI, `AcrPull` for each app identity)
>    to **after** the first infra deploy, because ACR and the app managed
>    identities do not exist until then — the draft granted `AcrPush` before ACR existed;
> 4. adds the `AcrPull` grants for each Container App's system-assigned managed
>    identity — these are excluded from the repeatable CD Bicep on purpose (the
>    CD service principal only holds `Contributor` and cannot write role assignments).
> 5. uses GitHub's **immutable OIDC subject claim** format
>    (`repo:<owner>@<ownerId>/<repo>@<repoId>:...`), not the legacy name-only
>    format (`repo:<owner>/<repo>:...`). GitHub switched new repositories to
>    the immutable format by default; a federated credential created with the
>    legacy format will fail Azure login with `AADSTS700213: No matching
>    federated identity record found`, because Azure matches the subject as an
>    exact string. See step 3 below for how to get the numeric IDs.

Set these shell variables once:

```bash
SUB_ID="<your-subscription-id>"
TENANT_ID="<your-tenant-id>"
RG="rg-todo-demo"
LOCATION="eastus"
GH_ORG_REPO="<org>/<repo>"    # e.g. dmata/todo-app
```

### Phase 1 — resource group + secretless CI/CD identity (OIDC)

```bash
# 1. Create the resource group (Bicep is RG-scoped and does NOT create it).
az group create --name "$RG" --location "$LOCATION"

# 2. Entra app registration + service principal for CI/CD.
APP_ID=$(az ad app create --display-name "gh-todo-demo-cicd" --query appId -o tsv)
az ad sp create --id "$APP_ID"

# 3. Federated credentials — one subject per trust context. No client secret.
#    GitHub's newer repos use the IMMUTABLE subject format, which embeds the
#    numeric owner/repo IDs: repo:<owner>@<ownerId>/<repo>@<repoId>:...
#    Get those IDs (needs `gh` with repo read access, or use the GitHub API/UI):
OWNER_ID=$(gh api "users/$(echo "$GH_ORG_REPO" | cut -d/ -f1)" --jq .id)
REPO_ID=$(gh api "repos/$GH_ORG_REPO" --jq .id)
GH_SUBJECT_REPO="$(echo "$GH_ORG_REPO" | cut -d/ -f1)@${OWNER_ID}/$(echo "$GH_ORG_REPO" | cut -d/ -f2)@${REPO_ID}"
# If unsure whether your repo is on the legacy or immutable format, the fastest
# check is to just trigger cd.yml once — a subject mismatch fails fast at the
# azure/login step and the error message includes the EXACT subject GitHub
# presented, which you can paste directly into the federated-credential below.

#    (a) deploys from main
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
#    (b) the `production` GitHub Environment used by cd.yml
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
#    (c) OPTIONAL — only if you later enable what-if on PRs in ci.yml
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-pr",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_SUBJECT_REPO"':pull_request",
  "audiences": ["api://AzureADTokenExchange"]
}'

# 4. Least-privilege RBAC: Contributor on the RESOURCE GROUP only (not the sub).
az role assignment create --assignee "$APP_ID" --role Contributor \
  --scope "/subscriptions/$SUB_ID/resourceGroups/$RG"

# 5. GitHub repo VARIABLES (not secrets — OIDC stores no credential material).
gh variable set AZURE_CLIENT_ID       --body "$APP_ID"
gh variable set AZURE_TENANT_ID       --body "$TENANT_ID"
gh variable set AZURE_SUBSCRIPTION_ID --body "$SUB_ID"

# 6. GitHub repo SECRET: the Postgres admin password (feeds the @secure() param).
gh secret set PGADMIN_PASSWORD        # prompts for the value
```

### Phase 2 — first infra deploy (creates ACR + app managed identities)

The ACR and the Container Apps' system-assigned identities do not exist until the
template is deployed once. Run it manually the first time (subsequent deploys are
handled by CD):

```bash
read -rs PGPWD; export PGPWD   # type the same value you set as PGADMIN_PASSWORD
az deployment group create \
  --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD"
```

Capture the outputs you'll need for Phase 3:

```bash
ACR_NAME=$(az deployment group show -g "$RG" -n main --query properties.outputs.acrName.value -o tsv)
API_PID=$(az deployment group show -g "$RG" -n main --query properties.outputs.todoApiPrincipalId.value -o tsv)
WEB_PID=$(az deployment group show -g "$RG" -n main --query properties.outputs.todoWebPrincipalId.value -o tsv)
ACR_ID=$(az acr show -n "$ACR_NAME" -g "$RG" --query id -o tsv)
```

### Phase 3 — ACR role grants (require ACR + identities from Phase 2)

These are **excluded from the repeatable CD Bicep** on purpose (a `Contributor`
principal cannot create role assignments). A human with `User Access
Administrator`/`Owner` runs them once:

```bash
# CI/CD service principal needs to PUSH images (az acr build).
az role assignment create --assignee "$APP_ID" --role AcrPush --scope "$ACR_ID"

# Each Container App's managed identity needs to PULL images.
az role assignment create --assignee "$API_PID" --role AcrPull --scope "$ACR_ID"
az role assignment create --assignee "$WEB_PID" --role AcrPull --scope "$ACR_ID"
```

After Phase 3, the CD workflow (`.github/workflows/cd.yml`) can build/push real
images and roll the apps on every merge to `main` with zero stored Azure secrets.

### Phase 4 — App Insights + managed-identity Postgres auth (this cycle, `pipeline/appinsights-tracing`)

> **Run once, by a human. No agent runs any command here.** These extend the
> Phase 1–3 bootstrap and follow the same "manual, human-run" convention as the
> OIDC/RBAC and `AcrPull` grants. They cover the observability + managed-identity
> cutover (specs §12 / §13) approved by the user on 2026-07-28. SKU/decisions
> baked into the Bicep this cycle:
> - **App Insights** `appi-todo-demo`: workspace-based (reuses `log-todo-demo`),
>   `DisableLocalAuth: true`, no independent SKU/fee.
> - **Sampling: 100%** — this is the backend OTel exporter default
>   (`AzureMonitorOptions.SamplingRatio = 1.0`), a code-level setting, **not** an
>   ARM property. Nothing to set on the component; see the note in
>   `modules/appinsights.bicep`.
> - **Postgres:** dual-auth (`activeDirectoryAuth: Enabled` + `passwordAuth:
>   Enabled` — password kept as break-glass), Entra admin set to the deploying
>   user (baked into `main.parameters.json`, non-secret identity metadata).
> - **Role grant** (`Monitoring Metrics Publisher`): a **manual** grant here
>   (step C below), NOT in Bicep — same reason as the `AcrPull` grants (a
>   `Contributor` principal cannot write role assignments).
> - **Log Analytics daily cap: 1 GB/day** (`workspaceCapping.dailyQuotaGb: 1`) —
>   a hard cost circuit-breaker (user override of the "no cap" default).
>   **Tradeoff:** once 1 GB is ingested on a given UTC day, further telemetry is
>   **silently dropped** until the next day.

> ## ⚠️ CUTOVER SEQUENCING — READ BEFORE APPLYING (live, already-deployed app)
> `main` is a running production app on Azure Container Apps. Getting the order
> wrong causes a **DB connectivity outage** (the backend runs `Migrate()` at
> startup, so a cold-started replica can fail to start). Apply strictly in this
> order — do NOT flip `todo-api`'s env vars first:
>
> 1. **Deploy the Postgres server change first** (enable Entra auth + set the
>    Entra admin). This is idempotent and does not disturb the still-running
>    password path. → deploy the updated Bicep (see "Deploy the infra yourself"),
>    OR do steps 1–3 as a targeted change before the app revision flips in 4.
> 2. **Create the in-DB principal for todo-api's managed identity** and grant it
>    schema/DDL rights (step B below). Until this exists, an Entra-token login for
>    `todo-api` will FAIL.
> 3. **Grant `Monitoring Metrics Publisher`** to the todo-api MI on the AI
>    component (step C below) and provision the AI component (`DisableLocalAuth:
>    true`). Do this before the app starts publishing telemetry, or ingestion
>    404/403s silently (app still runs; telemetry is just lost until the grant
>    propagates — a few minutes).
> 4. **ONLY THEN** roll `todo-api` with the new env (a single revision flip is
>    cleanest): `Postgres__UseEntraAuth=true`, the passwordless
>    `ConnectionStrings__TodoDb`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, and the
>    `todo-db-connection` secret DROPPED. Because the Bicep now embeds all of
>    step 4 (env vars + no secret), applying the full template performs steps 1
>    and 4 together — so **run steps 2 and 3 before you apply the full template**,
>    or split the deploy (server first, app last).
>
> Doing step 4 before step 2 is the concrete outage scenario. The passwordless
> `Username=` (`todo-api`) MUST exactly match the principal name created in step B.

Set these shell variables (continuing from Phases 1–3):

```bash
RG="rg-todo-demo"
PG_SERVER="pg-todo-demo-cus01"                     # immutable, matches main.parameters.json
PG_FQDN="${PG_SERVER}.postgres.database.azure.com"
MI_NAME="todo-api"                                 # todo-api's system-assigned MI name == the Container App name == the Postgres role == the Username= in the conn string
```

**(A) Postgres Entra admin — already covered by Bicep.** The Entra admin
(`objectId ee31140f-3164-439c-8fa9-9f7e5dbd1b2c`, UPN
`ing.david.mata.az_outlook.com#EXT#@ingdavidmataazoutlook.onmicrosoft.com`,
type `User`, tenant `6ce2ff72-209a-447d-bf53-9579c52c03f5`) and dual-auth are set
by `modules/postgres.bicep` (`authConfig` + the `administrators` child resource),
with the values baked into `main.parameters.json`. No separate `az` command is
needed for this — it happens when you deploy the template (cutover step 1). These
are non-secret (an object ID and a UPN are not credentials).

**(B) Create the managed identity's Postgres role + grant it schema/DDL rights
(cutover step 2 — cannot be done in Bicep; runs INSIDE the database).** Connect to
the **`postgres`** database **as the Entra administrator** (the user in A —
authenticate with an Entra access token as the password, `Ssl Mode=Require`), then:

```sql
-- Connected to the `postgres` database, AS the Entra admin:
-- creates a Postgres role backed by todo-api's system-assigned managed identity.
select * from pgaadauth_create_principal('todo-api', false, false);
--   arg1 = the MI name (must equal Username= in ConnectionStrings__TodoDb)
--   arg2 isAdmin = false, arg3 isMfa = false

-- Then connect to `tododb` (still as the Entra admin) and grant the new role what
-- the startup Migrate() needs (CONNECT + schema DDL/DML). Schema ownership is the
-- simplest path for the demo's auto-migrate-at-startup:
\c tododb
GRANT CONNECT ON DATABASE tododb TO "todo-api";
GRANT ALL ON SCHEMA public TO "todo-api";
ALTER SCHEMA public OWNER TO "todo-api";
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO "todo-api";
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO "todo-api";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO "todo-api";
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO "todo-api";
```

One documented source of truth for the MI name: it is **`todo-api`** (the Container
App name == its system-assigned identity name == the Postgres role == the
`Username=` in the passwordless connection string). A mismatch fails auth.

**(C) Grant `Monitoring Metrics Publisher` to the todo-api MI on the AI component
(cutover step 3 — manual, NOT in Bicep).** A human with `User Access
Administrator`/`Owner` runs this once, after the AI component + the todo-api
identity exist:

```bash
# The todo-api MI principalId and the AI component id are template outputs:
API_PID=$(az deployment group show -g "$RG" -n main --query properties.outputs.todoApiPrincipalId.value -o tsv)
APPI_ID=$(az deployment group show -g "$RG" -n main --query properties.outputs.appInsightsId.value -o tsv)

az role assignment create \
  --assignee "$API_PID" \
  --role "Monitoring Metrics Publisher" \
  --scope "$APPI_ID"
```

Because `DisableLocalAuth: true` is set on the component, this grant is what lets
`todo-api` publish telemetry at all — without it, ingestion is rejected (403) and
traces silently never land (the app itself keeps running).

### Branch protection on `main` (repo-admin action — human)

Settings → Branches → add a rule for `main`:
- Require a pull request before merging; require ≥ 1 approval; dismiss stale approvals.
- Require the **CI** workflow status check to pass; require branch up to date.
- Disallow force-pushes and deletion; do not allow admins to bypass.
- Squash merge preferred.

Without this, the human-reviews-the-PR gate this pipeline relies on is only a
convention, not enforced by GitHub.

---

# Deploy the infra yourself (human, ad-hoc)

> **⚠️ First time applying the `pipeline/appinsights-tracing` changes on the live
> app?** A single full-template apply performs BOTH the Postgres server change
> (cutover step 1) AND the todo-api env flip to Entra auth (cutover step 4) in one
> shot. If the in-DB principal (Phase 4 step B) and the AI role grant (Phase 4
> step C) are NOT done first, todo-api loses DB connectivity. Do Phase 4 steps
> B and C BEFORE the first full apply — see the **CUTOVER SEQUENCING** callout in
> Phase 4. Steady-state (once bootstrapped) this caveat no longer applies.

```bash
read -rs PGPWD; export PGPWD
az deployment group create \
  --resource-group rg-todo-demo \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD"

# Dry-run alternatives (no changes applied):
az deployment group validate --resource-group rg-todo-demo \
  --template-file infra/main.bicep --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD"
az deployment group what-if --resource-group rg-todo-demo \
  --template-file infra/main.bicep --parameters infra/main.parameters.json \
  --parameters postgresAdminPassword="$PGPWD"
```
