#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

ENV="$(require_env_arg "${1:-}")"

log "Checking tooling..."
ensure_bicep

log "Linting Bicep (offline)..."
az bicep build --file "${INFRA_DIR}/main.bicep" --stdout > /dev/null
log "Bicep OK."

log "Checking Azure login + subscription..."
confirm_subscription

log "Registering resource providers..."
for rp in Microsoft.Network Microsoft.OperationalInsights Microsoft.Insights \
          Microsoft.Storage Microsoft.DocumentDB Microsoft.Search \
          Microsoft.CognitiveServices Microsoft.App Microsoft.ContainerRegistry \
          Microsoft.Web Microsoft.KeyVault Microsoft.ManagedIdentity; do
  az provider register --namespace "$rp" >/dev/null
  log "  registering ${rp}"
done

DEPLOYER_IP="$(detect_ip)"
log "Detected deployer IP: ${DEPLOYER_IP:-<unknown>}"

log "Running what-if for env '${ENV}'..."
az deployment sub what-if \
  --location "${LOCATION}" \
  --template-file "${INFRA_DIR}/main.bicep" \
  --parameters "${INFRA_DIR}/env/${ENV}.bicepparam" \
  --parameters deployerIpAddress="${DEPLOYER_IP}"

log "Preflight complete."
