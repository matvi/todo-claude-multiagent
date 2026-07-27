// ---------------------------------------------------------------------------
// Todo app — infrastructure entry point (RESOURCE-GROUP scoped).
//
// The resource group `rg-todo-demo` is created out-of-band by a human during
// bootstrap (`az group create ...`, see infra/README.md); this template does
// NOT create the RG. Approved SKU decisions (user, 2026-07-24):
//   Postgres Standard_B1ms | ACA Consumption | ACR Basic
//   todo-web min1/max2 | todo-api min0/max3
//
// Provisions exactly the 8 approved resources:
//   1. ACR (Basic)                     5. todo-api Container App (backend)
//   2. Log Analytics workspace (30d)   6. todo-web Container App (frontend)
//   3. ACA managed environment         7. Postgres Flexible Server (B1ms)
//   4. tododb database                 8. + AllowAzureServices firewall rule
// (the RG itself is #0, created manually.)
//
// NOTE: the AcrPull role assignment for each Container App's managed identity
// is intentionally NOT declared here — it is a one-time manual bootstrap step
// (the CD service principal only has Contributor and cannot write role
// assignments). See infra/README.md.
// ---------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Azure region for all resources. Defaults to the RG location.')
param location string = resourceGroup().location

@description('Azure region for the Postgres Flexible Server specifically. Defaults to `location`, but can be overridden independently — some subscriptions have Postgres Flexible Server offer restrictions in certain regions (e.g. eastus) that do not affect the other resource types here. Cross-region access is fine since Postgres is reached over its public endpoint with required TLS, not a VNet.')
param postgresLocation string = location

@description('Base name token used in non-unique resource names.')
param namePrefix string = 'todo-demo'

@description('Suffix appended to globally-unique names (ACR, Postgres server).')
param uniqueSuffix string = uniqueString(resourceGroup().id)

@description('ACR name — globally unique, alphanumeric only.')
param acrName string = toLower('acrtododemo${uniqueSuffix}')

@description('PostgreSQL Flexible Server name — globally unique.')
param postgresServerName string = toLower('pg-todo-demo-${uniqueSuffix}')

@description('Log Analytics workspace name.')
param logAnalyticsName string = 'log-${namePrefix}'

@description('Container Apps managed environment name.')
param acaEnvironmentName string = 'cae-${namePrefix}'

@description('PostgreSQL administrator login.')
param postgresAdminUser string = 'todoadmin'

@description('Logical database name.')
param databaseName string = 'tododb'

@description('Backend image. Public placeholder for the first (bootstrap) deploy; CD overrides with the real ACR image tag.')
param todoApiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Frontend image. Public placeholder for the first (bootstrap) deploy; CD overrides with the real ACR image tag.')
param todoWebImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('PostgreSQL administrator password. Secure — supplied at deploy time from a GitHub/pipeline secret, never committed.')
@secure()
param postgresAdminPassword string

@description('Container ingress target port. Defaults to 8080, matching the real app images (nginx/Kestrel per the Dockerfiles). Override to 80 only for a bootstrap deploy still using the mcr.microsoft.com/k8se/quickstart placeholder, which listens on 80 — otherwise the revision never passes its health probe and provisioning times out.')
param containerTargetPort int = 8080

@description('Backend (todo-api) minimum replicas.')
param apiMinReplicas int = 0

@description('Backend (todo-api) maximum replicas.')
param apiMaxReplicas int = 3

@description('Frontend (todo-web) minimum replicas.')
param webMinReplicas int = 1

@description('Frontend (todo-web) maximum replicas.')
param webMaxReplicas int = 2

// --- 1. Azure Container Registry (Basic) -----------------------------------
module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    name: acrName
    location: location
    sku: 'Basic'
  }
}

// --- 2. Log Analytics workspace (PAYG, 30-day) -----------------------------
module logAnalytics 'modules/loganalytics.bicep' = {
  name: 'logAnalytics'
  params: {
    name: logAnalyticsName
    location: location
    retentionInDays: 30
  }
}

