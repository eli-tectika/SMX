#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Post-deploy smoke check: gateway reachable, ACA apps running, functions present.
ENV="$(require_env_arg "${1:-}")"
confirm_subscription
RG="rg-${NAME_PREFIX}-${ENV}-${REGION_SHORT}"

log "ACA apps:"
az containerapp list -g "$RG" --query "[].{name:name, running:properties.runningStatus}" -o table

log "Function apps:"
az functionapp list -g "$RG" --query "[].{name:name, state:state}" -o table

GW_IP="$(az network public-ip show -g "$RG" -n "pip-${NAME_PREFIX}-${ENV}-agw-${REGION_SHORT}" --query ipAddress -o tsv 2>/dev/null || true)"
if [ -n "${GW_IP}" ]; then
  log "App Gateway public IP: ${GW_IP} — probing http://${GW_IP}/ ..."
  code="$(curl -s -o /dev/null -m 20 -w '%{http_code}' "http://${GW_IP}/" || echo 000)"
  if [ "${code}" = "200" ]; then log "Gateway OK (HTTP ${code})."; else warn "Gateway returned HTTP ${code} (backend may still be warming)."; fi
else
  warn "App Gateway public IP not found."
fi

log "NAT egress IP (Functions controlled outbound):"
az network public-ip show -g "$RG" -n "pip-${NAME_PREFIX}-${ENV}-nat-${REGION_SHORT}" --query ipAddress -o tsv 2>/dev/null || warn "NAT public IP not found."
