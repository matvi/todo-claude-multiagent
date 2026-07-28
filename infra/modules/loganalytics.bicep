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

@description('Daily ingestion cap in GB — a hard cost circuit-breaker (approved by user in chat 2026-07-28, OVERRIDING the proposal §3e "no cap" default). Once the cap is hit on a given UTC day, further ingestion (including App Insights telemetry) is SILENTLY DROPPED until the next day. This is the accepted tradeoff for a guaranteed cost ceiling against unexpected charges. Set to -1 to disable the cap.')
param dailyQuotaGb int = 1

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: name
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    // Hard daily ingestion ceiling (cost circuit-breaker). Telemetry is dropped
    // for the rest of the UTC day once this is exceeded — accepted tradeoff.
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    features: {
      searchVersion: 1
    }
  }
}

output id string = law.id
output name string = law.name
