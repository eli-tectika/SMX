#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Put the search provider API key in Key Vault and print the secret URI to feed back into Bicep.
# The key is NEVER a plaintext app setting: the proxy reads it through a Key Vault reference
# (functions.bicep sets PROXY_SEARCH_API_KEY=@Microsoft.KeyVault(SecretUri=...)), resolved by the proxy's
# own UAMI — which is granted read on THIS ONE SECRET, not on the vault.
#
# Pass the key as $2, or out of band via SEARCH_PROVIDER_KEY to keep it out of your shell history.
ENV="$(require_env_arg "${1:-}")"
KEY="${2:-${SEARCH_PROVIDER_KEY:-}}"
[ -n "${KEY}" ] || die "usage: set-search-key.sh <env> <search-provider-api-key>   (or set SEARCH_PROVIDER_KEY)"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

# The vault name carries a Bicep uniqueString() suffix (security.bicep: kv-${namePrefix}-${env}-${uniqueSuffix}),
# seeded from the subscription id here and the resource-group id in the single-rg variant. It therefore cannot
# be reconstructed from the naming tokens — discover it, exactly as build-images.sh discovers the ACR.
KV="$(az keyvault list -g "${RG}" --query "[?starts_with(name, 'kv-${NAME_PREFIX}-${ENV}-')].name | [0]" -o tsv)"
[ -n "${KV}" ] || die "No Key Vault found in ${RG} with prefix 'kv-${NAME_PREFIX}-${ENV}-' (is env '${ENV}' deployed?)"

# Must match functions.bicep's searchKeySecretName default: the secret-scoped role assignment is built on it.
SECRET="search-provider-key"

log "Storing the search provider key in ${KV}/${SECRET}..."
ID="$(az keyvault secret set --vault-name "${KV}" --name "${SECRET}" --value "${KEY}" --query id -o tsv)" \
  || die "Could not write ${SECRET} to ${KV}. Run this BEFORE harden.sh — harden closes the vault's public access."
[ -n "${ID}" ] || die "Could not write ${SECRET} to ${KV} (empty secret id returned)."

# Drop the version segment. An unversioned SecretUri lets App Service pick a rotated key up on its own, so
# rotating the key is a re-run of THIS script — not another Bicep redeploy.
URI="${ID%/*}"

log "Secret URI: ${URI}"
warn "Wire it in on the next deploy:"
warn "  ./deploy.sh ${ENV} -p proxySearchKeySecretUri=${URI} -p deploySearchKeyRbac=true"
warn "deploySearchKeyRbac=true is what grants the proxy read on this one secret. It defaults to false because"
warn "on a fresh subscription the secret does not exist yet, and a grant scoped to a missing secret fails the deploy."
log "Re-run harden.sh afterwards: a deploy re-opens what harden closed."
