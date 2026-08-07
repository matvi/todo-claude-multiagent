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

---

## 2026-08-06 — Dev environment + dev→prod promotion pipeline

Branch `pipeline/dev-environment`. See specs.md **§15**. The project goes from
**one** deployment environment to **two** (`dev` + `prod`) inside the **same
resource group** `rg-todo-demo` (user instruction: "in the same azure resource"),
and CD becomes a **sequential dev → verify → approve → prod promotion** instead of
a single-shot deploy to the only environment. §15 **refines §11.7** (a `develop`
branch now exists) and **refines the "single Container Apps Environment"
convention** below; it supersedes nothing in §12/§13/§14 — every rule in those
sections applies identically to the new dev stack.

- **Services added/changed:** Four **new** Azure resources, all in `rg-todo-demo`:
  a **second `Microsoft.App/managedEnvironments`** (`cae-todo-demo-dev`, East US),
  two Container Apps (`todo-api-dev`, `todo-web-dev`), a **second PostgreSQL
  Flexible Server** (`pg-todo-demo-dev-cus01`, Central US, B1ms, db `tododb`), and
  a **second Application Insights component** (`appi-todo-demo-dev`). **Shared,
  not duplicated:** the resource group, the Basic ACR, and the `log-todo-demo`
  Log Analytics workspace. **Production is byte-for-byte untouched** —
  `infra/main.bicep`, `pg-todo-demo-cus01`, `appi-todo-demo`, `cae-todo-demo`,
  `todo-api`, `todo-web` and all their env vars/replica counts are unchanged.
