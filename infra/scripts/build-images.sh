#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + push the backend and orchestrator images in ACR (cloud build, no local docker).
# Usage: build-images.sh <env> [tag]
#   <env>  dev|prod (etc.) — selects the ACR named acr${NAME_PREFIX}${env}<suffix>.
#   [tag]  image tag; defaults to the current short git SHA.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
require_cmd az
require_cmd git

SRC_DIR="${INFRA_DIR}/../src"
TAG="${2:-$(git -C "${INFRA_DIR}/.." rev-parse --short HEAD)}"

# ACR name convention (infra/modules/acr.bicep): toLower('acr${namePrefix}${env}${uniqueSuffix}').
ACR_PREFIX="acr${NAME_PREFIX}${ENV}"
ACR_NAME="$(az acr list --query "[?starts_with(name, '${ACR_PREFIX}')].name | [0]" -o tsv)"
[[ -n "${ACR_NAME}" ]] || die "no ACR found with prefix '${ACR_PREFIX}' (is env '${ENV}' deployed in this subscription?)"

log "Building images in ${ACR_NAME} (tag ${TAG})"
for app in backend orchestrator; do
  proj="Smx.$(tr '[:lower:]' '[:upper:]' <<<"${app:0:1}")${app:1}"
  log "az acr build ${app} -> smx-${app}:${TAG}"
  az acr build --registry "${ACR_NAME}" \
    --image "smx-${app}:${TAG}" \
    --file "${SRC_DIR}/${proj}/Dockerfile" \
    "${SRC_DIR}"
done

log "images:"
log "  ${ACR_NAME}.azurecr.io/smx-backend:${TAG}"
log "  ${ACR_NAME}.azurecr.io/smx-orchestrator:${TAG}"
log "roll out with: az deployment ... -p backendImage=... orchestratorImage=...  (or swap-images.sh)"
