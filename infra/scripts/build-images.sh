#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Build + push the frontend, backend and orchestrator images in ACR (cloud build, no local docker).
# Usage: build-images.sh <env> [tag]
#   <env>  dev|prod (etc.) — selects the ACR named acr${NAME_PREFIX}${env}<suffix>.
#   [tag]  image tag; defaults to the current short git SHA.
#
# Entra SPA auth (optional): Vite inlines import.meta.env.VITE_* at build time, so the SPA
# client id + API scope must be baked into the frontend image's `npm run build`, not passed at
# container runtime. Export these before running this script — SPA_CLIENT_ID and API_CLIENT_ID
# are echoed by configure-auth.sh, ENTRA_TENANT_ID is your tenant id (az account show --query
# tenantId):
#   export SPA_CLIENT_ID=<spa app id>
#   export ENTRA_TENANT_ID=<tenant id>
#   export VITE_API_SCOPE="api://<api app id>/access_as_user"
# Left unset, all three default to empty and the frontend image builds in today's open mode.
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

# az streams build logs through colorama, which encodes to the *console* codepage. On a
# non-UTF-8 Windows console (e.g. cp1255) vite's "✓" crashes the CLI after the cloud build
# has already succeeded. Skip the stream there; ACR_BUILD_LOGS=1 forces it back on.
LOG_ARGS=()
if [[ -z "${ACR_BUILD_LOGS:-}" && ( "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* ) ]]; then
  warn "Windows shell detected: building with --no-logs (set ACR_BUILD_LOGS=1 to stream)."
  LOG_ARGS+=(--no-logs)
fi

# app -> tag : Dockerfile dir : build context [: extra az args…]. The SPA's context is its own
# dir (its Dockerfile COPYs package.json from the context root); the .NET images need the whole
# src/ tree.
build() {
  local app="$1" tag="$2" dockerfile="$3" context="$4"; shift 4
  local extra=("$@")
  log "az acr build ${app} -> smx-${app}:${tag}"
  # -o none drops the run JSON; it does not suppress streamed build logs.
  # ${arr[@]+…} keeps `set -u` happy when an array is empty. `extra` carries any extra
  # --build-arg pairs (frontend only; see FRONTEND_BUILD_ARGS / FRONTEND_ARGS below).
  az acr build --registry "${ACR_NAME}" \
    --image "smx-${app}:${tag}" \
    --file "${dockerfile}" \
    ${extra[@]+"${extra[@]}"} \
    ${LOG_ARGS[@]+"${LOG_ARGS[@]}"} \
    -o none \
    "${context}"
}

# Entra SPA client id + API scope, baked into the frontend image only (see the header comment
# for the env vars that feed this). Empty values are valid --build-arg args: the image builds
# in open mode, matching today's behavior.
FRONTEND_BUILD_ARGS=(
  --build-arg "VITE_ENTRA_CLIENT_ID=${SPA_CLIENT_ID:-}"
  --build-arg "VITE_ENTRA_TENANT_ID=${ENTRA_TENANT_ID:-}"
  --build-arg "VITE_API_SCOPE=${VITE_API_SCOPE:-}"
)

# FRONTEND_ENABLE_DEMO=true builds the STAKEHOLDER-DEMO frontend: it ships the fixture proj-demo
# (see src/smx-web/Dockerfile). It is tagged '-demo' so it can never be mistaken for a real image
# at the registry, and it must be served from its own origin — never a production one (MSW registers
# a service worker at the origin scope; see src/smx-web/src/mocks/demo.ts). The backend and
# orchestrator images are unaffected.
FRONTEND_TAG="${TAG}"
FRONTEND_ARGS=()
if [[ "${FRONTEND_ENABLE_DEMO:-}" == "true" ]]; then
  warn "FRONTEND_ENABLE_DEMO=true: building a DEMO frontend (ships fixture proj-demo) as smx-frontend:${TAG}-demo. Never serve it from a production origin."
  FRONTEND_TAG="${TAG}-demo"
  FRONTEND_ARGS=(--build-arg ENABLE_DEMO=true)
fi

log "Building images in ${ACR_NAME} (tag ${TAG})"
build frontend     "${FRONTEND_TAG}" "${SRC_DIR}/smx-web/Dockerfile"          "${SRC_DIR}/smx-web" "${FRONTEND_BUILD_ARGS[@]}" ${FRONTEND_ARGS[@]+"${FRONTEND_ARGS[@]}"}
build backend      "${TAG}"          "${SRC_DIR}/Smx.Backend/Dockerfile"      "${SRC_DIR}"
build orchestrator "${TAG}"          "${SRC_DIR}/Smx.Orchestrator/Dockerfile" "${SRC_DIR}"

log "images:"
log "  ${ACR_NAME}.azurecr.io/smx-frontend:${FRONTEND_TAG}"
for app in backend orchestrator; do log "  ${ACR_NAME}.azurecr.io/smx-${app}:${TAG}"; done
log "roll out with: az deployment ... -p frontendImage=... backendImage=... orchestratorImage=...  (or swap-images.sh)"
