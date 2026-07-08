#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Upload the reference workbooks to Bronze (lineage) and invoke SeedReferenceData.
# Run AFTER publish-functions.sh + configure-auth.sh, BEFORE harden.sh (needs public reach + Entra token).
# Requires the caller to hold "Storage Blob Data Contributor" on the env storage account.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"
APP="func-${NAME_PREFIX}-${ENV}-regsync-${REGION_SHORT}"
APP_REG_NAME="${NAME_PREFIX}-${ENV}-regsync-auth"
DATA_DIR="${INFRA_DIR}/../data"
DATASET_VERSION="${DATASET_VERSION:-2026-07}"

require_cmd az
require_cmd curl

STORAGE="$(az storage account list -g "${RG}" --query '[0].name' -o tsv)"
[ -n "${STORAGE}" ] || die "No storage account found in ${RG}"

log "Uploading reference workbooks to bronze (${STORAGE}) ..."
az storage blob upload --account-name "${STORAGE}" --auth-mode login -c bronze \
  -f "${DATA_DIR}/SMX Marker Compatibility Knowledge Base.xlsx" \
  -n "reference/compatibility/${DATASET_VERSION}.xlsx" --overwrite --output none
az storage blob upload --account-name "${STORAGE}" --auth-mode login -c bronze \
  -f "${DATA_DIR}/SMX Marker Suppliers - Comprehensive.xlsx" \
  -n "reference/suppliers/${DATASET_VERSION}.xlsx" --overwrite --output none

log "Invoking SeedReferenceData on ${APP} ..."
TOKEN="$(az account get-access-token --resource "api://${APP_REG_NAME}" --query accessToken -o tsv)"
curl -fsS -X POST "https://${APP}.azurewebsites.net/api/reference/seed" \
  -H "Authorization: Bearer ${TOKEN}" -H "Content-Length: 0"
echo
log "Reference data seeded. (Cosmos ref-* containers + smx-reference index populated.)"
