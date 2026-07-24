// Azure Database for PostgreSQL Flexible Server — Burstable B1ms, PG16,
// 32 GiB, no HA, no read replica, locally-redundant backup (specs.md §11.9).
// Public access + "allow Azure services" firewall rule + required TLS
// (architecture-memory 2026-07-23). No VNet / private endpoint.

@description('Globally-unique Postgres Flexible Server name.')
param name string

@description('Azure region.')
param location string

@description('Administrator login name.')
param administratorLogin string

@description('Administrator password. Secure — supplied at deploy time from a secret.')
@secure()
param administratorPassword string

@description('Logical database name.')
param databaseName string = 'tododb'

@description('Storage size in GiB. Smallest usable size per the cost guardrails.')
param storageSizeGB int = 32

@description('PostgreSQL major version.')
param postgresVersion string = '16'

@description('Compute SKU. Burstable Standard_B1ms per approved SKU decision.')
param skuName string = 'Standard_B1ms'

@description('Backup retention in days.')
@minValue(7)
@maxValue(35)
param backupRetentionDays int = 7

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: name
  location: location
  sku: {
    name: skuName
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    version: postgresVersion
    storage: {
      storageSizeGB: storageSizeGB
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: pg
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// "Allow public access from any Azure service within Azure to this server."
// The 0.0.0.0 - 0.0.0.0 range is the special "allow Azure services" rule so
// Container Apps egress can reach Postgres without pinning specific IPs.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: pg
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output fqdn string = pg.properties.fullyQualifiedDomainName
output serverName string = pg.name
output databaseName string = database.name
