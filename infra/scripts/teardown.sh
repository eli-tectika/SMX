#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
confirm_subscription

ENV_RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
HUB_RG="rg-${NAME_PREFIX}-hub-${REGION_SHORT}"
HUB_VNET="vnet-${NAME_PREFIX}-hub-${REGION_SHORT}"
SPOKE_VNET="vnet-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

# This environment's hub-side peering and per-env private DNS zone links live in
# the shared hub RG. Deleting the spoke RG does NOT remove them, so they must be
# cleaned up whenever the hub is retained, or they dangle pointing at a deleted VNet.
cleanup_hub_side() {
  az network vnet peering delete \
    --resource-group "${HUB_RG}" --vnet-name "${HUB_VNET}" \
    --name "peer-to-${SPOKE_VNET}" 2>/dev/null || true
  local zone
  for zone in $(az network private-dns zone list --resource-group "${HUB_RG}" --query "[].name" -o tsv 2>/dev/null || true); do
    az network private-dns link vnet delete \
      --resource-group "${HUB_RG}" --zone-name "${zone}" \
      --name "link-${NAME_PREFIX}-${ENV}" --yes 2>/dev/null || true
  done
  log "Cleaned up hub-side peering and DNS links for '${ENV}'."
}

warn "This will DELETE resource group: ${ENV_RG}"
read -r -p "Type the environment name '${ENV}' to confirm: " reply
[ "$reply" = "$ENV" ] || die "Confirmation failed; aborting."

az group delete --name "${ENV_RG}" --yes
log "Deleted ${ENV_RG}."

remaining="$(az group list --query "[?starts_with(name, 'rg-${NAME_PREFIX}-') && name != '${HUB_RG}'].name" -o tsv)"
if [ -z "${remaining}" ]; then
  warn "No environment resource groups remain."
  read -r -p "Delete the shared hub '${HUB_RG}' too? [y/N]: " hubreply
  case "${hubreply}" in
    y|Y)
      az group delete --name "${HUB_RG}" --yes
      log "Deleted ${HUB_RG}."
      ;;
    *)
      cleanup_hub_side
      log "Keeping hub."
      ;;
  esac
else
  cleanup_hub_side
  log "Keeping hub; still referenced by: ${remaining}"
fi
