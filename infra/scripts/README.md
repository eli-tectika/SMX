# SMX scripts

Every script ships as a **bash + PowerShell pair** with the same name, arguments and behaviour.
Pick whichever shell you are in — on Windows use the `.ps1`; on Linux/macOS (or WSL/Git Bash) use
the `.sh`. They are twins, not alternatives: fix a bug in one and fix it in the other.

```
lib.sh / lib.ps1                 shared helpers (naming, guards, logging) — sourced, not run
preflight.*                      dry run: lint, register RPs, what-if
deploy.*                         deploy the environment (subscription-scoped Bicep)
build-images.*                   build frontend + backend + orchestrator images in ACR
swap-images.*                    repoint one Container App at an image (stopgap — see below)
publish-functions.*              build + zip-deploy Smx.Functions to the regsync Function App
configure-auth.*                 Entra app registration + Easy Auth on the Function App
seed-reference-data.*            upload the workbooks to Bronze + invoke the seeder
harden.*                         lock data/AI services down to private endpoints
smoke.*                          post-deploy health check
teardown.*                       delete an environment
dev-local-setup.*                one-time local dev config, read from the deployed env
dev-local.*                      run the stack locally (up / down / status / logs / restart)
```

## Deploying to Azure

Order matters. `seed-reference-data` needs public reach and an Entra token, so it must run
**before** `harden`, which is what takes that reach away.

```bash
./preflight.sh dev                 # 0. dry run  (.\preflight.ps1 dev)
./deploy.sh dev                    # 1. infrastructure
./build-images.sh dev              # 2. images -> ACR, tagged with the short git SHA
./deploy.sh dev \
  -p frontendImage=<acr>.azurecr.io/smx-frontend:<tag> \
  -p backendImage=<acr>.azurecr.io/smx-backend:<tag> \
  -p orchestratorImage=<acr>.azurecr.io/smx-orchestrator:<tag>
./publish-functions.sh dev         # 3. function code
./configure-auth.sh dev            # 4. Entra + Easy Auth
./seed-reference-data.sh dev       # 5. reference data   <-- before harden
./harden.sh dev                    # 6. private endpoints only
./smoke.sh dev                     # 7. verify
```

In PowerShell the extra Bicep parameters go through `-Parameters`:

```powershell
.\deploy.ps1 dev -Parameters @("frontendImage=$acr.azurecr.io/smx-frontend:$tag")
```

**Container images are Bicep parameters, not live edits.** `swap-images.*` mutates only the
running Container App, so the next `deploy` reconciles it back to what Bicep declares. Use it
when you cannot run a full deploy, then follow up with a real one.

**Re-run `harden` after every deploy.** Bicep's default is public access, so a deploy re-opens
what harden closed.

## Running locally

```bash
./dev-local-setup.sh          # once (and after any redeploy): writes the local config
./dev-local.sh up             # azurite + backend + web
./dev-local.sh logs backend
./dev-local.sh down
```

| service | port | notes |
|---|---|---|
| web | 5173 | vite; proxies `/api/*` to the backend, so the API is same-origin (no CORS) |
| backend | 5169 | `dotnet watch`; reads Cosmos with your own `az login` |
| azurite | 10000-2 | storage emulator; only `Smx.Functions` needs it |

`dev-local-setup` reads the deployed environment's real endpoints and writes two **gitignored**
files: `.dev-local/backend.env` (exported into the backend) and `src/Smx.Functions/local.settings.json`.
No keys are written — local auth is your own `az login` via `DefaultAzureCredential`.

**The orchestrator and the Functions host are not started locally.** Both need AI Search and
Foundry, which `harden` puts behind private endpoints; a laptop is not in the VNet and cannot
reach them. `dev-local-setup` prints exactly which services are reachable, so check its output
before assuming a hang is a bug in your code.

**Cosmos is an IP allowlist.** It is the one backing service the local backend really uses, and
it only accepts the IPs recorded at deploy time. If your public IP has changed, every Cosmos call
hangs — `dev-local-setup` detects this and prints the `az cosmosdb update` command that fixes it.

## Configuration

All names derive from three tokens, overridable by environment variable:

| variable | default | |
|---|---|---|
| `NAME_PREFIX` | `smx` | resource-name prefix |
| `REGION_SHORT` | `swc` | region token in names |
| `LOCATION` | `swedencentral` | Azure region |
| `SMX_SUBSCRIPTION_ID` | the SMX estate | **guard**: scripts abort if `az` points elsewhere |
| `DEPLOYER_IP` | auto-detected | your public IPv4, allowlisted on the service firewalls |

The subscription guard exists because `az account set` is global and sticky: a subscription
switched in another terminal silently follows you here, and without the guard a deploy would
happily build the whole estate in the wrong place.

`DEPLOYER_IP` is not cosmetic. The Bicep reads it as `empty(ip) ? [] : [rule]`, so deploying with
an empty value **removes** the firewall allowlist from Cosmos, Storage, Search and Foundry and
locks you out of the data plane. The scripts refuse to deploy if they cannot detect it.

## Windows notes

These are real defects that were hit and fixed, not hypotheticals — leave the workarounds in.

- **ACR build logs crash the CLI.** `az` streams them through colorama, which encodes to the
  console codepage; vite's `✓` kills it on a non-UTF-8 console (e.g. cp1255) *after* the cloud
  build has already succeeded. `build-images` therefore passes `--no-logs` by default
  (`-Logs` / `ACR_BUILD_LOGS=1` to stream anyway).
- **`zip` is not installed.** Neither Git for Windows nor a bare Windows box ships it, so
  `publish-functions` falls back to the bsdtar in System32. **Not** `Compress-Archive`: on
  Windows PowerShell it writes entry names with backslashes, which is off-spec and leaves Kudu
  unable to recreate nested directories.
- **AppLocker blocks the apphost.** Launching the generated `Smx.Backend.exe` fails with "Access
  is denied", so `dev-local` runs the backend with `--property:UseAppHost=false`.
- **`.ps1` files must stay ASCII-only.** Windows PowerShell reads a BOM-less script in the local
  codepage, so a stray `—` inside a string is a parse error on a Hebrew-locale machine.