- **Key decisions (with rationale):**
  - *Dev gets its **own Container Apps Environment**, not just differently-named
    apps inside `cae-todo-demo`* (**user directive**). A managed environment is
    the ACA security/network boundary — shared VNet, internal DNS, log
    destination, platform upgrade schedule. Co-locating would give unreviewed dev
    code a network path to prod over internal DNS and would make every
    environment-level change unrehearsable. **Consumption-plan environments have
    no standing charge, so the boundary is free** — there was no argument for
    sharing. Consequence: `cae-todo-demo-dev` has its **own random
    `defaultDomain`**, so dev FQDNs are unpredictable and must always be read
    back with `az containerapp show`, never hardcoded.
  - *Dev gets its **own Postgres Flexible Server**, not a second database on the
    prod server.* This is ~80% of the cost delta and the one decision needing
    conscious sign-off. Decisive reason: **`pgaadauth` principals are
    server-global and `PUBLIC` holds `CONNECT` on every database by default** — on
    a shared server the dev managed identity could reach prod `tododb` unless a
    human remembered a `REVOKE`, i.e. isolation by grant hygiene rather than by
    construction. Secondary: startup `Migrate()` means a wrong `Database=` on a
    shared server runs DDL against the other environment (destructive) whereas on
    separate servers it simply fails to connect (fail-closed); B1ms is one shared
    burst-credit pool so dev load would degrade prod; server-level changes (PG
    version, `authConfig`, params, PITR drills) could never be rehearsed. The
    shared-server fallback is documented in §15.4.5 **with mandatory hardening
    SQL** and requires a superseding memory entry if ever adopted.
  - *ACR is **shared** — deliberately, and not for cost.* The promotion gate only
    means something if prod runs the **exact artifact** dev validated. One
    registry lets `deploy-prod` re-point production at the same `todo-api:<sha>`
    with **no rebuild**. Two registries would reduce "tested in dev" to a claim
    about source code.
  - *Log Analytics **shared** (honors the 2026-07-27 do-not-add-workspaces
    constraint; dev/prod separate cleanly via `EnvironmentName_s`), but App
    Insights **duplicated***. The AI component must be per-environment because
    ingestion is authorized by `Monitoring Metrics Publisher` **scoped to the
    component** — one component would mean granting the dev identity publisher
    rights on prod telemetry, the exact coupling this cycle removes. Both cost
    $0 fixed.
  - *Accepted, documented risk:* the workspace's **1 GB/day cap is now shared**,
    so dev ingestion can silently blind **prod** telemetry for a UTC day.
    Detection KQL + cap-raise lever in §15.4.2; a dev workspace is the escalation
    only, and would need a superseding entry.
  - *Bicep: a **new, separate `infra/main.dev.bicep`**, with `main.bicep`
    untouched and never re-deployed.* Prod live config has **permanently drifted**
    from Bicep by design (§14.2, CD does not apply Bicep); parameterizing one
    template by environment would make every dev bootstrap a potential prod
    deploy that silently reverts that drift. Both templates call the **same
    `infra/modules/*`**, which is what actually guarantees "same infrastructure".
  - *Pipeline: **one** `cd.yml`, triggered on `develop` **and** `main`, with
    `deploy-dev` → `verify-dev` → `deploy-prod`.* `deploy-prod` carries
    `if: github.ref == 'refs/heads/main'` + `environment: production` (required
    reviewers = the human gate) and `needs:` both earlier jobs. On a `main` push
    the prod-bound commit is deployed to dev and smoke-tested **first** — so the
    gate is enforced by the job graph, not by convention, and the SHA that
    reaches prod is provably the SHA dev ran. Rejected: two independent workflows
    (a merge/squash produces a **new SHA** dev never saw).
  - *`verify-dev` uses **no Azure credential at all*** — public HTTPS probes of
    `/health`, `GET /api/todos` (proves dev MI Entra auth to dev Postgres **and**
    that `Migrate()` succeeded), a create/delete round-trip, and the frontend's
    baked API URL. Retries with backoff because dev is `minReplicas: 0`.
  - *`todo-api` is truly promoted (same digest); `todo-web` must be **rebuilt**
    per environment* because `VITE_API_BASE_URL` is baked at build time — tags
    `todo-web:<sha>-dev` / `-prod`. Residual risk stated openly; the real fix
    (runtime config injection, which would also **remove the long-standing
    backend-before-frontend ordering constraint**) is recorded as the recommended
    next app cycle, not smuggled into an infra cycle.
  - *Zero new application secrets; still no Key Vault.* Dev's
    `ConnectionStrings__TodoDb` is passwordless and a **plain** env var, dev App
    Insights uses Entra with local auth disabled, dev apps pull via `AcrPull`,
    CI/CD uses OIDC. The only new secret is the **deploy-time**
    `PGADMIN_PASSWORD_DEV` (GitHub secret, **different value from prod's**) —
    out of application-secret scope per §13.9. Both servers keep
    `passwordAuth: Enabled` (§14 standing constraint).
  - *Cost delta ≈ **+$18–22/month*** (~$16–17 dev Postgres, ~$2 dev ACA compute,
    ~$0–3 extra ingestion; ACA env / AI component / ACR / RG all $0). New total
    ~$43–59/month. Dev apps run **min 0 / max 1** so dev compute idles at $0.
    Note the ACA Consumption free grant is **per subscription**, already consumed
    by prod's `minReplicas: 1` frontend — dev bills from the first second.
    Must be surfaced in `changes.md` for explicit approval before provisioning.
  - *Branch protection finally has a remote to apply to.* `.pipeline/infra.md`
    flagged twice that protection on `main` was unverifiable/absent because no git
    remote existed; **`git@github.com:matvi/todo-claude-multiagent.git` now
    exists**. §15.12 specifies rules for `main` (PR + review + 4 CI checks +
    `guard-promotion-source` + no admin bypass) and lighter rules for `develop`
    (PR + CI, no required review). Still a **human** step; `gh` is not installed
    in the agent environment so status remains unverified.
  - *Known plan risk:* **required reviewers are unavailable on private repos on
    GitHub Free.** §15.9.5 specifies a fully-worked **Plan B** (split
    `cd-dev.yml` + `workflow_dispatch`-only `cd-prod.yml` taking a dev-validated
    `image_tag`) that preserves the hard human gate and artifact promotion.
    Verify repo visibility **before** writing the workflow.
- **Constraints future features must respect:**
  - **One Container Apps Environment *per deployment environment*** — this
    refines the earlier "single Container Apps Environment" convention (whose
    intent, *never one environment per app*, still holds). Dev apps go in
    `cae-todo-demo-dev`; prod apps go in `cae-todo-demo`. Never mix.
  - **Naming rule:** environment-specific resources take a **`-dev` suffix** in
    dev and **no suffix** in prod. Never introduce a `-prod` suffix — the
    unsuffixed name *is* production.
  - **The four-way identity invariant now holds twice, independently:** Container
    App name == managed-identity display name == `pgaadauth` role == `Username=`,
    as `todo-api` on `pg-todo-demo-cus01` and `todo-api-dev` on
    `pg-todo-demo-dev-cus01`. The two sets **never mix**; a dev identity must
    never become a principal on the prod server (or vice versa).
  - **Every new deployable component must be added to BOTH `main.bicep` and
    `main.dev.bicep`, and to all three CD jobs**, and must respect the
    backend-before-frontend ordering constraint until Q3 removes it.
  - **`infra/main.bicep` stays production-only and is never parameterized by
    environment**, because prod's drift from it is permanent and by design.
    Real resource shape lives in `infra/modules/*` so both templates stay
    identical; any module change must be validated against both.
  - **Production is only ever deployed by promotion** — through `deploy-dev` +
    `verify-dev` of the same SHA, then human approval. No workflow may deploy to
    prod without that chain, and `deploy-prod` must never rebuild `todo-api`
    (assert the tag exists in ACR instead).
  - **`develop` is the integration branch and the default PR target**; `main`
    only ever receives PRs from `develop` or `hotfix/*` (enforced by the
    `guard-promotion-source` CI check, since branch protection cannot express it).
  - **Dev inherits every §13/§14 rule verbatim:** Entra-only app→Postgres with no
    password path or flag, no `Password=`/`Passfile=` in any connection string,
    plain non-secret env vars (Container Apps raw secrets remain deprecated for
    credentials), Key Vault only via the §13.6 pattern if a future dependency
    truly cannot do Entra, TLS everywhere.
  - **No production data ever enters dev** — no copy, restore, replication or
    anonymization pipeline.
  - Identities, federated credentials, RBAC role assignments, in-database
    `pgaadauth` principals, GitHub secrets/variables/environments and branch
    protection remain **human-run only** (§11.6/§11.8). Agents author the exact
    commands (§15.11 Phases A–F) and never execute them.
  - **OIDC gotcha to preserve:** a job declaring `environment: <name>` makes the
    federated-credential subject `repo:<owner>/<repo>:environment:<name>`, **not**
    the branch form. Any new deploy job needs its own matching federated
    credential.
