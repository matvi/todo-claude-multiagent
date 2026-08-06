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
| `main.bicep` | RG-scoped entry point; wires the modules; assembles the **passwordless, non-secret** DB connection string as a **plain env var** on `todo-api` (Entra-only app auth, specs §14 — it is *not* a Container Apps secret and there is no `todo-db-connection` secret); emits outputs. |
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

### Phase 4 — Entra-ONLY Postgres auth for the app (+ App Insights) — cutover runbook

> **Run once, by a human. No agent runs any command here.** These extend the
> Phase 1–3 bootstrap and follow the same "manual, human-run" convention as the
> OIDC/RBAC and `AcrPull` grants. They cover the observability + managed-identity
> work (specs §12 / §13, approved 2026-07-28) and its completion as
> **Entra-only application DB auth** (specs §14, approved 2026-08-06).
> SKU/decisions baked into the Bicep:
> - **App Insights** `appi-todo-demo`: workspace-based (reuses `log-todo-demo`),
>   `DisableLocalAuth: true`, no independent SKU/fee.
> - **Sampling: 100%** — this is the backend OTel exporter default
>   (`AzureMonitorOptions.SamplingRatio = 1.0`), a code-level setting, **not** an
>   ARM property. Nothing to set on the component; see the note in
>   `modules/appinsights.bicep`.
> - **Postgres:** dual-auth (`activeDirectoryAuth: Enabled` + `passwordAuth:
>   Enabled`), Entra admin set to the deploying user (baked into
>   `main.parameters.json`, non-secret identity metadata).
>   **`passwordAuth: Enabled` and the `todoadmin` login are KEPT ON PURPOSE — do
>   not "helpfully" disable them.** Specs §14.1/§14.12: "Entra-only" is a property
>   of **the application's connection**, not of the server. The human operator
>   still needs password `psql`/admin access for the step B SQL below, for
>   break-glass, and for the recovery path in step D. Disabling password auth or
>   removing `postgresAdminUser` / the `@secure() postgresAdminPassword` param /
>   the `PGADMIN_PASSWORD` CI secret is explicitly out of scope and would break
>   the runbook.
> - **Role grant** (`Monitoring Metrics Publisher`): a **manual** grant here
>   (step C below), NOT in Bicep — same reason as the `AcrPull` grants (a
>   `Contributor` principal cannot write role assignments).
> - **Log Analytics daily cap: 1 GB/day** (`workspaceCapping.dailyQuotaGb: 1`) —
>   a hard cost circuit-breaker (user override of the "no cap" default).
>   **Tradeoff:** once 1 GB is ingested on a given UTC day, further telemetry is
>   **silently dropped** until the next day.

> ## ⚠️ CUTOVER SEQUENCING — READ BEFORE RUNNING ANYTHING (live, already-deployed app)
>
> `main` is a running production app on Azure Container Apps. Getting the order
> wrong causes a **DB connectivity outage** (the backend runs `Migrate()` at
> startup, so a cold-started replica can fail to start). Two facts drive the whole
> runbook:
>
> - **The app has NO password path any more** (specs §14.3). There is no
>   `Postgres__UseEntraAuth` flag to flip back. Recovery is **revision-level or
>   re-creating the secret by hand** (step D), not a config toggle.
> - **CD does not apply Bicep** (see "Image / ordering model" above). Editing
>   `main.bicep` therefore changes **nothing** on the live app. The connection
>   string must be replaced **imperatively** — that is step D. A Bicep-only change
>   is not a cutover.
>
> Strict order — do NOT touch `todo-api`'s env vars first:
>
> 1. **Postgres server config first** (Entra auth enabled + Entra admin set).
>    Already live since 2026-07-28; idempotent, and it does not disturb anything.
>    Only needed again on a fresh bootstrap. → step A.
> 2. **Verify the in-DB principal for todo-api's managed identity** exists and is
>    bound to the right object ID, and that it holds the `tododb` grants. → step B.
>    **Verify first; do not blindly re-create.** Until this row exists, an
>    Entra-token login for `todo-api` FAILS.
> 3. **Grant `Monitoring Metrics Publisher`** to the todo-api MI on the AI
>    component (`DisableLocalAuth: true` is set), or ingestion 403s silently — the
>    app still runs, telemetry is just lost. → step C.
> 4. **ONLY THEN** run the live cutover: deploy the new image, then replace
>    `ConnectionStrings__TodoDb` with the passwordless value, remove the dead
>    `Postgres__UseEntraAuth` env var, and delete the `todo-db-connection`
>    secret — all in one sequence. → step D.
>
> Doing step 4 before step 2 is the concrete outage scenario. The passwordless
> `Username=` (`todo-api`) MUST exactly match the principal name verified in
> step B.

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

**(B) VERIFY the managed identity's Postgres role, and only repair it if missing
(cutover step 2 — cannot be done in Bicep; runs INSIDE the database).** The
`pgaadauth_*` functions exist **only in the `postgres` database** and have no
ARM/Bicep surface, so this is human-run SQL. Connect to the **`postgres`** database
**as the Entra administrator** (the user in A — authenticate with an Entra access
token as the password, `Ssl Mode=Require`, from a machine whose IP is allowed by
the server firewall).

