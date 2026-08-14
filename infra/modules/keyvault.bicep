// ---------------------------------------------------------------------------
// Azure Key Vault — CUSTODIAL store of record for the Google OAuth client
// secret (specs §16.6). Approved SKU: **Standard** (user, 2026-08-12).
//
// WHY THIS VAULT EXISTS AT ALL. The standing rule (§13.3) is managed-identity
// first: no application runtime secret should exist, and none does. The Google
// OAuth client secret is the one value that genuinely cannot authenticate via
// Entra — it is issued by Google, a third party with no Entra relationship —
// which is exactly the §13.6 fallback case.
//
// WHO CONSUMES IT: **Microsoft Entra External ID**, once, at IdP configuration
// time, plus the human operator who pastes it in. **No application of ours ever
// reads it.** Per §16.6, NEITHER `todo-api`'s NOR `todo-web`'s managed identity
// is granted any role on this vault. That is a deliberate, documented deviation
// from the literal original request ("the API's managed identity gets read
// permission"): the API has no use for the value, and granting it would create
// an exfiltration path for a Google credential for zero benefit.
//
// WHY STANDARD AND NOT PREMIUM: Premium's only differentiator is HSM-protected
// *keys*. We store a *secret*, and secrets are stored identically under both
// SKUs — Premium would cost more per feature and deliver literally nothing here.
// Managed HSM (~$3k/month) is absurd for one demo secret. Standard bills ~$0.03
// per 10,000 operations and this vault sees a handful of operations per YEAR.
//
// WHAT IS DELIBERATELY *NOT* IN THIS FILE:
//   - Any role assignment. §11.6: agents never create role assignments; the CD
//     service principal holds only Contributor and cannot write them anyway.
//     A human grants themself `Key Vault Secrets Officer` out-of-band
//     (infra/README.md Phase 5).
//   - Any secret VALUE or any `Microsoft.KeyVault/vaults/secrets` child
//     resource. Agents never touch secret material; the value is written once
//     by a human via `az keyvault secret set`.
//   - Any access policy. RBAC authorization only (`enableRbacAuthorization`).
//   - Any private endpoint / VNet integration. This architecture has no VNet
//     (§8); a private endpoint would cost ~$7.30/month, roughly 2x the entire
//     rest of the demo's bill, to protect a value a human writes once a year.
// ---------------------------------------------------------------------------

@description('Key Vault name — GLOBALLY unique, 3-24 chars, alphanumeric and hyphens, must start with a letter.')
@minLength(3)
@maxLength(24)
param name string

@description('Azure region.')
param location string

@description('Entra tenant ID that owns this vault (the WORKFORCE tenant that owns the subscription — NOT the External ID customer tenant). Defaults to the deploying subscription\'s tenant.')
param tenantId string = subscription().tenantId

@description('Key Vault SKU. Standard per specs §16.6 / §11.9 — Premium only adds HSM-backed KEYS, which does nothing for a SECRET.')
@allowed([
  'standard'
  'premium'
])
param sku string = 'standard'

@description('Soft-delete retention in days (7-90). 7 = the minimum, chosen so a mis-named demo vault can be purged and recreated quickly.')
@minValue(7)
@maxValue(90)
param softDeleteRetentionInDays int = 7

@description('Purge protection. DELIBERATELY false for the demo: soft-delete already gives recoverability, while purge protection is IRREVERSIBLE once enabled and would make a mis-named vault impossible to clean up. Revisit for production.')
param enablePurgeProtection bool = false

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: sku
    }
    // Entra RBAC, never legacy access policies (§16.6). Access to the vault is
    // itself Entra-authenticated via the caller's identity — no vault access
    // key, no SAS token, no shared credential of any kind.
    enableRbacAuthorization: true
    // With RBAC authorization on, `accessPolicies` must be present but empty.
    accessPolicies: []
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    // See the param description — intentionally off, not an oversight.
    enablePurgeProtection: enablePurgeProtection ? true : null
    // No VNet exists in this architecture (§8), so there is nothing to
    // private-link to. Authorization is the control here, not network reach.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
  }
}

output id string = vault.id
output name string = vault.name
// Non-secret: the vault URI is an endpoint address, not a credential. Reaching
// it still requires an Entra token with an RBAC data-plane role.
output vaultUri string = vault.properties.vaultUri
