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

---

## 2026-07-27 — Observability / distributed tracing (App Insights + OpenTelemetry)

Added end-to-end request/response tracing for the .NET 10 backend, correlated by
`traceId` and exported to Azure Application Insights. See specs.md §12. Branch
`pipeline/appinsights-tracing`.

- **Services added/changed:** One new Azure resource — a **workspace-based
  Application Insights component** (`appi-todo-demo`, `Microsoft.Insights/
  components`) backed by the **existing** `log-todo-demo` Log Analytics
  workspace (NO second workspace). Backend `TodoApi` gains OpenTelemetry
  packages; `todo-api` Container App gains an `appinsights-connection` secret to
  env var `APPLICATIONINSIGHTS_CONNECTION_STRING`. No frontend/runtime topology
  change otherwise.
- **Key decisions (with rationale):**
  - *Telemetry package = **`Azure.Monitor.OpenTelemetry.AspNetCore`*** (the
    modern OpenTelemetry-based, Microsoft-recommended App Insights integration),
    **not** the legacy `Microsoft.ApplicationInsights.AspNetCore` classic SDK.
    Auto-instruments ASP.NET Core requests + HttpClient + EF Core/Npgsql DB calls
    into one W3C-Trace-Context-correlated trace via a single
    `AddOpenTelemetry().UseAzureMonitor()` call. DB spans enabled via
    `Npgsql.OpenTelemetry`.
  - *Tracing **coexists** with existing logging, does not replace it.* The app's
    default `ILogger`/console logging (per §7) stays; `UseAzureMonitor()` adds an
    OTel logging provider. `traceId`/`spanId` land on every log line
    **automatically** via `Activity.Current` + `Microsoft.Extensions.Logging` —
    no Serilog, no custom enricher, no code change to existing log calls. Keeps
    "keep it simple."
  - *Connection string from env var only.* Standard SDK var
    `APPLICATIONINSIGHTS_CONNECTION_STRING`, sourced from a **Container Apps
    secret** exactly like the Postgres connection string (§5.5). Never hardcoded/
    committed. Absent locally/CI = exporter no-ops, app still runs.
  - *Application Insights is **workspace-based, reusing `log-todo-demo`.***
    Single-workspace / single-of-everything posture (§11.9); one queryable place
    for all telemetry; no second cost center or retention setting.
  - *Frontend RUM (App Insights JS SDK / browser tracing) is **out of scope***
    — the requirement targets the .NET backend only. Documented as an optional,
    separately-approved future enhancement (SPA App Insights Web SDK +
    `traceparent` header + CORS allowed-headers), NOT silently included.
  - *Testability without live Azure:* verify OTel is wired in-process via
    `WebApplicationFactory<Program>` + in-memory exporter / `ActivityListener`;
    assert a request produces a valid 32-hex `TraceId` and that a log record
    shares that `TraceId`/`SpanId`; assert the app runs with the env var unset.
    No live App Insights ingestion endpoint needed in tests/CI.
  - *Cost:* workspace-based App Insights bills on ingestion (pay-as-you-go, ~5
    GB/month free grant); reuses the existing 30-day-retention workspace so adds
    no fixed monthly charge — only usage beyond the free grant. Sampling / daily
    cap are the levers if volume grows. Devops must flag any projected
    meaningful cost in `changes.md` (§11.9 rule).
  - *PROPOSE-mode split preserved:* the architect specifies WHAT/WHY; the
    **devops agent** later PROPOSES the Bicep module (`infra/modules/
    appinsights.bicep`), the SKU/retention posture, and the secret/env wiring —
    author + validate only, never provision (§11.8).
