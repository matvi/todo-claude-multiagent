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

## 2026-08-12 — End-user authentication & authorization (Entra External ID + Postgres roles)

Branch `pipeline/entra-external-id-auth`. See specs.md **§16**. Removes the "no auth"
out-of-scope item from §8. **Does NOT supersede §14** — the application's Postgres
connection stays Entra/managed-identity-only and passwordless; §16 adds *end-user*
identity on top of it.

- **Services added/changed:** One new **Microsoft Entra External ID (CIAM) external
  tenant** — a *separate directory* from the workforce tenant `6ce2ff72-…`, linked to
  subscription `9ba6633f-…` / `rg-todo-demo` — containing two app registrations
  (`todo-api-app` = protected API exposing scope `Todos.ReadWrite`; `todo-web-spa` =
  public SPA client, PKCE, no secret), one sign-up/sign-in user flow, a custom
  `phoneNumber` attribute, and Google as a federated identity provider. One new Azure
  resource: **Key Vault** (`kv-todo-…`, RBAC-authorization, eastus) used **only** as
  a custodial store for the Google OAuth client secret. New `users` table in the
  existing `tododb`. `todo-api` gains JWT bearer validation + a claims transformation;
  `todo-web` gains MSAL React + react-router. External prerequisite outside Azure: a
  **Google Cloud OAuth Web client**. Cost delta ≈ **$0** (External ID free ≤50k MAU;
  KV a few operations/year).
- **Key decisions (with rationale):**
  - *Product = **Entra External ID external tenant**, never Azure AD B2C* (legacy,
    being retired; its `b2clogin.com` / `p=B2C_1_*` patterns must not be copied).
    Consumer identities are deliberately kept **out of the workforce tenant** that
    holds the subscription and the managed identities.
  - ***Entra proves identity; Postgres decides permissions.*** Role lives in
    `users.role` (`'User'`/`'Admin'`, CHECK-constrained) and is **re-derived from the
    DB on every request** by a scoped `IClaimsTransformation`, which also **strips
    every inbound `role`/`roles` claim** before adding its own. Rejected Entra app
    roles/groups: promoting a user would need Graph *write* into the external tenant,
    and a workforce-tenant managed identity **cannot authenticate to another tenant**,
    so it would have required a stored client secret — contradicting §13/§14.
  - *Identity key is the **`oid`** claim, never `sub`.* `sub` is pairwise (different
    per application), so keying on it would split one human across rows. Missing
    `oid`, or `tid` ≠ the configured tenant ⇒ 401, no fallback.
  - *First-login provisioning is **lazy, server-side get-or-create** inside the same
    transformation* (unique index on `entra_object_id` + 23505 re-read for the
    multi-replica race), **not** a `POST /api/auth/provision` endpoint the client could
    skip. `role` on insert is always the server-side literal `'User'`. **No "first user
    becomes Admin" rule** (land-grab escalation); the first Admin is a one-time human
    `UPDATE users SET role='Admin'` via psql as the Postgres Entra admin.
  - *Secure-by-default authorization*: `FallbackPolicy = RequireAuthenticatedUser`,
    with `/health` the single explicit `AllowAnonymous` carve-out. Never
    `ValidateIssuer/Audience/Lifetime = false`; the External ID authority-vs-issuer
    host mismatch (`<sub>.ciamlogin.com` vs `<tenantId>.ciamlogin.com`) is fixed with
    explicit `ValidIssuers` if needed, never by disabling validation.
  - ***Key Vault reconciliation of the "put the connection string in KV" request:***
    since §14 the Postgres connection string holds **no credential**, so it stays a
    plain env var — that half of the request is **not applicable by design**, and
    moving a non-secret into KV would regress §14. The Google client secret **is** a
    real secret from a third party that **cannot** use Entra auth → the sanctioned
    Key Vault fallback. It is consumed by **Entra External ID**, not by our apps, so
    **no application identity is granted any role on the vault** (deliberate deviation
    from the literal request; least privilege). KV uses **Entra RBAC only** — no
    access policies, no vault keys, no SAS.
  - *Application runtime secrets remain **zero**.* Tenant IDs, client IDs, authority
    URLs and scope names are **public identifiers** → plain env vars on `todo-api`
    (`AzureAd__*`) and Vite **build args** on `todo-web`; never Container Apps secrets.
  - *`todos` gets **no** owner column.* Per-user todo ownership is a separate feature,
    explicitly out of scope; "add auth" must not be read as "todos become per-user".
  - *SPA*: MSAL redirect flows, `knownAuthorities` required for `*.ciamlogin.com`,
    **`sessionStorage`** token cache (XSS blast radius), `ssoSilent` → interactive
    fallback, and a "Continue with Google" button using `domain_hint=google.com` that
    **must degrade gracefully** if External ID ignores the hint. Client-side route
    hiding is UX only — the API is the enforcement point.
  - *There is **no built-in phone attribute** in External ID* (built-ins are city,
    country, displayName, email, givenName, jobTitle, postalCode, state,
    streetAddress, surname). Phone is a **custom directory-extension attribute**, so
    its claim name is tenant-specific (`extension_<appId>_phoneNumber`) — the API
    resolves it by config key or `_phoneNumber` suffix match, never a hard-coded GUID.
