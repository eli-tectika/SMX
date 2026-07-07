#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Ensure an Entra app registration for the regsync Function App and enforce Easy Auth (single-RG variant).
# Entra app objects are Graph resources (not ARM), so this is a script step.
RG="${1:-${SMX_RG:-Tectica}}"
ENV_LABEL="${SMX_ENV:-prod}"
confirm_subscription
APP="func-${NAME_PREFIX}-${ENV_LABEL}-regsync-${REGION_SHORT}"
APP_REG_NAME="${NAME_PREFIX}-${ENV_LABEL}-regsync-auth"

log "Ensuring Entra app registration '${APP_REG_NAME}'..."
CLIENT_ID="$(az ad app list --display-name "${APP_REG_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${CLIENT_ID}" ]; then
  CLIENT_ID="$(az ad app create --display-name "${APP_REG_NAME}" \
    --identifier-uris "api://${APP_REG_NAME}" --query appId -o tsv)"
  log "Created app registration ${CLIENT_ID}"
fi

TENANT_ID="$(az account show --query tenantId -o tsv)"
log "Enforcing Easy Auth on ${APP} (audience api://${APP_REG_NAME})..."
az webapp auth update -g "${RG}" -n "${APP}" \
  --enabled true --action Return401 \
  --aad-allowed-token-audiences "api://${APP_REG_NAME}" \
  --aad-client-id "${CLIENT_ID}" \
  --aad-token-issuer-url "https://login.microsoftonline.com/${TENANT_ID}/v2.0" --output none

warn "Callers (ACA orchestrator) must present an Entra token for audience api://${APP_REG_NAME}."
log "Keep Bicep in sync: redeploy with -p authClientId=${CLIENT_ID}, then run harden.sh."
