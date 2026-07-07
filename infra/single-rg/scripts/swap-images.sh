#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Repoint an ACA app from the placeholder to a real image (single-RG variant).
# Usage: ./swap-images.sh <RG> <frontend|backend|orchestrator> <image-ref>
RG="${1:-${SMX_RG:-Tectica}}"
APP="${2:?app name: frontend | backend | orchestrator}"
IMAGE="${3:?image reference, e.g. <acr>.azurecr.io/smx-frontend:1.0.0}"
ENV_LABEL="${SMX_ENV:-prod}"
confirm_subscription

APP_NAME="ca-${NAME_PREFIX}-${ENV_LABEL}-${APP}-${REGION_SHORT}"
log "Updating ${APP_NAME} in ${RG} → ${IMAGE}"
az containerapp update --resource-group "${RG}" --name "${APP_NAME}" --image "${IMAGE}" --output none
log "Done. New revision is rolling out."
