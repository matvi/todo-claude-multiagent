// ---------------------------------------------------------------------------
// Todo app — DEV environment entry point (RESOURCE-GROUP scoped).
//
// Deploys the `-dev` half of the two-environment model (specs.md §15) into the
// SAME resource group as production (`rg-todo-demo`, user-mandated §15.15.1).
//
// WHY THIS IS A SEPARATE TEMPLATE AND NOT A PARAMETERIZED `main.bicep` (§15.5):
//   Production's LIVE configuration has permanently drifted from `main.bicep`
//   by design — §14.2 established that CD does not apply Bicep, it imperatively
//   rolls images with `az containerapp update`. Turning `main.bicep` into a
//   two-environment template would make every dev bootstrap a potential
//   PRODUCTION deployment that silently reverts that drift. The accepted cost
//   is duplication between two top-level templates; it is mitigated by both
//   calling the SAME `infra/modules/*` (where all real resource shape lives)
//   and by CI linting both (`.github/workflows/ci.yml`, `iac` job).
//
//   `infra/main.bicep` is NOT modified by this cycle. Do not "unify" them.
//
// Approved SKU decisions for dev (user, in chat 2026-08-07 — Option A of
// `.pipeline/infra-proposal.md` §2):
//   Postgres Standard_B1ms / 32 GiB / Central US, dedicated + always on
//   ACA Consumption (no workload profiles) | ACR Basic (SHARED with prod)
//   todo-api-dev min 0 / max 1 | todo-web-dev min 0 / max 1 | 0.25 vCPU / 0.5 GiB
//   Est. delta ~+$19-25/month, of which ~$18.18 is an always-on floor.
//
// Provisions FOUR new resources (everything else is `existing` and shared):
//   1. cae-todo-demo-dev     — 2nd Container Apps managed environment (East US)
//   2. appi-todo-demo-dev    — 2nd App Insights component (workspace-based)
//   3. pg-todo-demo-dev-cus01 — 2nd Postgres Flexible Server (Central US) + tododb
//   4. todo-web-dev + todo-api-dev — the two dev Container Apps
//
// SHARED, referenced as `existing`, created by NOTHING here:
//   - the ACR (promotion requires one registry across both environments)
//   - the `log-todo-demo` Log Analytics workspace (standing constraint: never
//     create a second workspace). NOTE the accepted risk from §15.4.2: that
//     workspace has a 1 GB/day ingestion cap which is now SHARED with dev, so
//     heavy dev ingestion can silently stop PRODUCTION telemetry for the rest
//     of that UTC day. Detection KQL + the cap-raise lever are in
//     `.pipeline/infra.md` and `infra/README.md`.
//
// NOT declared here, on purpose (identical convention to `main.bicep`): the
// `AcrPull` and `Monitoring Metrics Publisher` role assignments for the two new
// dev identities, and the in-database `pgaadauth_create_principal('todo-api-dev')`
// bootstrap. The CD service principal holds only `Contributor` and cannot write
// role assignments, and an in-DB principal has no ARM surface at all. Those are
// Phases B/C/D in infra/README.md — human-run, once.
// ---------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Azure region for the dev compute/observability resources. Defaults to the RG location. Must be eastus to match production.')
param location string = resourceGroup().location

@description('Azure region for the DEV Postgres Flexible Server. Defaults to centralus and deliberately does NOT default to `location`: this subscription has a PostgreSQL Flexible Server offer restriction in East US (§15.2), which is exactly why production lives in Central US. Cross-region is fine — Postgres is reached over its public endpoint with required TLS, not a VNet.')
param postgresLocation string = 'centralus'

@description('Base name token used in non-unique resource names.')
param namePrefix string = 'todo-demo'

@description('Suffix appended to globally-unique names. Same derivation as main.bicep so the default resolves to the SAME shared ACR in this resource group.')
param uniqueSuffix string = uniqueString(resourceGroup().id)

@description('Name of the EXISTING, SHARED Azure Container Registry. NOT created here — one registry serves both environments because artifact promotion requires it.')
param acrName string = toLower('acrtododemo${uniqueSuffix}')

@description('Name of the EXISTING, SHARED Log Analytics workspace. NOT created here — the standing constraint forbids a second workspace.')
param logAnalyticsName string = 'log-${namePrefix}'

