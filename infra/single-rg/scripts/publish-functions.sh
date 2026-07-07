#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + zip-deploy the Smx.Functions project (SDS subsystem) to the regsync Function App
# in the single-RG variant. Keyless: uses the caller's `az` login (Entra).
RG="${1:-${SMX_RG:-Tectica}}"
ENV_LABEL="${SMX_ENV:-prod}"
confirm_subscription
APP="func-${NAME_PREFIX}-${ENV_LABEL}-regsync-${REGION_SHORT}"
PROJ="${INFRA_DIR}/../../src/Smx.Functions"
OUT="$(mktemp -d)"; ZIP="${OUT}/sds-functions.zip"

require_cmd dotnet
require_cmd zip
log "Publishing ${PROJ} -> ${APP} (${RG})"
dotnet publish "${PROJ}" -c Release -o "${OUT}/publish"
( cd "${OUT}/publish" && zip -qr "${ZIP}" . )
az functionapp deployment source config-zip -g "${RG}" -n "${APP}" --src "${ZIP}" --output none
rm -rf "${OUT}"
log "Published SDS functions to ${APP}. (Run configure-auth.sh next, then harden.sh.)"
