#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Target resource group (default: Tectica). Override: ./deploy.sh <RG>  or  SMX_RG=<RG> ./deploy.sh
RG="${1:-${SMX_RG:-Tectica}}"
ensure_bicep
confirm_subscription

DEPLOYER_IP="$(detect_ip)"
DEPLOY_NAME="smx-$(date +%Y%m%d%H%M%S)"
log "Deploying SMX into resource group '${RG}' (region swedencentral, deployer IP: ${DEPLOYER_IP:-<none>})..."

# Everything after <rg> is forwarded verbatim to az, so extra Bicep parameters work:
#   ./deploy.sh Tectica -p proxySearchKeySecretUri=<uri> -p deploySearchKeyRbac=true
# Without this the proxy params that main.bicep declares are unreachable from the script.
az deployment group create \
  --resource-group "${RG}" \
  --name "${DEPLOY_NAME}" \
  --template-file "${INFRA_DIR}/main.bicep" \
  --parameters "${INFRA_DIR}/main.bicepparam" \
  --parameters deployerIpAddress="${DEPLOYER_IP}" \
  "${@:2}"

log "Deploy '${DEPLOY_NAME}' complete."
az deployment group show -g "${RG}" --name "${DEPLOY_NAME}" --query "properties.outputs" -o json
