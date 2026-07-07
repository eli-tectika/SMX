#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Deletes only SMX-tagged resources inside the RG — never the RG itself (it's the customer's).
RG="${1:-${SMX_RG:-Tectica}}"
confirm_subscription

warn "This deletes SMX-tagged resources in resource group '${RG}'. The RG itself is left intact."
mapfile -t IDS < <(az resource list -g "$RG" --tag project=SMX --query "[].id" -o tsv)
if [ "${#IDS[@]}" -eq 0 ]; then
  log "No SMX-tagged resources found in ${RG}."
  exit 0
fi
printf '  %s\n' "${IDS[@]##*/}"
read -r -p "Delete these ${#IDS[@]} resources? Type 'delete' to confirm: " reply
[ "$reply" = "delete" ] || die "Confirmation failed; aborting."

# Private endpoints first (they depend on the services they front).
for id in $(az network private-endpoint list -g "$RG" --query "[?tags.project=='SMX'].id" -o tsv); do
  az network private-endpoint delete --ids "$id" && log "deleted PE ${id##*/}" || warn "failed PE ${id##*/}"
done

# Then the remaining tagged resources. Best-effort ordering; re-run if dependencies block.
for id in $(az resource list -g "$RG" --tag project=SMX --query "[].id" -o tsv); do
  az resource delete --ids "$id" >/dev/null 2>&1 && log "deleted ${id##*/}" || warn "skipped ${id##*/} (dependency; re-run)"
done

log "Teardown pass complete. Re-run if any SMX resources remain (private DNS zones may need their links removed first)."