- **Constraints future features must respect:**
  - Telemetry standard for this project is **OpenTelemetry via
    `Azure.Monitor.OpenTelemetry.AspNetCore`**. Do not reintroduce the classic
    Application Insights SDK; add new instrumentation as OTel sources.
  - Reuse the single `log-todo-demo` workspace for any future telemetry/App
    Insights; do not spin up additional Log Analytics workspaces.
  - App Insights connection string travels only as the
    `APPLICATIONINSIGHTS_CONNECTION_STRING` env var from a Container Apps secret
    — keep it out of source and out of `appsettings*.json`.
  - Any new backend component/service should inherit trace correlation for free
    via `Activity.Current`; preserve default activity tracking (do not disable
    `TraceId`/`SpanId` enrichment).

---

## 2026-07-27 — Identity & secrets policy: managed-identity-first (Key Vault fallback)

Human policy folded into the same cycle as the App Insights tracing work
(branch `pipeline/appinsights-tracing`). See specs.md §13 (and the §12.5 / §5.5
revisions it supersedes). Policy verbatim: "Applications that can use identity
manager [managed identity] to authenticate should use it. If applications can
not use it, then it should use KeyVault to store secrets or connection strings."

- **Services added/changed:** No new always-on resource. Behavioral changes:
  Postgres Flexible Server gains **Entra authentication** + an Entra admin;
  Application Insights (`appi-todo-demo`) gets **local auth disabled**; `todo-api`
  managed identity gains **`Monitoring Metrics Publisher`** on the AI component.
  Two Container Apps **secrets removed** (`todo-db-connection`,
  `appinsights-connection`). **No Key Vault provisioned** (not needed).
- **Key decisions (with rationale):**
  - *Decision hierarchy for every app credential:* (1) managed-identity/Entra
    auth if the target supports it — REQUIRED; (2) Key Vault (RBAC, `Key Vault
    Secrets User`, Container Apps Key Vault reference) only if MI is impossible;
    (3) raw Container Apps secret = deprecated last resort. Applying this
    **eliminates all application runtime secrets**, so **Key Vault is specified
    but NOT instantiated** this cycle (no secret reaches its branch).
  - *Postgres = managed-identity (Entra) auth; password DROPPED.* Verified viable
    with Npgsql on .NET 10: acquire an Entra token (scope
    `https://ossrdbms-aad.database.windows.net/.default`) via `Azure.Identity`
    and pass it as the Npgsql password, SSL required; refresh via Npgsql
    **periodic password provider** (token ~60 min). Connection string becomes
    **passwordless + non-secret** (Username = the MI's Postgres role name),
    delivered as a **plain env var**. No blocker found that justified keeping the
    password. Local dev stays on the local-Docker password via a
    `Postgres__UseEntraAuth` gate (local Postgres isn't Entra-enabled; local
    password is not an Azure secret → out of policy scope).
  - *App Insights = Entra auth via `AzureMonitorOptions.Credential`
    (`ManagedIdentityCredential`); connection string DEMOTED from secret to plain
    env var.* Nuance recorded: the connection string is still REQUIRED (ingestion
    endpoint + resource InstrumentationKey/App-Id) but, with **local auth
    disabled** on the component, the embedded key is no longer a usable
    credential — it is just a resource identifier, hence non-secret. This
    **revises §12.5** (the `appinsights-connection` secret is not created).
  - *ACR pull* already uses MI (`AcrPull`) — canonical compliant example, no
    change. *CI/CD → Azure* uses **OIDC federation** (external workload, can't use
    an Azure MI) — the identity-first, secretless equivalent; satisfies the
    policy's spirit, distinct case, no change. *Postgres admin password*
    (`PGADMIN_PASSWORD`) is a **deploy-time** CI/CD secret, not an app runtime
    secret → out of the application-secret policy scope; unchanged.
  - *New manual human bootstrap* (extends §11.6, never agent-run): enable Entra
    auth + set Entra admin on Postgres (Bicep does the server side), then connect
    as the Entra admin to the `postgres` DB and run
    `pgaadauth_create_principal('<todo-api-MI-name>', false, false)` + grant it
    schema/DDL rights on `tododb` (so startup `Migrate()` works under the MI).
  - *Cost:* policy is cost-neutral-to-negative — removes stored secrets, adds no
    Key Vault (avoids its per-op cost); MI auth + role assignments are free.
    Strengthens the §11.9 posture; **supersedes the §5.5 "Container Apps secrets,
    not Key Vault — one fewer resource" rationale** (now: managed-identity-first,
    zero app secrets).