@description('DEV Container Apps managed environment name. A second managed environment is the security/network boundary that isolates dev compute from prod (§15.4.1); on the Consumption plan it has no standing charge.')
param acaEnvironmentDevName string = 'cae-${namePrefix}-dev'

@description('DEV Application Insights component name (workspace-based, backed by the shared log-todo-demo).')
param appInsightsDevName string = 'appi-${namePrefix}-dev'

@description('DEV PostgreSQL Flexible Server name — globally unique, and immutable once created. Pinned, not derived, so every future deploy targets the same server.')
param postgresServerName string = 'pg-${namePrefix}-dev-cus01'

@description('DEV logical database name. Same name as production, on a DIFFERENT server (§15.3).')
param databaseName string = 'tododb'

@description('PostgreSQL administrator login for the dev server. Same login NAME as prod, different server and — mandatorily — a DIFFERENT password.')
param postgresAdminUser string = 'todoadmin'

@description('DEV PostgreSQL administrator password. Secure — supplied at deploy time from the GitHub secret PGADMIN_PASSWORD_DEV, NEVER committed and NEVER written to main.dev.parameters.json. MUST differ from production\'s PGADMIN_PASSWORD: a dev credential leak must not reach production (§15.6). This is a deploy-time break-glass credential only — the application itself has no password path to Postgres at all (§14).')
@secure()
param postgresAdminPassword string

// --- Postgres Microsoft Entra administrator (dual-auth, §13.4/§15.6) --------
// Non-secret identity metadata (an object ID and a UPN are not credentials).
// Deliberately the SAME HUMAN identity as production's (§15.15 assumption 4):
// reusing the human admin across servers is correct; reusing an *application*
// identity across servers would not be, and is forbidden (§15.6).
@description('Object ID of the Postgres Entra administrator (user or group).')
param postgresEntraAdminObjectId string

@description('Principal name of the Postgres Entra administrator (UPN for a user).')
param postgresEntraAdminPrincipalName string

@description('Postgres Entra administrator principal type.')
@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param postgresEntraAdminPrincipalType string = 'User'

@description('Tenant ID hosting the Postgres Entra administrator.')
param postgresEntraAdminTenantId string

@description('DEV backend image. Public placeholder for the first (bootstrap) deploy; CD overrides with the real ACR image tag todo-api:<sha>.')
param todoApiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('DEV frontend image. Public placeholder for the first (bootstrap) deploy; CD overrides with the real ACR image tag todo-web:<sha>-dev.')
param todoWebImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Container ingress target port. Defaults to 8080, matching the real app images (nginx/Kestrel per the Dockerfiles). Override to 80 ONLY for the bootstrap deploy still using the mcr.microsoft.com/k8se/quickstart placeholder, which listens on 80 — otherwise the revision never passes its health probe and provisioning times out. Identical caveat to main.bicep.')
param containerTargetPort int = 8080

@description('DEV backend (todo-api-dev) minimum replicas. 0 — dev scales to zero when idle, which is why dev compute costs ~$0 at rest. Cold start is absorbed by verify-dev\'s retry/backoff.')
param apiMinReplicas int = 0

@description('DEV backend (todo-api-dev) maximum replicas. 1 — dev is deliberately cheaper than prod (§15.10).')
param apiMaxReplicas int = 1

@description('DEV frontend (todo-web-dev) minimum replicas. 0 — scale to zero.')
param webMinReplicas int = 0

@description('DEV frontend (todo-web-dev) maximum replicas. 1.')
param webMaxReplicas int = 1

// --- SHARED, EXISTING resources — this template creates NEITHER -------------

@description('Shared Azure Container Registry. One registry across both environments is what makes true artifact promotion possible: deploy-dev builds todo-api:<sha> once, deploy-prod re-points production at that exact tag without rebuilding.')
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
}

@description('Shared Log Analytics workspace. Dev and prod logs stay separable by the ContainerAppConsoleLogs_CL / ContainerAppSystemLogs_CL column EnvironmentName_s (== "cae-todo-demo-dev" for dev).')
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsName
}

