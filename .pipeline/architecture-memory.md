# Architecture Memory — Todo Application

Persistent, **append-only** log of durable architectural decisions and their
rationale for this project. It survives across pipeline runs/cycles so that
future architect/devops runs stay consistent and do not re-litigate settled
decisions or drift from established conventions.

Rules for this file:
- **Never overwrite or delete** prior entries. Only append new dated entries.
- Record decisions that are *durable* (would be expensive or confusing to
  reverse), not per-feature trivia.
- If a later cycle intentionally reverses an earlier decision, add a **new**
  entry that explicitly supersedes the old one (name the date it supersedes);
  do not edit the old entry.
- Commit this file to git like any other source file.

---

## Conventions established (quick reference)

These are the running conventions all future work must respect unless a dated
entry below supersedes them.

- **Cloud:** Azure. Single subscription. Default region **eastus**.
- **Resource group strategy:** one resource group per app/environment,
  `rg-todo-demo` for this demo. Do not sprawl resources across multiple RGs.
- **Naming convention:** `<type-abbrev>-todo-demo` (e.g. `rg-`, `cae-`, `pg-`,
  `log-`) for uniquely-scoped resources; workload names without prefix for the
  apps (`todo-api`, `todo-web`, database `tododb`). Globally-unique names (ACR,
  Postgres server) get a short suffix appended.
- **Compute:** everything runs as **Azure Container Apps** (frontend + backend),
  in a **single Container Apps Environment** (`cae-todo-demo`). No AKS, no App
  Service, no Static Web Apps.
- **Registry:** a single **Azure Container Registry (Basic SKU)** shared by all
  images.
- **Database:** **Azure Database for PostgreSQL Flexible Server**, Burstable
  tier (`Standard_B1ms`), single logical DB `tododb`.
- **Cost posture:** this is a **demo**. Always favor free/cheap/Consumption/
  Burstable/Basic SKUs. No Premium/Dedicated/Standard-tier upgrades without an
  explicit, documented reason.
- **Secrets:** Container Apps secrets (not Key Vault) for the demo. Nothing
  secret committed to git.
- **Schema ownership:** EF Core code-first migrations. No hand-maintained SQL.

---

## 2026-07-23 — Todo app (initial specification)

Seeded retroactively from `.pipeline/specs.md` (the initial app spec) so the
foundational decisions are captured in memory.

- **Services added/changed:** Introduced the whole stack — React (Vite + TS) SPA
  frontend, .NET 10 Web API backend, PostgreSQL Flexible Server, Azure Container
  Registry (Basic), Azure Container Apps Environment + two Container Apps, Log
  Analytics workspace.
- **Key decisions (with rationale):**
  - *Both frontend and backend on Container Apps* (not Static Web Apps for the
    SPA) — one uniform deploy mechanism/CLI/registry/environment for a
    container-focused reference app; marginal cost saving of SWA not worth the
    second service model. Clean swap to SWA later if cost minimization matters.
  - *Single Container Apps Environment* shared by both apps — simplest topology,
    shared Log Analytics, shared networking.
  - *Postgres public access + firewall + required TLS* (no VNet / private
    endpoint) — avoids VNet injection + delegated subnet + private DNS
    complexity; acceptable for a demo. Firewall uses the "allow Azure services"
    (`0.0.0.0`) rule so ACA egress reaches Postgres without pinning IPs.
  - *Container Apps secrets, not Key Vault* — one fewer resource, no RBAC/access-
    policy setup. Key Vault is the documented production upgrade path.
  - *ACR pull via managed identity (`AcrPull`)* preferred over admin-user creds.
  - *Backend scale-to-zero* (min 0, max 3); *frontend min 1* for instant SPA
    load. 0.25 vCPU / 0.5 GiB per replica.
  - *EF Core migrations applied at startup* (`db.Database.Migrate()`) — demo
    convenience, single-instance app.
  - *No auth* — single anonymous user, explicitly out of scope.
  - *Frontend bakes `VITE_API_BASE_URL` at build time* → **deployment ordering
    constraint**: backend must exist (FQDN known) before the frontend image is
    built.
