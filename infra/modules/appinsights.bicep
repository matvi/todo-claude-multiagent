// Application Insights component — workspace-based, backed by the EXISTING
// `log-todo-demo` Log Analytics workspace (NO second workspace; specs §12.6).
//
// Local (shared-key) auth is DISABLED (`DisableLocalAuth: true`, §13.5) so the
// InstrumentationKey embedded in the connection string is not a usable
// credential — it is only a resource identifier. Telemetry ingestion therefore
// requires the `todo-api` managed identity holding the "Monitoring Metrics
// Publisher" role on this component. That role assignment is a MANUAL human
// bootstrap step (see infra/README.md Phase 4), consistent with the AcrPull
// convention — it is NOT authored in Bicep (the CD/infra principal holds only
// Contributor and cannot write role assignments).
//
// SAMPLING NOTE (100% requested, approved 2026-07-28):
//   For the Azure Monitor OpenTelemetry distro
//   (`Azure.Monitor.OpenTelemetry.AspNetCore`) the sampling rate is controlled
//   in APPLICATION CODE via `AzureMonitorOptions.SamplingRatio` (default 1.0 =
//   100%), NOT by an ARM property on this component. The `SamplingPercentage`
//   property that DOES exist on `Microsoft.Insights/components` drives the
//   classic/legacy ingestion-sampling path and does NOT affect the OTel
//   exporter, so it is intentionally left UNSET here. 100% sampling is the
//   already-merged backend default; no ARM-level setting is required. (This is
//   also documented in infra/README.md so the choice is not mistaken for an
//   omission.)

@description('Application Insights component name (e.g. appi-todo-demo).')
param name string

@description('Azure region. Must match the backing Log Analytics workspace region (App Insights follows its workspace region).')
param location string

@description('Resource ID of the EXISTING Log Analytics workspace (log-todo-demo) that backs this workspace-based component. NO second workspace is created.')
param workspaceResourceId string

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: name
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // Workspace-based (not classic): telemetry is stored in the shared
    // log-todo-demo workspace.
    WorkspaceResourceId: workspaceResourceId
    IngestionMode: 'LogAnalytics'
    // §13.5 — shared-key ingestion off; Entra (managed identity) required.
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// Non-secret now that local auth is disabled — it identifies the resource +
// ingestion endpoint but is not an ingestion credential (§13.5).
output connectionString string = appi.properties.ConnectionString
output id string = appi.id
output name string = appi.name
