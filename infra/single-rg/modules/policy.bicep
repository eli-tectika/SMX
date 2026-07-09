// Lightweight governance guardrails (SMX-009, dev posture).
// Audit-only assignments of built-in policies at this resource group's scope: they surface any
// PaaS resource that (re-)enables public network access without ever blocking a deployment —
// dev intentionally allows a deployer-IP window until scripts/harden.sh closes it, so 'deny'
// would fight the deploy flow. Defender for Cloud free CSPM (MCSB / 'SecurityCenterBuiltIn')
// is already assigned at subscription scope and provides the secure-score layer on top.
// Definition IDs verified against the live built-in catalog (2026-07-09).

targetScope = 'resourceGroup'

var audits = [
  {
    name: 'audit-storage-public-access'
    defId: 'b2982f36-99f2-4db5-8eff-283140c09693' // Storage accounts should disable public network access
  }
  {
    name: 'audit-cosmos-public-access'
    defId: '797b37f7-06b8-444c-b1ad-fc62867f335a' // Azure Cosmos DB should disable public network access
  }
  {
    name: 'audit-keyvault-public-access'
    defId: '405c5871-3e91-4644-8a63-58e19d68ff5b' // Azure Key Vault should disable public network access
  }
  {
    name: 'audit-search-public-access'
    defId: 'ee980b6d-0eca-4501-8d54-f6290fd512c3' // Azure AI Search services should disable public network access
  }
  {
    name: 'audit-acr-private-link'
    defId: 'e8eef0a8-67cf-4eb4-9386-14b0e78733d4' // Container registries should use private link
  }
]

resource assignments 'Microsoft.Authorization/policyAssignments@2024-04-01' = [for a in audits: {
  name: a.name
  properties: {
    displayName: a.name
    policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policyDefinitions', a.defId)
    enforcementMode: 'Default' // all effects are Audit/AuditIfNotExists — never blocks
  }
}]
