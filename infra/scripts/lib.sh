#!/usr/bin/env bash
# Shared helpers for SMX infra scripts.
set -euo pipefail

log()  { printf '\033[0;34m[smx]\033[0m %s\n' "$*"; }
warn() { printf '\033[0;33m[smx][warn]\033[0m %s\n' "$*"; }
err()  { printf '\033[0;31m[smx][err]\033[0m %s\n' "$*" >&2; }
die()  { err "$*"; exit 1; }

# Absolute path to the infra/ directory (parent of scripts/).
INFRA_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Default naming tokens (overridable via environment).
NAME_PREFIX="${NAME_PREFIX:-smx}"
REGION_SHORT="${REGION_SHORT:-swc}"
LOCATION="${LOCATION:-swedencentral}"

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

require_env_arg() {
  case "${1:-}" in
    dev|prod) printf '%s' "$1" ;;
    *) die "Usage: expected environment 'dev' or 'prod', got '${1:-<none>}'" ;;
  esac
}

ensure_bicep() {
  require_cmd az
  az bicep version >/dev/null 2>&1 || { log "Installing Bicep..."; az bicep install; }
}

confirm_subscription() {
  require_cmd az
  local sub_id sub_name
  sub_id="$(az account show --query id -o tsv 2>/dev/null)" || die "Not logged in. Run: az login"
  sub_name="$(az account show --query name -o tsv)"
  log "Target subscription: ${sub_name} (${sub_id})"
}

detect_ip() {
  command -v curl >/dev/null 2>&1 || { printf ''; return 0; }
  curl -fsS https://api.ipify.org 2>/dev/null || printf ''
}
