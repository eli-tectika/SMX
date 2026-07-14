#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + zip-deploy the Smx.SearchProxy project to the searchproxy Function App.
# It is a SEPARATE project from Smx.Functions on purpose: this app's identity has NO corpus RBAC, and
# deploying the SDS/Reg code here would drag Cosmos/Bronze/Search dependencies onto the internet-facing app.
# Keyless: uses the caller's `az` login (Entra). deploy.sh provisions the shell first.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-searchproxy-${REGION_SHORT}"
PROJ="${INFRA_DIR}/../src/Smx.SearchProxy"
OUT="$(mktemp -d)"; ZIP="${OUT}/search-proxy.zip"

require_cmd dotnet
log "Publishing ${PROJ} -> ${APP} (${RG})"
dotnet publish "${PROJ}" -c Release -o "${OUT}/publish"
make_zip "${OUT}/publish" "${ZIP}"
# az is a native binary on Windows: hand it a Windows path, not /tmp/....
az functionapp deployment source config-zip -g "${RG}" -n "${APP}" --src "$(to_native_path "${ZIP}")" --output none
rm -rf "${OUT}"
log "Published the Search Proxy to ${APP}."
log "Next: set-search-key.sh (the key) and configure-auth.sh (Easy Auth) — then redeploy to wire both in."
