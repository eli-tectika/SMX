#!/usr/bin/env bash
# Run the SMX stack locally. Deliberately no `set -e`: this supervises child processes and
# must keep going when one of them is already dead.
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Usage: dev-local.sh [up|down|status|logs [svc]|restart]
#
# Services (ports match vite.config.ts's proxy and launchSettings.json):
#   azurite   10000-10002   storage emulator, only Smx.Functions needs it
#   backend   5169          dotnet watch
#   web       5173          vite; proxies /api -> :5169
#
# The orchestrator and Smx.Functions are NOT started: both need AI Search + Foundry, which
# harden.sh puts behind private endpoints — unreachable from a laptop. Run them in Azure.
CMD="${1:-up}"
REPO_ROOT="$(cd "${INFRA_DIR}/.." && pwd)"
RUN_DIR="${REPO_ROOT}/.dev-local"
LOG_DIR="${RUN_DIR}/logs"
PID_DIR="${RUN_DIR}/pids"
ENV_FILE="${RUN_DIR}/backend.env"
SERVICES=(azurite backend web)

mkdir -p "${LOG_DIR}" "${PID_DIR}"

port_of() {
  case "$1" in
    azurite) printf '10000' ;;
    backend) printf '5169' ;;
    web)     printf '5173' ;;
  esac
}

is_running() { # <svc>
  local pidfile="${PID_DIR}/$1.pid"
  [[ -f "${pidfile}" ]] || return 1
  local pid; pid="$(cat "${pidfile}")"
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

start_one() { # <svc>
  local svc="$1"
  if is_running "${svc}"; then log "${svc} already running (pid $(cat "${PID_DIR}/${svc}.pid"))"; return 0; fi
  local logfile="${LOG_DIR}/${svc}.log"
  case "${svc}" in
    azurite)
      command -v azurite >/dev/null 2>&1 || { warn "azurite not installed (npm i -g azurite); skipping."; return 0; }
      azurite --silent --location "${RUN_DIR}/azurite" >"${logfile}" 2>&1 &
      ;;
    backend)
      # backend.env carries the Cosmos/Search/Foundry endpoints written by dev-local-setup.*.
      # It is plain KEY=value (shared with the .ps1 twin), so export it rather than source it.
      # ASP.NET reads them straight out of the environment, exactly as it does in Container Apps.
      (
        cd "${REPO_ROOT}/src/Smx.Backend" || exit 1
        if [[ -f "${ENV_FILE}" ]]; then
          set -a; . <(grep -vE '^\s*(#|$)' "${ENV_FILE}"); set +a
        fi
        # UseAppHost=false: on Windows, launching the generated Smx.Backend.exe dies with
        # "Access is denied" under AppLocker; without an apphost `dotnet run` executes the dll.
        # On Linux/macOS the flag is a no-op beyond skipping the native launcher.
        dotnet watch run --non-interactive --property:UseAppHost=false >"${logfile}" 2>&1
      ) &
      ;;
    web)
      [[ -d "${REPO_ROOT}/src/smx-web/node_modules" ]] || {
        log "Installing web dependencies..."; ( cd "${REPO_ROOT}/src/smx-web" && npm install ); }
      ( cd "${REPO_ROOT}/src/smx-web" && npm run dev >"${logfile}" 2>&1 ) &
      ;;
  esac
  echo $! > "${PID_DIR}/${svc}.pid"
  log "started ${svc} (pid $!, port $(port_of "${svc}"), log ${logfile#"${REPO_ROOT}/"})"
}

stop_one() { # <svc>
  local svc="$1" pidfile="${PID_DIR}/$1.pid"
  if is_running "${svc}"; then
    local pid; pid="$(cat "${pidfile}")"
    # Negative pid = the whole process group: dotnet watch and vite both fork children that
    # keep the port bound if only the parent is signalled.
    kill -- "-${pid}" 2>/dev/null || kill "${pid}" 2>/dev/null
    log "stopped ${svc} (pid ${pid})"
  fi
  rm -f "${pidfile}"
}

case "${CMD}" in
  up)
    [[ -f "${ENV_FILE}" ]] \
      || warn "No .dev-local/backend.env — run ./dev-local-setup.sh first, or the backend starts without Cosmos."
    for s in "${SERVICES[@]}"; do start_one "$s"; done
    log ""
    log "  web      http://localhost:5173"
    log "  backend  http://localhost:5169/healthz"
    log "Tail logs with: ./dev-local.sh logs [azurite|backend|web]"
    ;;
  down)
    for s in "${SERVICES[@]}"; do stop_one "$s"; done
    ;;
  restart)
    for s in "${SERVICES[@]}"; do stop_one "$s"; done
    for s in "${SERVICES[@]}"; do start_one "$s"; done
    ;;
  status)
    for s in "${SERVICES[@]}"; do
      if is_running "$s"; then printf '  %-8s up    (pid %s, port %s)\n' "$s" "$(cat "${PID_DIR}/$s.pid")" "$(port_of "$s")"
      else printf '  %-8s down\n' "$s"; fi
    done
    ;;
  logs)
    svc="${2:-}"
    if [[ -n "${svc}" ]]; then tail -n 50 -F "${LOG_DIR}/${svc}.log"
    else tail -n 20 -F "${LOG_DIR}"/*.log; fi
    ;;
  *)
    die "Usage: $0 [up|down|status|restart|logs [azurite|backend|web]]"
    ;;
esac
