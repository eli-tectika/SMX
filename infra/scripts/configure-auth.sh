#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Ensure the Entra app registrations this app needs (regsync, Search Proxy, and the frontend's SPA +
# API) and enforce Easy Auth (Return401) on the Function Apps. Entra app objects are Graph resources
# (not ARM), so this is a script step.
#
# Usage: configure-auth.sh <env> [host]
#   env    'dev' or 'prod'.
#   host   FQDN the SPA signs in against, e.g. dev.smxmarkers.io (or set APP_HOST instead). Only the
#          frontend SPA + API section at the bottom needs it — the regsync/searchproxy sections below
#          run fine without it.
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

# =====================================================================================================
# Frontend Entra app registrations (Task B1): the SPA (React web app) that signs the operator in, and
# the API (the audience the backend validates). Two SEPARATE app registrations — same reasoning as the
# Search Proxy block above: the SPA acquires a token whose AUDIENCE is the API, never itself, so the
# SPA's client id can never be replayed as a bearer token the backend accepts. AzureADMyOrg = single-
# tenant (this org only) — there is exactly one operator and no cross-tenant use case. The SPA is added
# to the API's preAuthorizedApplications so the operator is not prompted for consent every sign-in
# (Grant admin consent, below, still has to run once).
# =====================================================================================================
HOST="${2:-${APP_HOST:-}}"
if [ -z "${HOST}" ]; then
  die "Missing host for the SPA redirect URI. Usage: $0 ${ENV} <host>  (or APP_HOST=<host> $0 ${ENV})
 Example: $0 ${ENV} dev.smxmarkers.io
 Refusing to guess: a wrong/placeholder redirect URI silently breaks login instead of erroring loudly."
fi

# --- API app registration: exposes the delegated scope the backend validates as its JwtBearer audience. ---
API_APP_NAME="${NAME_PREFIX}-${ENV}-api"
log "Ensuring Entra app registration '${API_APP_NAME}'..."
API_ID="$(az ad app list --display-name "${API_APP_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${API_ID}" ]; then
  API_ID="$(az ad app create --display-name "${API_APP_NAME}" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  [ -n "${API_ID}" ] || die "Failed to create the app registration '${API_APP_NAME}'."
  log "Created app registration ${API_ID}"
fi
# api://<appId>, not api://<display-name> — same reasoning as PROXY_AUDIENCE above: the backend's
# JwtBearer audience (API_CLIENT_ID via apiClientId, Task B3) keys off the literal appId, so this must
# match exactly.
az ad app update --id "${API_ID}" --identifier-uris "api://${API_ID}" --output none

# Expose the delegated scope access_as_user, idempotently. Read back any EXISTING scope id first, via a
# null-safe JMESPath filter (works whether `api` is absent or its oauth2PermissionScopes is null, empty,
# or already populated) rather than a length()-based emptiness check — length() throws a hard error on a
# null collection, which set -euo pipefail would turn into a script abort on a freshly created app whose
# `api` object has not been populated yet. A freshly generated uuid must NEVER be used once the scope
# already exists: on a re-run that would pre-authorize the SPA (below) for a scope id the API was never
# actually assigned, and the backend would then reject every token as an invalid scope.
SCOPE_ID="$(az ad app show --id "${API_ID}" \
  --query "api.oauth2PermissionScopes[?value=='access_as_user'].id | [0]" -o tsv)"
if [ -z "${SCOPE_ID}" ]; then
  SCOPE_ID="$(cat /proc/sys/kernel/random/uuid)"
  log "Exposing scope access_as_user (${SCOPE_ID}) on ${API_APP_NAME}..."
  az ad app update --id "${API_ID}" --set api="{\"oauth2PermissionScopes\":[{\"id\":\"${SCOPE_ID}\",\"value\":\"access_as_user\",\"type\":\"User\",\"isEnabled\":true,\"adminConsentDisplayName\":\"Access SMX API\",\"adminConsentDescription\":\"Allow the SMX web app to call the SMX API as the signed-in operator\",\"userConsentDisplayName\":\"Access SMX API\",\"userConsentDescription\":\"Allow the SMX web app to call the SMX API on your behalf\"}]}" --output none
else
  log "Scope access_as_user already exposed on ${API_APP_NAME} (${SCOPE_ID})."
fi

# Pin token version to 2: without this Entra defaults to v1 access tokens (iss =
# https://sts.windows.net/<tenant>/), but the backend's JwtBearer Authority is the v2.0 endpoint (iss =
# https://login.microsoftonline.com/<tenant>/v2.0) — a v1 token would fail issuer validation and every
# authenticated call would 401 even after a successful sign-in. Idempotent; safe to re-run.
az ad app update --id "${API_ID}" --set api.requestedAccessTokenVersion=2 --output none

# --- SPA app registration: the React app; pre-authorized on the API so sign-in needs no consent prompt. ---
SPA_APP_NAME="${NAME_PREFIX}-${ENV}-web"
log "Ensuring Entra app registration '${SPA_APP_NAME}'..."
SPA_ID="$(az ad app list --display-name "${SPA_APP_NAME}" --query '[0].appId' -o tsv)"
if [ -z "${SPA_ID}" ]; then
  SPA_ID="$(az ad app create --display-name "${SPA_APP_NAME}" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
  [ -n "${SPA_ID}" ] || die "Failed to create the app registration '${SPA_APP_NAME}'."
  log "Created app registration ${SPA_ID}"
fi

# SPA-platform redirect URI (auth code + PKCE), root path on the gateway host. Bare origin (NO trailing
# slash): msal.ts sends redirectUri: window.location.origin, which the browser platform ALWAYS returns
# without a trailing slash, and Entra exact-matches redirect URIs — a registered "https://host/" would
# never match and login would fail with AADSTS50011. Always (re)set to the given HOST so a later domain
# change is corrected on the next run rather than silently left stale.
log "Setting SPA redirect URI to https://${HOST} ..."
az ad app update --id "${SPA_ID}" --set spa="{\"redirectUris\":[\"https://${HOST}\"]}" --output none

# Pre-authorize the SPA for access_as_user so the operator sees no separate per-app consent prompt.
# Uses the READ-BACK SCOPE_ID from above, never a freshly generated one — see the comment there.
log "Pre-authorizing ${SPA_APP_NAME} on ${API_APP_NAME}'s access_as_user scope..."
az ad app update --id "${API_ID}" \
  --set api.preAuthorizedApplications="[{\"appId\":\"${SPA_ID}\",\"delegatedPermissionIds\":[\"${SCOPE_ID}\"]}]" \
  --output none

echo "API_CLIENT_ID=${API_ID}"
echo "SPA_CLIENT_ID=${SPA_ID}"
warn "Set in dev.bicepparam: apiClientId='${API_ID}'"
warn "Rebuild the frontend image with VITE_ENTRA_CLIENT_ID=${SPA_ID} (SPA), VITE_API_SCOPE=api://${API_ID}/access_as_user, VITE_ENTRA_TENANT_ID=${TENANT_ID}"
warn "Grant admin consent for the SPA: az ad app permission admin-consent --id ${SPA_ID}  (needs a directory admin)"
