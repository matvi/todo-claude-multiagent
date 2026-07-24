// Azure Container Registry (Basic SKU) — shared registry for both images.
// Admin user is deliberately DISABLED; images are pulled via each Container
// App's system-assigned managed identity with an AcrPull role assignment that
// is granted manually during human bootstrap (see infra/README.md), not here.

@description('Globally-unique ACR name (alphanumeric, 5-50 chars).')
param name string

@description('Azure region.')
param location string

@description('ACR SKU. Basic per the cost guardrails in specs.md §11.9.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param sku string = 'Basic'

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: name
  location: location
  sku: {
    name: sku
  }
  properties: {
    adminUserEnabled: false
  }
}

output loginServer string = acr.properties.loginServer
output name string = acr.name
output id string = acr.id
