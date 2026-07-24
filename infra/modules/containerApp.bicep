// Reusable module for a single Azure Container App (Consumption).
// Each app gets a system-assigned managed identity. Images are pulled from ACR
// via that identity; the AcrPull role assignment is granted MANUALLY during
// human bootstrap (see infra/README.md), NOT created here, because the CD
// service principal only holds Contributor and cannot write role assignments.

@description('Container App name.')
param name string

@description('Azure region.')
param location string

@description('Resource ID of the Container Apps managed environment.')
param environmentId string

@description('Fully-qualified container image reference to run.')
param image string

@description('Container port that ingress targets.')
param targetPort int = 8080

@description('Whether ingress is exposed publicly.')
param externalIngress bool = true

@description('Minimum replica count.')
@minValue(0)
param minReplicas int

@description('Maximum replica count.')
@minValue(1)
param maxReplicas int

@description('vCPU per replica (as a string, e.g. "0.25").')
param cpu string = '0.25'

@description('Memory per replica (e.g. "0.5Gi").')
param memory string = '0.5Gi'

@description('ACR login server for managed-identity image pulls. Empty to skip.')
param registryServer string = ''

@description('Plain environment variables: [{ name, value }].')
param envVars array = []

@description('Secret-backed environment variables: [{ name, secretRef }].')
param secretEnvVars array = []

@description('Map of Container Apps secret name -> value. Secure.')
@secure()
param secrets object = {}

var secretArray = [for item in items(secrets): {
  name: item.key
  value: item.value
}]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: externalIngress
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: empty(registryServer) ? [] : [
        {
          server: registryServer
          identity: 'system'
        }
      ]
      secrets: secretArray
    }
    template: {
      containers: [
        {
          name: name
          image: image
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(envVars, secretEnvVars)
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
output principalId string = app.identity.principalId
output name string = app.name
