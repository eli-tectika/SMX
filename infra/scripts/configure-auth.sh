#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Ensure an Entra app registration for the regsync Function App and enforce Easy Auth (Return401).
# Entra app objects are Graph resources (not ARM), so this is a script step.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-regsync-${REGION_SHORT}"
APP_REG_NAME="${NAME_PREFIX}-${ENV}-regsync-auth"

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

# --- Search Proxy: its OWN app registration, never regsync's. Separate apps, separate identities,
#     separate audiences — sharing an audience would hand the internet-facing proxy a token the
#     corpus-writing app accepts, destroying exactly the boundary the two identities exist to hold. ---
PROXY_APP="func-${NAME_PREFIX}-${ENV}-searchproxy-${REGION_SHORT}"
PROXY_REG_NAME="${NAME_PREFIX}-${ENV}-searchproxy-auth"

log "Ensuring Entra app registration '${PROXY_REG_NAME}'..."
PROXY_CLIENT_ID="$(az ad app list --display-name "${PROXY_REG_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${PROXY_CLIENT_ID}" ]; then
  PROXY_CLIENT_ID="$(az ad app create --display-name "${PROXY_REG_NAME}" --query appId -o tsv)"
  [ -n "${PROXY_CLIENT_ID}" ] || die "Failed to create the app registration '${PROXY_REG_NAME}'."
  log "Created app registration ${PROXY_CLIENT_ID}"
fi

# The audience is api://<appId>, NOT api://<display-name>: functions.bicep pins the proxy's
# allowedAudiences to 'api://${proxyAuthClientId}' and main.bicep hands the orchestrator that same
# string as SEARCH_PROXY_AUDIENCE. The identifier URI must exist in exactly that form or the token the
# orchestrator asks for is one Entra will not mint. Setting it here keeps the script's state and the
# Bicep's state identical, so the follow-up redeploy is idempotent rather than a flip-flop.
PROXY_AUDIENCE="api://${PROXY_CLIENT_ID}"
az ad app update --id "${PROXY_CLIENT_ID}" --identifier-uris "${PROXY_AUDIENCE}" --output none

log "Enforcing Easy Auth on ${PROXY_APP} (audience ${PROXY_AUDIENCE})..."
az webapp auth update -g "${RG}" -n "${PROXY_APP}" \
  --enabled true --action Return401 \
  --aad-allowed-token-audiences "${PROXY_AUDIENCE}" \
  --aad-client-id "${PROXY_CLIENT_ID}" \
  --aad-token-issuer-url "https://login.microsoftonline.com/${TENANT_ID}/v2.0" --output none

warn "The ACA orchestrator must present a token for audience ${PROXY_AUDIENCE} (SEARCH_PROXY_AUDIENCE)."
log "Keep Bicep in sync: redeploy with -p proxyAuthClientId=${PROXY_CLIENT_ID}. That redeploy is not optional:"
log "it is also what sets the orchestrator's SEARCH_PROXY_AUDIENCE, which is empty until you pass it."