- **Per-secret classification (authoritative — do not re-litigate):**
  - `todo-api` → Postgres: **managed identity** (Entra token), no stored secret.
  - `todo-api` → App Insights: **managed identity** (Entra), connection string =
    non-secret env var, local auth disabled.
  - `todo-api`/`todo-web` → ACR: **managed identity** (`AcrPull`) — existing.
  - CI/CD → Azure: **OIDC federation** — existing (MI-equivalent for external).
  - Postgres admin password: **deploy-time CI/CD secret** — out of app scope.
  - Result: **zero application runtime secrets; no Key Vault.**
- **Constraints future features must respect:**
  - **Managed-identity-first is now the project rule.** Any new dependency on an
    Azure service that supports Entra/MSI auth MUST use the Container App's
    managed identity — do not introduce a stored secret for it.
  - If a future secret **cannot** use MI, use the **Key Vault pattern in §13.6**
    (provision `kv-todo-demo`, RBAC `Key Vault Secrets User`, Container Apps Key
    Vault reference) — never a raw Container Apps secret, never legacy KV access
    policies. Record any such secret here.
  - The §5.5 "Container Apps secrets, not Key Vault" default and the §12.5
    "connection-string-as-secret" approach are **superseded** by §13 for all
    application credentials. Container Apps *raw* secrets are deprecated for
    credentials.
  - The Postgres Entra-principal SQL and any role assignments remain **manual /
    IaC-authored but human-applied**; agents never run them (PROPOSE mode).

---

## 2026-08-06 — Application DB auth is Entra-ONLY (no password path, no mode flag)

Branch `pipeline/entra-passwordless-connection-string`. See specs.md **§14**, which
**supersedes §13.4** (application DB auth + connection string) and **§6.2/§6.3**
(local dev DB auth). Two drivers: a live bug, and a **deliberate user scope
decision** (recorded as such — this was not discovered by an agent).

- **Services added/changed:** **No Azure resource, SKU, region or server-auth
  change. Cost delta zero.** Behavioural only: `todo-api`'s DB connection becomes
  Entra/managed-identity-only; its `ConnectionStrings__TodoDb` becomes a
  passwordless **plain env var** and the `todo-db-connection` Container Apps
  secret is retired after live verification; the `Postgres__UseEntraAuth` env var
  is deleted from `infra/main.bicep` and from the live app. `infra/modules/
  postgres.bicep`, `main.parameters.json` and the CD workflows are **untouched**.
- **The live bug that started the cycle:** after the `UsePasswordProvider` fix
  (`1b765fd`) shipped, flipping `Postgres__UseEntraAuth=true` live failed with
  `NotSupportedException: When registering a password provider, a password or
  password file may not be set.` Root cause: **one connection-string value served
  two auth modes that require different shapes, and nothing reshaped it** — the
  Entra builder received the SQL-auth string (`Username=todoadmin;Password=…`)
  from the shared secret. The boolean flag was never actually sufficient to
  switch modes.
