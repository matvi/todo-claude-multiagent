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
| `modules/loganalytics.bicep` | Log Analytics workspace (PerGB2018, 30-day retention). |
| `modules/acaEnvironment.bicep` | Container Apps managed environment (Consumption). |
| `modules/postgres.bicep` | Postgres Flexible Server (Burstable B1ms, PG16, 32 GiB, no HA, LRS backup) + `tododb` database + "allow Azure services" firewall rule. |
| `modules/containerApp.bicep` | Reusable single Container App (system-assigned identity, ingress, secrets, scale). Used for `todo-api` and `todo-web`. |

## Image / ordering model

`main.bicep`'s `todoApiImage` / `todoWebImage` default to a **public placeholder**
(`mcr.microsoft.com/k8se/quickstart:latest`) so the first deploy succeeds before
any app image exists in ACR. The CD pipeline then builds the real images and rolls
the apps imperatively, honoring the backend-before-frontend ordering (the frontend
bakes `VITE_API_BASE_URL=<backend FQDN>` at build time). This is approach **(a)**
from specs §11.5 (run Bicep once for infra, then imperative image updates). The CD
pipeline reads the currently-running images and passes them back into the infra
deploy so re-running Bicep never resets the apps to the placeholder.

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
#    (a) deploys from main
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_ORG_REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
#    (b) the `production` GitHub Environment used by cd.yml
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_ORG_REPO"':environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
#    (c) OPTIONAL — only if you later enable what-if on PRs in ci.yml
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "gh-todo-demo-pr",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$GH_ORG_REPO"':pull_request",
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
