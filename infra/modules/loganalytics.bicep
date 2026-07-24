// Log Analytics workspace backing the Container Apps environment.
// Pay-as-you-go (PerGB2018), 30-day retention per specs.md §11.9.

@description('Workspace name.')
param name string

@description('Azure region.')
param location string

@description('Retention in days. Kept at 30 per the cost guardrails.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: name
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    features: {
      searchVersion: 1
    }
  }
}

output id string = law.id
output name string = law.name
