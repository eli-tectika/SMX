#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
confirm_subscription
ENV_RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

log "Hardening '${ENV}': switching data/AI services to private-endpoint-only access..."

# Apply --set overrides to every id in the first argument.
update_ids() {
  local ids="$1"; shift
  local id
  for id in $ids; do
    az resource update --ids "$id" "$@" --output none
    log "  locked: ${id##*/}"
  done
}

# Storage (ADLS Gen2): no public access, no shared-key auth
update_ids "$(az storage account list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.allowSharedKeyAccess=false properties.networkAcls.defaultAction=Deny

# Cosmos DB: no public access, Entra-only
update_ids "$(az cosmosdb list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.disableLocalAuth=true

# Azure AI Search: no public access (lowercase value for this RP). Local auth is
# already disabled at creation in ai.bicep; it can't be toggled here while authOptions is set.
update_ids "$(az search service list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=disabled

# AI Foundry (Cognitive Services): no public access, Entra-only
update_ids "$(az cognitiveservices account list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny properties.disableLocalAuth=true

# Key Vault: no public access
update_ids "$(az keyvault list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled properties.networkAcls.defaultAction=Deny

# Function Apps (Search Proxy + Regulatory Sync): no public inbound. Runtime storage is
# already keyless + private-endpoint (functions.bicep), so the storage lockdown above is safe;
# the apps reach it and each other over the VNet / private endpoints.
update_ids "$(az functionapp list -g "$ENV_RG" --query '[].id' -o tsv)" \
  --set properties.publicNetworkAccess=Disabled

log "Hardening complete — storage, Cosmos, Search, Foundry, Key Vault, and Functions are private-endpoint only."
warn "Re-running deploy.sh re-enables public access (Bicep default); re-run harden.sh after any redeploy."
