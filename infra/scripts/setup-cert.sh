#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Guided, RE-RUNNABLE checklist for wiring KeyVault-Acmebot (github.com/shibayan/keyvault-acmebot) to
# this environment's Key Vault + Azure DNS zone, so it can issue/auto-renew a free Let's Encrypt cert
# via DNS-01 and the App Gateway (Task A3) can auto-rotate from the vault. Acmebot itself is deployed
# separately (its own ARM template + web UI) — this script is NOT a substitute for that. It resolves
# this env's resource names, prints the ordered steps, and runs the verification/extraction commands
# once you have enough of the puzzle in hand. Safe to re-run at any point in the checklist.
#
# Usage: setup-cert.sh <env> [acmebot-app-name] [host] [cert-name]
#   acmebot-app-name  Function App name you deployed Acmebot as (you choose it in Acmebot's
#                      Deploy-to-Azure form; there is no fixed naming convention to derive it from).
#                      Needed from Step 2 onward.
#   host              FQDN to issue the cert for, e.g. dev.smxmarkers.io. Needed from Step 3 onward.
#   cert-name         Certificate name inside Key Vault (Acmebot's UI shows/lets you set this).
#                      Needed for Step 5's verification.
ENV="$(require_env_arg "${1:-}")"
ACMEBOT_APP="${2:-}"
CERT_HOST="${3:-}"
CERT_NAME="${4:-}"
confirm_subscription

RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

# The vault name carries a Bicep uniqueString() suffix (security.bicep: kv-${namePrefix}-${env}-${uniqueSuffix}),
# so discover it rather than reconstruct it — same approach as set-search-key.sh.
KV="$(az keyvault list -g "${RG}" --query "[?starts_with(name, 'kv-${NAME_PREFIX}-${ENV}-')].name | [0]" -o tsv)"
[ -n "${KV}" ] || die "No Key Vault found in ${RG} with prefix 'kv-${NAME_PREFIX}-${ENV}-' (is env '${ENV}' deployed?)"

log "=== KeyVault-Acmebot cert setup checklist: ${ENV} ==="
log "Resource group: ${RG}"
log "Key Vault:      ${KV}"
echo

log "Step 1 — Deploy KeyVault-Acmebot (github.com/shibayan/keyvault-acmebot) if you haven't already."
log "  Use its README's Deploy-to-Azure button / ARM template. Point it at:"
log "    - Key Vault:  ${KV}"
log "    - DNS zone:   the Azure DNS zone backing appDomainName (see infra/env/${ENV}.bicepparam)"
log "  Start on Let's Encrypt STAGING — its rate limits are generous, production's are not."
echo

log "Step 2 — Grant Acmebot's identity the role assignments this task added to Bicep."
if [ -z "${ACMEBOT_APP}" ]; then
  warn "  No acmebot-app-name given — re-run: $0 ${ENV} <acmebot-function-app-name>"
  log "  Then this step reads its principal id and prints the redeploy command for you."
else
  log "  Reading identity of ${ACMEBOT_APP}..."
  PRINCIPAL_ID="$(az functionapp identity show -g "${RG}" -n "${ACMEBOT_APP}" --query principalId -o tsv 2>/dev/null || true)"
  if [ -z "${PRINCIPAL_ID}" ]; then
    warn "  Could not read a principal id for ${ACMEBOT_APP} in ${RG} (not deployed yet, or no system-assigned identity)."
    log "  Manual check:  az functionapp identity show -g ${RG} -n ${ACMEBOT_APP} --query principalId -o tsv"
  else
    log "  Principal id: ${PRINCIPAL_ID}"
    log "  Redeploy so the DNS Zone Contributor + Key Vault Certificates Officer grants apply:"
    log "    ./deploy.sh ${ENV} -p acmebotPrincipalId=${PRINCIPAL_ID}"
  fi
fi
echo

ACMEBOT_APP_SHOWN="${ACMEBOT_APP:-<acmebot-app>}"
HOST_SHOWN="${CERT_HOST:-<host, e.g. dev.smxmarkers.io>}"
log "Step 3 — Issue the certificate via the Acmebot web UI (https://${ACMEBOT_APP_SHOWN}.azurewebsites.net/)."
log "  Target host: ${HOST_SHOWN}"
log "  Acmebot writes the DNS-01 TXT challenge into the zone (Step 2's grant) and stores the cert in ${KV}."
echo

log "Step 4 — Once the STAGING cert issues cleanly, switch Acmebot's Let's Encrypt endpoint app setting"
log "  to PRODUCTION and re-issue the cert for the same host. Staging certs are not browser-trusted."
echo

log "Step 5 — Verify the cert and extract the versionless secret id for certKeyVaultSecretId."
if [ -z "${CERT_NAME}" ]; then
  warn "  No cert-name given — re-run: $0 ${ENV} ${ACMEBOT_APP_SHOWN} ${HOST_SHOWN} <cert-name>"
  log "  Manual verification:"
  log "    az keyvault certificate show --vault-name ${KV} --name <cert-name>"
  log "  Manual versionless-id extraction:"
  log "    az keyvault secret show --vault-name ${KV} --name <cert-name> --query id -o tsv | sed 's:/[^/]*\$::'"
else
  log "  Verifying ${CERT_NAME} in ${KV}..."
  if az keyvault certificate show --vault-name "${KV}" --name "${CERT_NAME}" -o table 2>/dev/null; then
    VERSIONLESS_ID="$(az keyvault secret show --vault-name "${KV}" --name "${CERT_NAME}" --query id -o tsv | sed 's:/[^/]*$::')"
    log "  Versionless secret id: ${VERSIONLESS_ID}"
    log "  Paste it in and redeploy:"
    log "    ./deploy.sh ${ENV} -p certKeyVaultSecretId=${VERSIONLESS_ID}"
  else
    warn "  Certificate '${CERT_NAME}' not found in ${KV} yet — complete Steps 1-4 first, then re-run this script."
  fi
fi
