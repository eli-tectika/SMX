#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + zip-deploy the Smx.Functions project (SDS subsystem) to the regsync Function App.
# Keyless: uses the caller's `az` login (Entra). deploy.sh provisions the shell first.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-regsync-${REGION_SHORT}"
PROJ="${INFRA_DIR}/../src/Smx.Functions"
OUT="$(mktemp -d)"; ZIP="${OUT}/sds-functions.zip"

require_cmd dotnet
require_cmd zip
log "Publishing ${PROJ} -> ${APP} (${RG})"
dotnet publish "${PROJ}" -c Release -o "${OUT}/publish"
( cd "${OUT}/publish" && zip -qr "${ZIP}" . )
az functionapp deployment source config-zip -g "${RG}" -n "${APP}" --src "${ZIP}" --output none
rm -rf "${OUT}"
log "Published SDS functions to ${APP}. (Run configure-auth.sh next, then harden.sh.)"