// --- 1. DEV Container Apps managed environment (Consumption) ---------------
//        A SECOND managed environment, not a shared one: in ACA the environment
//        is the security/network boundary (shared VNet, internal DNS namespace,
//        log destination, certificates, platform upgrade schedule). Co-locating
//        would give unreviewed dev code an internal-DNS network path to
//        production compute, and would make every environment-level change
//        impossible to rehearse. On Consumption this boundary costs $0 standing.
//
//        Consequence the CD workflow must handle: this environment gets its OWN,
//        different `defaultDomain`, so dev FQDNs are NOT predictable and must be
//        read back with `az containerapp show` at deploy time. Never hardcode one.
module acaEnvDev 'modules/acaEnvironment.bicep' = {
  name: 'acaEnvironmentDev'
  params: {
    name: acaEnvironmentDevName
    location: location
    logAnalyticsWorkspaceId: law.id
  }
}

// --- 2. DEV Application Insights (workspace-based; reuses log-todo-demo) ----
//        Same module as prod, unchanged: no second workspace, no SKU, no
//        standing fee, and `DisableLocalAuth: true` so the instrumentation key
//        in the connection string is an identifier, not a usable credential.
//        Ingestion therefore requires todo-api-dev's MI holding "Monitoring
//        Metrics Publisher" on THIS component only (manual Phase C).
module appinsightsDev 'modules/appinsights.bicep' = {
  name: 'appinsightsDev'
  params: {
    name: appInsightsDevName
    location: location
    workspaceResourceId: law.id
  }
}

// --- 3. DEV Postgres Flexible Server + tododb + firewall rule --------------
//        Same module as prod, unchanged, same SKU (Standard_B1ms / 32 GiB /
//        PG16 / no HA / LRS 7-day PITR) — already the absolute floor of the
//        service, there is nothing cheaper to choose.
//
//        A DEDICATED server (not a second database on the prod server) is the
//        whole point: `pgaadauth` principals are SERVER-GLOBAL, so a shared
//        server would leave the dev identity a principal on the production
//        server with CONNECT on prod's database by PostgreSQL default —
//        isolation would then rest on a human remembering a REVOKE that has no
//        IaC representation and no drift detection.
//
//        Dev data is synthetic and disposable: no prod data is ever copied in,
//        and "recovery" for dev means deleting it and letting Migrate() rebuild.
module postgresDev 'modules/postgres.bicep' = {
  name: 'postgresDev'
  params: {
    name: postgresServerName
    location: postgresLocation
    administratorLogin: postgresAdminUser
    administratorPassword: postgresAdminPassword
    databaseName: databaseName
    entraAdminObjectId: postgresEntraAdminObjectId
    entraAdminPrincipalName: postgresEntraAdminPrincipalName
    entraAdminPrincipalType: postgresEntraAdminPrincipalType
    entraAdminTenantId: postgresEntraAdminTenantId
  }
}

// The todo-api-dev Container App name is also its system-assigned managed
// identity's name — i.e. the Postgres role created by pgaadauth_create_principal
// on the DEV server (Phase D) and the Username= in the passwordless connection
// string below MUST all match it. That four-way invariant now holds twice,
// independently, and the two sets NEVER mix: `todo-api-dev` is a principal only
// on pg-todo-demo-dev-cus01; `todo-api` is a principal only on pg-todo-demo-cus01.
var todoApiDevName = 'todo-api-dev'
var todoWebDevName = 'todo-web-dev'

// PASSWORDLESS, NON-SECRET connection string (§14). There is NO `Password=` and
// NO `Passfile=` — the application has no password path to Postgres at all
// anymore; it authenticates with an Entra token from its managed identity via
// Npgsql's UsePasswordProvider. Because it holds no credential it is delivered
// as a PLAIN env var: not a Container Apps secret, not a Key Vault reference.
//
// `Postgres__UseEntraAuth` is deliberately ABSENT. That configuration key was
// DELETED from the application in §14 — it no longer exists in the code. Do not
// reintroduce it here; it would be dead config that implies a password fallback
// which the app cannot perform.
var todoDbDevConnectionString = 'Host=${postgresDev.outputs.fqdn};Port=5432;Database=${databaseName};Username=${todoApiDevName};Ssl Mode=Require;Trust Server Certificate=true'