> Per `.pipeline/infra.md` the principal and its `tododb` grants were created on
> 2026-07-28 and **already exist**. This step is a **verification**, not a re-run.
> Do not blindly re-create it.

```sql
-- 1. VERIFY FIRST. Connected to the `postgres` database, AS the Entra admin:
select * from pgaadauth_list_principals(false);
```

Expect a row with `rolname = 'todo-api'`, `principal_type = 'service'`, and
`objectid = '05c60b63-dda9-4a3e-be87-ac8350600b79'` (the todo-api MI principalId).
**If that row is present and the objectid matches, step B is done — skip the rest
of it.**

```sql
-- 2. ONLY IF the row is MISSING, or its objectid does NOT match: recreate BY OID.
--    The by-OID form is preferred over pgaadauth_create_principal('todo-api',...),
--    which resolves the name in Entra and can bind the wrong object (or fail on a
--    non-unique display name).
select * from pgaadauth_create_principal_with_oid(
  'todo-api',                                 -- role name == Username= in the conn string
  '05c60b63-dda9-4a3e-be87-ac8350600b79',     -- todo-api MI principalId (objectId)
  'service',                                  -- managed identity / service principal
  false,                                      -- isAdmin
  false);                                     -- isMfa

-- 3. ONLY IF you ran step 2 (or the grants are missing): connect to `tododb`, still
--    as the Entra admin, and grant the role what the startup Migrate() needs
--    (CONNECT + schema DDL/DML). Schema ownership is the simplest path for the
--    demo's auto-migrate-at-startup.
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

**Do NOT** add the todo-api MI to
`Microsoft.DBforPostgreSQL/flexibleServers/administrators` (specs §14.7) — that
resource is for *server-level* Entra admins, and using it here would make the
application a database admin. The in-DB role above is the correct, least-privilege
mechanism.

**Diagnostic:** a login failing with *"Service Principal oid mismatch for role
[todo-api]"* means the in-DB role exists but is bound to a different object ID
(e.g. the Container App identity was recreated). Fix = drop and recreate with
`pgaadauth_create_principal_with_oid` using the current principalId. That is not an
application bug.

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

**(D) LIVE CUTOVER to Entra-only DB auth (cutover step 4 — imperative, human-run,
one maintenance window).** This is the step that actually changes the running app.
`main.bicep` already describes this end state, but **CD never applies Bicep**, so
the live app only changes when you run the commands below.

> ⚠️ **Approved deviation from specs §14.9 / the spec's own Q1 (user decision,
> 2026-08-06): the `todo-db-connection` secret is deleted as part of THIS
> sequence, not as a later cleanup.** The consequence, stated plainly: **there is
> no revision-rollback safety net.** Once the secret is gone, the previous
> revision's `secretRef` dangles and
> `az containerapp revision activate "$PREV_REV"` will not restore a working app
> on its own — recovery needs the secret re-created first (see D6). So: run D3,
> D4 and D5 **in the same maintenance window**, and do not consider the cutover
> done until the health check **and** a real DB-backed request have both passed.

```bash
RG=rg-todo-demo
APP=todo-api
PG_FQDN=pg-todo-demo-cus01.postgres.database.azure.com

# --- D0. PRE-CHECK — do not skip. -------------------------------------------
# Step B above must be green: pgaadauth_list_principals(false) shows rolname
# 'todo-api', principal_type 'service', objectid 05c60b63-dda9-4a3e-be87-ac8350600b79.
# Also confirm the todo-api MI principalId still matches that objectid:
az containerapp show -g $RG -n $APP --query identity.principalId -o tsv

# --- D1. Record the current (known-good) revision BEFORE changing anything. --
PREV_REV=$(az containerapp revision list -g $RG -n $APP \
  --query "[?properties.active]|[0].name" -o tsv); echo "previous revision: $PREV_REV"
# Keep this value. It is still useful for diagnosis and for the D6 recovery path,
# but see the caveat above: on its own it is no longer a one-command rollback.

# --- D2. Let CD deploy the new (Entra-only) image, then CONFIRM it is running. -
az containerapp revision list -g $RG -n $APP \
  --query "[?properties.active].{rev:name,image:properties.template.containers[0].image}" -o table
# The image tag must be the merge commit that contains the Entra-only backend.

# --- D3. CUTOVER (single sequence — env flip THEN secret delete). ------------
# 3a. Replace the connection string with the passwordless value and drop the dead
#     flag. --set-env-vars replaces only that variable's definition
#     (secretRef -> plain value) and leaves the other env vars alone.
az containerapp update -g $RG -n $APP \
  --set-env-vars "ConnectionStrings__TodoDb=Host=$PG_FQDN;Port=5432;Database=tododb;Username=todo-api;Ssl Mode=Require;Trust Server Certificate=true" \
  --remove-env-vars Postgres__UseEntraAuth

