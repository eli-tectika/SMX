#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Repoint an ACA app from the placeholder to a real image.
# Usage: ./swap-images.sh <env> <frontend|backend|orchestrator> <image-ref>
ENV="$(require_env_arg "${1:-}")"
APP="${2:?app name: frontend | backend | orchestrator}"
IMAGE="${3:?image reference, e.g. <acr>.azurecr.io/smx-frontend:1.0.0}"
confirm_subscription

RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP_NAME="ca-${NAME_PREFIX}-${ENV}-${APP}-${REGION_SHORT}"

log "Updating ${APP_NAME} → ${IMAGE}"
az containerapp update --resource-group "${RG}" --name "${APP_NAME}" --image "${IMAGE}" --output none
log "Done. New revision is rolling out."