- **Key decisions (with rationale):**
  - *The application authenticates to Postgres with **Entra/managed identity
    only**, unconditionally, in every environment* (**user directive**, not an
    agent choice). `Postgres:UseEntraAuth` and the `else`/password branch in
    `TodoDbContextRegistration` are **deleted**; `AddTodoDbContext` always builds
    the Entra `NpgsqlDataSource`. Removing the branch removes the whole bug class:
    with one code path there is only one connection-string shape, so it can never
    be the wrong one for the mode.
  - *The **server** keeps dual auth* (`activeDirectoryAuth: Enabled` +
    `passwordAuth: Enabled`), keeps the `todoadmin` login, the
    `@secure() postgresAdminPassword` param and the `PGADMIN_PASSWORD` CI secret
    — **user directive**: out-of-band `psql`/admin access must keep working.
    "Entra-only" scopes to the **application's connection**, never to the server.
    Do not "helpfully" disable password auth in a future cycle.
  - *`ConnectionStrings:TodoDb` is the **single source of truth** and is
    passwordless everywhere.* No second connection string, no second secret, and
    **no** `Postgres:EntraUsername`-style key (an intermediate draft had one;
    dropped). `Username=` must be the Postgres role mapped to the caller's Entra
    identity (`todo-api` in Azure).
  - *The Entra path **normalizes** what it is handed:* strips `Password`/
    `Passfile` (both — Npgsql rejects a password provider if **either** is set),
    fails fast with a named-key message if `Username` is missing, and forces
    `SslMode=Require` for **non-loopback** hosts only (the token travels in the
    cleartext-password field). Defence-in-depth that makes the crash structurally
    impossible and the live cutover order-independent.
  - *One `Information` startup log line — `Host`/`Database`/`Username` + auth mode,
    plus warnings when a credential was stripped or SSL upgraded.* The original
    incident was diagnosable only from a stack trace; never log the token,
    password or full connection string.
  - *Local dev keeps ONE code path with zero passwords* by running the local
    Docker Postgres with **`POSTGRES_HOST_AUTH_METHOD: trust`** (no
    `POSTGRES_PASSWORD`, no `Password=` in `appsettings.Development.json`). The
    server never challenges, so the Entra path runs unchanged. Costs: a one-time
    `docker compose down -v` (trust is only applied by `initdb` on an empty
    volume) and a documented one-time `az login`. *Rejected alternative:* pointing
    local dev at the real Azure server as the developer's own identity (needs a
    per-dev firewall rule + per-dev `pgaadauth` principal, gives every dev write
    access to the demo DB with `Migrate()` at startup, breaks offline work).
  - *`UsePasswordProvider` (connection-open path) is retained and reaffirmed.*
    **Never** reintroduce `UsePeriodicPasswordProvider` (lessons §5a outage).
  - *Rollback model changed:* with no in-app fallback, recovery is a **revision
    rollback**, which stays viable precisely because the server keeps password
    auth and the previous revision + its secret are left intact until the Entra
    connection is verified live. Capture the rollback revision name *before* the
    cutover.
  - *The MI is granted via an **in-database** `pgaadauth` role, never via the
    server's `administrators` child resource* (that would make the app a DB
    admin). It has no ARM/Bicep surface and stays a human-run step; already
    applied (verify with `pgaadauth_list_principals(false)`, recreate by OID with
    `pgaadauth_create_principal_with_oid` only if the row or its `objectid` is
    wrong).
- **Constraints future features must respect:**
  - **The application must never gain a password/SQL-auth path to Postgres
    again** — no flag, no "just for local", no fallback. Any new service that
    needs the DB uses the same Entra data source.
  - **`ConnectionStrings:TodoDb` must never contain `Password=`/`Passfile=`** in
    any environment or file, and stays a **plain, non-secret** env var
    (Container Apps secrets remain deprecated for credentials, §13).
  - **Do not disable `passwordAuth` on the Postgres server** and do not remove
    `todoadmin` / `postgresAdminPassword` / `PGADMIN_PASSWORD` — human admin
    access is a standing requirement.
  - The `Username=` in the connection string, the Container App name, the
    system-assigned MI name and the `pgaadauth` role name are **one value**
    (`todo-api`). Changing any one of them requires changing all four.
  - Local dev is `trust`-auth Docker Postgres + `az login`; keep it password-free
    if the local setup is ever revised.
  - Live config drift from Bicep is **permanent and by design** (CD does not apply
    Bicep — lessons §2c). Any DB/auth change must be expressed as an imperative
    `az containerapp update` runbook step as well as in Bicep, or it will never
    take effect.
