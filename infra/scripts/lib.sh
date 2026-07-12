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

# Subscription the SMX estate lives in. `az account set` is global and sticky, so a
# subscription switched in another shell silently follows you here; without this guard
# confirm_subscription would happily print the wrong one and deploy into it.
# Override for a different estate: SMX_SUBSCRIPTION_ID=<guid> ./deploy.sh dev
SMX_SUBSCRIPTION_ID="${SMX_SUBSCRIPTION_ID:-98c6dba9-5088-4d2b-aadc-31b629a308de}"

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Required command not found: $1"
}

is_windows() { [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* ]]; }

# Git Bash hands native .exe arguments POSIX paths (/tmp/x, /c/SMX) that az cannot open.
to_native_path() {
  if is_windows && command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# `zip` ships with neither Git for Windows nor a bare Windows box, so fall back to the
# bsdtar in System32 (-a picks the format from the .zip suffix). NOT Compress-Archive:
# on Windows PowerShell it writes entry names with backslashes, which is off-spec and
# leaves Kudu unable to recreate nested directories. `-C dir .` (not a `*` glob) is what
# keeps the hidden `.azurefunctions/` directory in the isolated-worker publish output.
make_zip() {
  local src_dir="$1" out_zip="$2"
  if command -v zip >/dev/null 2>&1; then
    ( cd "${src_dir}" && zip -qr "${out_zip}" . )
  elif [[ -x /c/Windows/System32/tar.exe ]]; then
    /c/Windows/System32/tar.exe -a -c -f "$(to_native_path "${out_zip}")" \
      -C "$(to_native_path "${src_dir}")" . || die "bsdtar failed to package ${src_dir}"
  else
    die "Need 'zip' (apt/brew install zip) or Windows' bsdtar to package the app."
  fi
  [[ -s "${out_zip}" ]] || die "Packaging produced an empty archive: ${out_zip}"
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
  if [[ "${sub_id}" != "${SMX_SUBSCRIPTION_ID}" ]]; then
    die "Wrong subscription: active is '${sub_name}' (${sub_id}), expected ${SMX_SUBSCRIPTION_ID}.
     Fix with: az account set --subscription ${SMX_SUBSCRIPTION_ID}
     (or set SMX_SUBSCRIPTION_ID to deploy a different estate on purpose)"
  fi
  log "Target subscription: ${sub_name} (${sub_id})"
}

# The deployer's public IPv4, allowlisted on the service firewalls during deployment.
# -4: Azure ipRules reject an IPv6 literal. --ssl-no-revoke on Windows: Git's curl uses
# schannel, which fails closed (CRYPT_E_NO_REVOCATION_CHECK) when it cannot reach a CRL.
detect_ip() {
  command -v curl >/dev/null 2>&1 || { printf ''; return 0; }
  local args=(-4 -fsS --max-time 15)
  is_windows && args+=(--ssl-no-revoke)
  local ip
  ip="$(curl "${args[@]}" https://api.ipify.org 2>/dev/null || true)"
  [[ "${ip}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] && printf '%s' "${ip}" || printf ''
}

# An empty deployerIpAddress is not a no-op: the Bicep reads it as `empty() ? [] : [rule]`,
# so it strips the firewall allowlist from Cosmos/Storage/Search/Foundry and locks the
# operator out of the data plane. Refuse to deploy on a silent detection failure.
require_deployer_ip() {
  local ip
  ip="$(detect_ip)"
  if [[ -z "${ip}" ]]; then
    die "Could not detect this machine's public IPv4 (needed for the service firewall allowlist).
     Deploying with an empty IP would REMOVE the existing allowlist. Pass it explicitly:
       DEPLOYER_IP=<your.ip.v4> $0 $*"
  fi
  printf '%s' "${ip}"
}