- **Constraints future features must respect:**
  - Keep the folder/project layout in specs §3.2 (`backend/…`) and §4.2
    (`frontend/…`) stable — tester agents depend on exact names.
  - Containers listen on port **8080** (ACA ingress target port) — frontend
    nginx and backend Kestrel both.
  - Backend exposes `GET /health` returning `{ "status": "ok" }`, ungated, for
    ACA probes.
  - The frontend build-arg / deployment-ordering constraint above applies to any
    future deploy automation (CI/CD must respect it).

---

## 2026-07-24 — CI/CD + Infrastructure-as-Code layer

Extended the SDLC with a `devops` subagent stage. See specs.md §11.

- **Services added/changed:** No new *runtime* Azure services. Added a delivery
  layer: **Bicep IaC**, **GitHub Actions** workflows (CI + CD), an
  **`azure-pipelines.yml`** (Azure DevOps equivalent), and a one-time
  **workload-identity-federation (OIDC) app registration** for CI/CD to
  authenticate to Azure without stored secrets.
- **Key decisions (with rationale):**
  - *IaC tool = **Bicep*** (not Terraform) — Azure-native, no remote state
    backend to provision/lock (Azure stores deployment state server-side),
    first-class ACA + Postgres Flexible Server typing, lowest ceremony for a
    single-subscription demo. Terraform documented as the alternative if the
    project ever goes multi-cloud or wants explicit state/plan artifacts.
  - *CI/CD auth = **OIDC federated credentials** (workload identity federation)*,
    not a service-principal client secret — no long-lived secret to store or
    rotate; short-lived tokens minted per run. Applies to both GitHub Actions
    (`azure/login@v2`) and Azure Pipelines (workload identity federation service
    connection).
  - *Identity/credential + role-assignment creation is a **manual one-time human
    step**, never automated by any agent.* Agents author the IaC/YAML and
    document the exact `az`/`gh` commands; a human runs them. This is a hard
    rule (agents must not create app registrations, federated creds, RBAC role
    assignments, or GitHub repo secrets/variables).
  - *Two GitHub Actions workflows:* **CI** on `pull_request` → `main`
    (build/test/lint + `docker build` validation + `bicep build`/lint +
    `az deployment ... what-if`, **no push, no deploy**); **CD** on `push` to
    `main` → build+push images to ACR, `az deployment group create` the Bicep,
    roll the Container App revisions. CD respects the frontend build-arg ordering
    constraint (backend deployed first, its FQDN fed to the frontend build).
  - *Branch protection on `main`:* feature branches off `main`, PRs required,
    **no direct pushes to `main`**, CI status check required to merge, squash
    merge. CD is the only thing that deploys, and only from `main`.
  - *devops agent operates in **PROPOSE mode***: it writes IaC + pipeline YAML +
    documents commands; it does **not** run any `create`/`deploy`/`delete`, does
    not provision, does not touch credentials.
  - *Cost-conscious SKU guidance is codified* (Consumption ACA, Burstable
    Postgres B1ms, Basic ACR, Log Analytics 30-day retention) so PROPOSE-mode
    output stays cheap by default.
- **Constraints future features must respect:**
  - IaC is **Bicep** — future infra changes go through the Bicep templates under
    `infra/`, not ad-hoc `az create`. Keep the CLI command sketches in specs as
    reference/bootstrap only.
  - The OIDC app registration + its federated-credential subjects (repo/branch/
    environment) and role assignments are managed by humans out-of-band; agents
    reference them by the documented GitHub variable/secret names only.
  - Any new deployable component must be added to the Bicep + both pipelines and
    must respect the backend-before-frontend ordering constraint.
  - `main` stays protected; all agent-driven work lands via `pipeline/*` branches
    and PRs.
