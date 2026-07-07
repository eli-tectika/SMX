#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

RG="${1:-${SMX_RG:-Tectica}}"
confirm_subscription

log "Hardening SMX services in '${RG}': switching to private-endpoint-only access..."

# Apply --set overrides to every id in the first argument.
update_ids() {
  local ids="$1"; shift
  local id
  for id in $ids; do
    az resource update --ids "$id" "$@" --output none
    log "  locked: ${id##*/}"
  done
}

# Only touch resources WE created (project=SMX) — the RG may hold other customer resources.

# Storage (ADLS Gen2): no public access, no shared-key auth
update_ids "$(az storage account list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.allowSharedKeyAccess=false properties.networkAcls.defaultAction=Deny

# Cosmos DB: no public access, Entra-only
update_ids "$(az cosmosdb list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.disableLocalAuth=true

# Azure AI Search: no public access (keyless already set at creation)
update_ids "$(az search service list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv)" \
  --set properties.publicNetworkAccess=disabled

# AI Foundry (Cognitive Services): no public access, Entra-only
update_ids "$(az cognitiveservices account list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny properties.disableLocalAuth=true

# Key Vault: no public access
update_ids "$(az keyvault list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny

log "Hardening complete — SMX storage, Cosmos, Search, Foundry, and Key Vault are private-endpoint only."
warn "Re-running deploy.sh re-enables public access (Bicep default); re-run harden.sh afterwards."
