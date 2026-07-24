// Azure Container Apps managed environment (Consumption workload profile).
// The environment itself has no standing charge; usage bills via the apps.
// Reads the Log Analytics shared key inline via listKeys() so the key is never
// passed around as a plain parameter or emitted as an output.

@description('Managed environment name.')
param name string

@description('Azure region.')
param location string

@description('Resource ID of the Log Analytics workspace that backs app logs.')
param logAnalyticsWorkspaceId string

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsWorkspaceId, '/'))
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
    // Consumption-only environment: no explicit workloadProfiles block means
    // the default Consumption profile is used (specs.md §11.9). No Dedicated.
  }
}

output id string = env.id
output name string = env.name