# 3b. Delete the now-unreferenced secret, in this same sequence (user decision).
#     ORDER MATTERS: this must come AFTER 3a — Container Apps refuses to remove a
#     secret that a live env var still references via secretRef.
az containerapp secret remove -g $RG -n $APP --secret-names todo-db-connection

# 3c. Confirm the end state: no secrets, no Postgres__UseEntraAuth, plain conn str.
az containerapp show -g $RG -n $APP --query "properties.configuration.secrets" -o json   # expect [] or null
az containerapp show -g $RG -n $APP \
  --query "properties.template.containers[0].env[].name" -o tsv                          # expect NO Postgres__UseEntraAuth

# --- D4. VERIFY — health AND a real DB-backed request. Both, in this window. --
API_FQDN=$(az containerapp show -g $RG -n $APP --query properties.configuration.ingress.fqdn -o tsv)
curl -fsS "https://$API_FQDN/health"     # liveness only — does NOT touch the DB
curl -fsS "https://$API_FQDN/api/todos"  # THE REAL CHECK: must be 200 + a JSON array, not 500

# --- D5. VERIFY — the startup log names the right principal, with no warning. -
az containerapp logs show -g $RG -n $APP --tail 100 | grep -i "Postgres auth"
#   EXPECT: "Postgres auth: Entra / managed identity. Host=pg-todo-demo-cus01... Username=todo-api"
#   EXPECT: NO "static Postgres password/passfile ... ignored" warning (D3a removed it)
#   EXPECT: no NotSupportedException, no 28P01, no "No password has been provided"
```

**D6. Recovery, if D4 or D5 fails.** There is no config flag and no in-app
fallback by design. In rough order of preference:

1. **Fix forward** — the usual cause is a step B mismatch (missing role or wrong
   `objectid`, surfaced as *"Service Principal oid mismatch for role [todo-api]"*)
   or a `Username=` typo. Re-run step B's verify, then re-issue D3a with the
   corrected string. This does not need the deleted secret.
2. **Re-create the secret, then roll back** — only if you need the old password
   path back. Because D3b already deleted it, `az containerapp revision activate`
   alone is not enough; you must first restore the secret it referenced:
   ```bash
   read -rs PGPWD    # the todoadmin password (GitHub secret PGADMIN_PASSWORD)
   az containerapp secret set -g $RG -n $APP --secrets \
     "todo-db-connection=Host=$PG_FQDN;Port=5432;Database=tododb;Username=todoadmin;Password=$PGPWD;Ssl Mode=Require;Trust Server Certificate=true"
   az containerapp revision activate -g $RG -n $APP --revision "$PREV_REV"
   ```
   This works **only because the server still accepts password auth** (see the
   `passwordAuth: Enabled` note at the top of Phase 4). Note the old *image* also
   has to be one that still contains a password code path — after this cycle's
   backend change, it does not, so this path really means "roll back to a
   pre-§14 image".

**Record the outcome.** After a successful cutover, write the real result into
`.pipeline/infra.md` and into `.pipeline/deployment-lessons-learned.md` §5a, which
is currently marked UNRESOLVED and must be closed out with what actually happened.
(Heads-up: that lessons file lives on the `docs/deployment-lessons-learned` branch,
not on this one — find it before you start writing.)

**Follow-up, deliberately NOT done this cycle:** the connection string keeps
`Ssl Mode=Require;Trust Server Certificate=true`. That encrypts the transport but
**skips certificate verification**, so it is MITM-susceptible. Moving to
`Ssl Mode=VerifyFull` was considered and deferred by the user on 2026-08-06 —
it changes TLS behavior and deserves its own live verification rather than riding
along inside a cutover that has no fallback. Tracked as a follow-up.

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

> **⚠️ Applying the full template against the LIVE app? Read this first.**
>
> - **This is NOT the cutover.** The live Entra-only cutover is **Phase 4 step D**
>   (imperative `az containerapp update` + `secret remove`). CD never applies
>   Bicep, so the app's live config is changed there, not here. Running a full
>   apply *instead of* step D is not a substitute — and running it *before*
>   Phase 4 step B (the in-DB principal) is the concrete DB-outage scenario,
>   because a full apply also flips `todo-api` to the passwordless connection
>   string and removes the `todo-db-connection` secret in one shot.
> - **A full apply will revert `todo-api`/`todo-web` to the placeholder image.**
>   `todoApiImage`/`todoWebImage` default to `mcr.microsoft.com/k8se/quickstart`,
>   so unless you pass the currently-running image tags with
>   `--parameters todoApiImage=... todoWebImage=...`, the apply rolls the apps
>   back off their real images. Read the current tags first:
>   ```bash
>   az containerapp show -g rg-todo-demo -n todo-api --query properties.template.containers[0].image -o tsv
>   az containerapp show -g rg-todo-demo -n todo-web --query properties.template.containers[0].image -o tsv
>   ```
> - **Always `what-if` before `create`** and read the `todo-api` delta line by
>   line. Steady state, the only expected deltas are Azure-side defaults; anything
>   touching `env`, `secrets` or `image` is you about to change the running app.

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
