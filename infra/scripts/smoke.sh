#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Post-deploy smoke check: ACA apps running, functions present, and the app reachable ONLY privately.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

log "ACA apps:"
az containerapp list -g "$RG" --query "[].{name:name, running:properties.runningStatus}" -o table

log "Function apps:"
az functionapp list -g "$RG" --query "[].{name:name, state:state}" -o table

# Echoes the HTTP status, or 000 when nothing answered (refused / timed out / no DNS).
# curl writes its --write-out string even when the transfer FAILS — http_code is 000 — so the
# obvious `curl … || echo 000` prints both and yields '000000', which reads as a live status code
# and would fire the die below on the expected-success path. Take curl's own value; only default
# when curl produced no output at all.
probe_http() {
  local code
  code="$(curl -s -o /dev/null -m "$2" -w '%{http_code}' "$1" 2>/dev/null || true)"
  printf '%s' "${code:-000}"
}

GW_IP="$(az network public-ip show -g "$RG" -n "pip-${NAME_PREFIX}-${ENV}-agw-${REGION_SHORT}" --query ipAddress -o tsv 2>/dev/null || true)"
if [ -n "${GW_IP}" ]; then
  # The public IP stays ALLOCATED (App Gateway v2 wants a public frontend for its control plane) but no
  # listener binds to it. A response here is a REGRESSION, not a success: it means a listener drifted back
  # onto the public frontend and the app is on the internet again. Short timeout — silence is the expected
  # outcome, so there is nothing worth waiting 20s for.
  log "Probing http://${GW_IP}/ — expecting NO response (the public listener must be closed)..."
  code="$(probe_http "http://${GW_IP}/" 8)"
  if [ "${code}" = "000" ]; then
    log "Public frontend closed (no response). OK."
  else
    die "PUBLIC FRONTEND IS ANSWERING (HTTP ${code}) at ${GW_IP} — the app is reachable from the internet."
  fi
else
  warn "App Gateway public IP not found."
fi

# Deliberately asymmetric with the probe above. Public reachability is a security regression and must break
# the build; private unreachability is almost always just "you are not on the VPN", so it only warns — and
# conflating the two would train the operator to ignore the one that matters.
AGW_PRIVATE_IP="${AGW_PRIVATE_IP:-10.0.0.10}"
log "Probing http://${AGW_PRIVATE_IP}/ — requires an established VPN tunnel..."
code="$(probe_http "http://${AGW_PRIVATE_IP}/" 20)"
if [ "${code}" = "200" ]; then
  log "Private frontend OK (HTTP ${code})."
else
  warn "Private frontend returned HTTP ${code} — connect the VPN client, or the backend is still warming."
fi

log "NAT egress IP (Functions controlled outbound):"
az network public-ip show -g "$RG" -n "pip-${NAME_PREFIX}-${ENV}-nat-${REGION_SHORT}" --query ipAddress -o tsv 2>/dev/null || warn "NAT public IP not found."