- **Constraints future features must respect:**
  - **The external tenant is not IaC-able in any practical sense.** The
    `Microsoft.AzureActiveDirectory/ciamDirectories` type is preview, creates only an
    empty directory, and **cannot be deployed by the CD service principal or a managed
    identity** (interactive user with Tenant Creator required). The Microsoft Graph
    Bicep extension targets **the deploying credential's tenant** (our workforce
    tenant) and MI-as-federated-credential is workforce-only, so app registrations,
    user flows, custom attributes, the Google IdP, optional claims and admin consent
    are **Portal/Graph, human-run**. Never hand these to devops as Bicep tasks.
  - **Never trust a client-supplied role**, and never cache the role in the token.
    Any new authorization check re-derives the role from Postgres.
  - **New endpoints are authenticated by default** (fallback policy). Adding
    `[AllowAnonymous]` requires an explicit, reviewed justification.
  - **No application identity may be granted a Key Vault role** for the Google secret.
    If a *future* app secret is unavoidable, use the §13.6 pattern (Container Apps
    Key Vault reference + `Key Vault Secrets User` on the Container App MI) and record
    it in this file.
  - The `users` table is reached over the **same single Entra `NpgsqlDataSource`**
    from §14 — no second connection string, no second DB auth path, ever.
  - **`users` now holds PII** (email, display name, phone). Never log it, never put it
    in telemetry dimensions; log the `oid` when a user must be identified.
  - The frontend image is now **environment-specific** (tenant/client IDs are baked at
    build time), extending the existing build-arg ordering constraint and complicating
    §15's "promote the exact image" model for `todo-web` (backend stays promotable).
  - Auth env vars must be applied **imperatively** (`az containerapp update`) as well
    as in Bicep — CD does not apply Bicep (lessons §2c) and the API now **fails fast**
    without them.
  - Roles stay exactly **two** (`User`, `Admin`). Adding a third is a spec change, not
    an implementation detail.

---

## 2026-08-12 — Entra External ID auth: spec verification pass (§16 revision)

Same branch `pipeline/entra-external-id-auth`, same feature as the 2026-08-12 entry
above — this is a **second architect pass** that verified the already-written §16
against the partially-implemented code on disk and closed the gaps it found. It
**does not reverse** any decision in the entry above; every decision there stands.

- **Services added/changed:** **None.** No new Azure resource, SKU, region or cost
  delta beyond what the previous entry already recorded (External ID external
  tenant + `kv-todo-…` Key Vault, both ≈ $0). This pass changed `.pipeline/specs.md`
  only.