// Only wire an ACR registry credential when the image actually comes from that
// ACR. Guard reused VERBATIM from main.bicep, and it matters just as much here:
// on the bootstrap deploy both images default to a public MCR placeholder, and
// attaching an ACR `registries` entry with `identity: system` for an image ACA
// never pulls from that registry still makes ACA try to resolve the managed
// identity's AcrPull access during provisioning. That role assignment is
// deliberately granted AFTER this deploy (Phase B, using this template's own
// principalId outputs), so on a placeholder-image deploy it does not exist yet,
// and the revision hangs until "Operation expired".
var todoWebDevUsesAcr = startsWith(todoWebImage, acr.properties.loginServer)
var todoApiDevUsesAcr = startsWith(todoApiImage, acr.properties.loginServer)

// --- 4. DEV frontend Container App (todo-web-dev) — deployed FIRST so its ---
//        FQDN is known when the backend's CORS allow-list is set below.
module todoWebDev 'modules/containerApp.bicep' = {
  name: 'todoWebDev'
  params: {
    name: todoWebDevName
    location: location
    environmentId: acaEnvDev.outputs.id
    image: todoWebImage
    targetPort: containerTargetPort
    // External ingress: the GitHub-hosted runner that executes `verify-dev` and
    // a developer's browser both need to reach dev. Internal-only ingress would
    // require a VNet, contradicting the standing no-VNet cost posture. Dev is
    // publicly reachable and contains no real data. TLS is still mandatory —
    // the module sets allowInsecure: false.
    externalIngress: true
    minReplicas: webMinReplicas
    maxReplicas: webMaxReplicas
    cpu: '0.25'
    memory: '0.5Gi'
    registryServer: todoWebDevUsesAcr ? acr.properties.loginServer : ''
  }
}

// --- 5. DEV backend Container App (todo-api-dev) ---------------------------
//        Managed-identity-first (§13/§14): ZERO Container Apps secrets, zero
//        application runtime secrets, no Key Vault. Both config values below are
//        NON-SECRET plain env vars:
//          - ConnectionStrings__TodoDb: passwordless (Entra token at runtime)
//          - APPLICATIONINSIGHTS_CONNECTION_STRING: non-secret (local auth off)
//        CORS is strictly per-environment: allow-list origin 0 is the DEV
//        frontend FQDN only. It never contains a production origin, and the
//        production allow-list never contains a dev origin.
module todoApiDev 'modules/containerApp.bicep' = {
  name: 'todoApiDev'
  params: {
    name: todoApiDevName
    location: location
    environmentId: acaEnvDev.outputs.id
    image: todoApiImage
    targetPort: containerTargetPort
    externalIngress: true
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    cpu: '0.25'
    memory: '0.5Gi'
    registryServer: todoApiDevUsesAcr ? acr.properties.loginServer : ''
    envVars: [
      {
        name: 'Cors__AllowedOrigins__0'
        value: 'https://${todoWebDev.outputs.fqdn}'
      }
      {
        name: 'ConnectionStrings__TodoDb'
        value: todoDbDevConnectionString
      }
      {
        name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
        value: appinsightsDev.outputs.connectionString
      }
    ]
  }
}

// --- Outputs ---------------------------------------------------------------
// The principalId and appInsightsDevId outputs are consumed by the MANUAL
// Phase B/C role grants (infra/README.md) — the CD service principal cannot
// write role assignments, so a human reads these and runs the grants once.
output acaEnvironmentDevId string = acaEnvDev.outputs.id
output acaEnvironmentDevName string = acaEnvDev.outputs.name
output todoApiDevName string = todoApiDev.outputs.name
output todoWebDevName string = todoWebDev.outputs.name
output todoApiDevFqdn string = todoApiDev.outputs.fqdn
output todoWebDevFqdn string = todoWebDev.outputs.fqdn
output todoApiDevPrincipalId string = todoApiDev.outputs.principalId
output todoWebDevPrincipalId string = todoWebDev.outputs.principalId
output postgresDevServerName string = postgresDev.outputs.serverName
output postgresDevFqdn string = postgresDev.outputs.fqdn
output appInsightsDevId string = appinsightsDev.outputs.id
output appInsightsDevName string = appinsightsDev.outputs.name
// The shared ACR, echoed for convenience in the Phase B grant (its scope).
output sharedAcrName string = acr.name
output sharedAcrLoginServer string = acr.properties.loginServer
