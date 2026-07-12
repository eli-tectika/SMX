#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"
ensure_bicep
confirm_subscription

DEPLOYER_IP="${DEPLOYER_IP:-$(require_deployer_ip "$@")}"
DEPLOY_NAME="smx-${ENV}-$(date +%Y%m%d%H%M%S)"
log "Deploying env '${ENV}' to ${LOCATION} (deployer IP: ${DEPLOYER_IP:-<none>})..."

az deployment sub create \
  --name "${DEPLOY_NAME}" \
  --location "${LOCATION}" \
  --template-file "${INFRA_DIR}/main.bicep" \
  --parameters "${INFRA_DIR}/env/${ENV}.bicepparam" \
  --parameters deployerIpAddress="${DEPLOYER_IP}"

log "Deploy '${DEPLOY_NAME}' complete."
az deployment sub show --name "${DEPLOY_NAME}" --query "properties.outputs" -o json