- **Key decisions (with rationale):**
  - *`AzureAd:Scopes` is a **fail-fast required** key* (five required keys, not
    four). It is what `[RequiredScope]` binds to, so a blank value would silently
    disable the scope check — an invisible security downgrade. `Instance` must also
    parse as an absolute **https** URI and `TenantId` as a GUID.
  - *Scope enforcement is explicit and applies to **every** authenticated
    controller.* `[RequiredScope(RequiredScopesConfigurationKey = "AzureAd:Scopes")]`
    goes on `TodosController` **as well as** `UsersController`. Audience validation
    alone does not prove the delegated permission was granted; a token for this
    audience without `Todos.ReadWrite` would otherwise reach `/api/todos`.
    Insufficient scope ⇒ **403**, not 401. `TodosController` therefore changes by
    **attributes only** — §3.5's payload contract is untouched.
  - *`AuthOutcomeMiddleware` is a specified, named component* sitting between
    `UseAuthentication()` and `UseAuthorization()`. The claims transformation
    **marks** a bad principal (`todo:auth_failure`) rather than throwing;
    the middleware maps `invalid_principal` ⇒ **401** and
    `role_source_unavailable` ⇒ **503** (fail closed), skips `IAllowAnonymous`
    endpoints so `/health` can never be taken down by a broken token, and is the
    **only** place that mapping exists. Reason: `RequireAuthenticatedUser()` is
    satisfied by any validated token, so a wrong-`tid`/no-`oid` principal would
    otherwise pass the fallback policy as "authenticated with no role".
  - *PII column widths are bounded at the API edge, not the DB.* `email`,
    `display_name`, `phone_number` stay unbounded `text` (never truncate an
    IdP-supplied value); `PUT /api/users/me` enforces `displayName` ≤ 256 and
    `phoneNumber` ≤ 64 via data annotations ⇒ `400 ValidationProblemDetails`.
  - *Explicit supersession map added to the document header*, because §1/§7/§8/§9.2
    still said "no authentication". Those statements are now marked historical;
    **multi-tenancy stays out of scope** (only the auth half of the §8 bullet moved
    in). Restated in the header that **§16 does not supersede §14**: end-user
    identity and the app's own DB connection are different concerns.
  - *Backend file inventory + `todo:` claim-type table are now in the spec*, so the
    ten `Auth/*.cs` files are specified artifacts rather than undocumented code the
    reviewer has to judge without a reference.
- **Payments / PCI — verified, and deliberately NOT designed for:** this pass was
  asked to scope how end-user identity interacts with "existing payment/PCI-relevant
  data access". **Verified against the whole source tree and §1–§14: this
  application has no payment processing, no checkout/order flow, no cardholder
  data, no payment provider integration, and no payment endpoint or column.** The
  PCI DSS material in the repo `README.md` describes the *pipeline's engineering
  standards*, not this app's scope. No payment surface was invented. Instead §16.10
  records **forward constraints** binding on whoever adds payments later: no
  cardholder data in `tododb` (processor tokenization only, never PAN/CVV even
  encrypted); `Admin` must **not** implicitly grant payment/CDE access (a payment
  feature brings its own policy, re-derived from Postgres); no `[AllowAnonymous]`
  on payment endpoints; payment-provider credentials follow the **§13.6 Key Vault
  reference pattern** (Container Apps KV reference + `Key Vault Secrets User` on the
  Container App MI) — which is a *different* pattern from §16.6's custodial-only
  vault; and payment data never enters logs or telemetry.
- **Constraints future features must respect:**
  - **Adding a controller means adding `[RequiredScope(...)]`**, not just relying on
    the fallback policy. Reviewers check for it.
  - **The auth-outcome → HTTP status mapping lives in exactly one place**
    (`AuthOutcomeMiddleware`). Never re-map it in a controller, filter, or exception
    handler, and never let a role-lookup failure degrade to a default role.
  - **`AzureAd:*` keys stay plain, non-secret env vars and stay fail-fast.** Do not
    add a default/fallback value that would let the API boot without auth config.
  - **`Admin` means exactly two endpoints** (`GET /api/users`,
    `PATCH /api/users/{id}/role`). It is not a general "elevated" role; do not
    attach unrelated privileges to it.
  - The payment forward-constraints above are binding if a payments feature is ever
    specified; that feature is a **new cycle**, not an extension of this one.