// --- 3. Container Apps managed environment (Consumption) --------------------
module acaEnv 'modules/acaEnvironment.bicep' = {
  name: 'acaEnvironment'
  params: {
    name: acaEnvironmentName
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
  }
}

// --- 4 + 7 + 8. Postgres Flexible Server + tododb + firewall rule ----------
module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    name: postgresServerName
    location: postgresLocation
    administratorLogin: postgresAdminUser
    administratorPassword: postgresAdminPassword
    databaseName: databaseName
  }
}

// Assembled Postgres connection string, surfaced only as a Container Apps
// secret on the backend. Built from the secure password param — never output.
var todoDbConnectionString = 'Host=${postgres.outputs.fqdn};Port=5432;Database=${databaseName};Username=${postgresAdminUser};Password=${postgresAdminPassword};SslMode=Require;Trust Server Certificate=true'

// Only wire an ACR registry credential when the image actually comes from
// that ACR. On the bootstrap deploy both images default to a public MCR
// placeholder — attaching an ACR `registries` entry with `identity: system`
// for an image ACA never pulls from that registry still makes ACA try to
// resolve the managed identity's AcrPull access during provisioning. That
// role assignment is deliberately granted AFTER this deploy (Phase 3, using
// its own outputs — see infra/README.md), so on a placeholder-image deploy
// it doesn't exist yet, and the revision hangs until "Operation expired".
var todoWebUsesAcr = startsWith(todoWebImage, registry.outputs.loginServer)
var todoApiUsesAcr = startsWith(todoApiImage, registry.outputs.loginServer)

// --- 6. Frontend Container App (todo-web) — deployed first so its FQDN is ---
//        known when the backend's CORS allow-list is set below.
module todoWeb 'modules/containerApp.bicep' = {
  name: 'todoWeb'
  params: {
    name: 'todo-web'
    location: location
    environmentId: acaEnv.outputs.id
    image: todoWebImage
    targetPort: containerTargetPort
    externalIngress: true
    minReplicas: webMinReplicas
    maxReplicas: webMaxReplicas
    cpu: '0.25'
    memory: '0.5Gi'
    registryServer: todoWebUsesAcr ? registry.outputs.loginServer : ''
  }
}

// --- 5. Backend Container App (todo-api) -----------------------------------
//        DB connection string as a secret -> env ConnectionStrings__TodoDb.
//        CORS allow-list origin 0 = the frontend FQDN from todoWeb above.
module todoApi 'modules/containerApp.bicep' = {
  name: 'todoApi'
  params: {
    name: 'todo-api'
    location: location
    environmentId: acaEnv.outputs.id
    image: todoApiImage
    targetPort: containerTargetPort
    externalIngress: true
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    cpu: '0.25'
    memory: '0.5Gi'
    registryServer: todoApiUsesAcr ? registry.outputs.loginServer : ''
    secrets: {
      'todo-db-connection': todoDbConnectionString
    }
    secretEnvVars: [
      {
        name: 'ConnectionStrings__TodoDb'
        secretRef: 'todo-db-connection'
      }
    ]
    envVars: [
      {
        name: 'Cors__AllowedOrigins__0'
        value: 'https://${todoWeb.outputs.fqdn}'
      }
    ]
  }
}

// --- Outputs consumed by the CD pipeline / printed for humans --------------
output acrLoginServer string = registry.outputs.loginServer
output acrName string = registry.outputs.name
output acaEnvironmentId string = acaEnv.outputs.id
output todoApiFqdn string = todoApi.outputs.fqdn
output todoWebFqdn string = todoWeb.outputs.fqdn
output todoApiName string = todoApi.outputs.name
output todoWebName string = todoWeb.outputs.name
output todoApiPrincipalId string = todoApi.outputs.principalId
output todoWebPrincipalId string = todoWeb.outputs.principalId
output postgresServerName string = postgres.outputs.serverName
