# M1 Integration Test & Sign-off Record — 2026-07-09

Milestone: **M1 — Secure Cloud Foundation & Data Platform** (SOW tasks SMX-001…SMX-016).
Environment verified: **dev** (`rg-smx-hub-swc` + `rg-smx-dev-swc`, swedencentral, subscription `SecurityMatters`).
Agreed scope for this sign-off: **production spoke deliberately not deployed** (deferred to M5); enterprise
hardening items (CMK/HSM, Sentinel onboarding, tenant MFA/Conditional Access, cryptographic snapshot
signing) **descoped to the production phase by operator decision (2026-07-09)**.

## Task-by-task verdicts

| Task | SOW item | Verdict | Evidence / notes |
|---|---|---|---|
| SMX-001 | Kickoff & dependency intake | ✅ Done | Specs/datasets in `project_files/`, curated workbooks in `data/` |
| SMX-002 | Subscriptions & MG hierarchy | ✅ Done (simplified) | Single subscription + RG separation (hub/spoke) per design docs; 6-sub landing zone descoped |
| SMX-003 | IaC, repo, CI/CD | ✅ Done | Bicep (two variants, both compile), scripts; **CI added 2026-07-09** (`.github/workflows/ci.yml`: dotnet test ×2 solutions, bicep build ×2 variants, gated what-if on PR). Deploys remain script-based by decision |
| SMX-004 | Hub network + firewall + DNS | ✅ Done (simplified) | Hub VNet + 13 private DNS zones + peering; Azure Firewall/VPN descoped — NAT egress + anonymizing Search Proxy is the designed egress model |
| SMX-005 | Spoke VNets + PE subnets | ✅ Done (dev) | Dev spoke VNet, PE subnets, 14+ private endpoints, hub-spoke peering live. Prod spoke deferred |
| SMX-006 | Entra, RBAC, MFA, CA | ✅ Done (simplified) | UAMI + least-privilege role assignments via Bicep; MFA/CA descoped (single-operator, tenant-level) |
| SMX-007 | Key Vault Premium + HSM CMK | ✅ Done (simplified) | KV Standard + private endpoint + RBAC; Premium/HSM/CMK descoped |
| SMX-008 | KV networking for Cosmos CMK | ⚪ N/A | CMK descoped |
| SMX-009 | Defender + Policy guardrails | ✅ Done (partial-live) | MCSB initiative (`SecurityCenterBuiltIn`) already assigned at subscription scope; all Defender plans Free tier (free CSPM + secure score active). 5 audit-only policy assignments codified in `infra/modules/policy.bicep` (both variants), **gated off on dev**: applying them needs the *Resource Policy Contributor* role, which the deployer account lacks — flip `deployPolicyGuardrails=true` after the grant |
| SMX-010 | Monitoring foundation | ✅ Done | Log Analytics + App Insights (hub) live; OTel wired in backend/orchestrator (`APPLICATIONINSIGHTS_CONNECTION_STRING` present); Sentinel descoped ("provision-only if SOC mandate") |
| SMX-011 | Cosmos DB private | ✅ Done | Serverless account, private endpoint, public access disabled post-harden; CMK descoped |
| SMX-012 | Cosmos containers & partitioning | ✅ Done (diverged by design) | 13 containers with explicit PKs (`record`, `record-leases`, `sds-*` ×2, `reg-*` ×5, `ref-*` ×4) per the implemented subsystem designs, superseding the SOW's speculative schema |
| SMX-013 | ADLS Gen2 artifact store | ✅ Done | Storage account `isHnsEnabled: true`, `bronze` container, blob+dfs private endpoints, lifecycle via design |
| SMX-014 | Regulatory ingestion outbound + catalog | ✅ Done | Reg Sync timer function + curated `reg-registry` source catalog + controlled NAT egress; Search Proxy anonymizes agent-time searches |
| SMX-015 | Staging/validation/signing/publish | ✅ Done (simplified) | sha256 change detection, staged→live promotion, anomaly review gate (`reg-review`), private Cosmos publish; cryptographic signing descoped |
| SMX-016 | M1 integration test & sign-off | ✅ **This record** | See below |

## SMX-016 integration test evidence (2026-07-09)

**End-to-end (public entry → gateway → private ACA → app):**
- `GET http://20.91.142.32/` → **HTTP 200** (frontend via App Gateway → internal ACA envoy)
- `GET http://20.91.142.32/api/healthz` → **HTTP 200** `{"status":"ok"}` (path-based routing `/api/*` →
  backend API container, `PATH_BASE=/api`)
- App Gateway backend health: **both pools Healthy** (`acaBackendPool`, `acaApiBackendPool`)
- Observability proven end-to-end: backend traces visible in Application Insights
  (`cloud_RoleName = ca-smx-dev-backend-swc`) after the first real traffic
- All 3 Container Apps `Running` on intended images (`smx-backend:403d2e1`, `smx-orchestrator:403d2e1`,
  placeholder frontend pending the M3 portal); both Function Apps `Running`

**Root cause fixed en route (recorded as Learned Conclusion for the infra):** ACA app ingress was
`external: false` — meaning *"Limited to Container Apps Environment"* (app-to-app only) — so the
environment's VNet-facing envoy listener had no route for any app and returned 404 to the gateway
regardless of Host header, DNS, or TLS configuration. On an **internal** environment, `external: true`
means *"Limited to VNet"* — private, non-public ingress reachable by the gateway. Fixed in
`infra/modules/compute.bicep` + `infra/single-rg/modules/compute.bicep`; gateway now routes by app FQDN
through a private DNS zone (`infra/modules/gateway.bicep`), all reconciled by full Bicep redeploy
(deployment `Succeeded`, drift removed).

**Zero-public-exposure audit:**
- Public IPs in SMX RGs: exactly **2** — `pip-smx-dev-agw-swc` (20.91.142.32, the single intended
  public entry) and `pip-smx-dev-nat-swc` (4.225.167.42, outbound-only NAT; no inbound listener)
- PaaS public network access: **verified `Disabled` on every service** after `infra/scripts/harden.sh dev`
  (3× storage accounts, Cosmos, AI Search, Foundry, Key Vault, both Function Apps). E2E re-verified
  green post-harden (200/200). Sole designed exception: **ACR stays public on dev** (Standard SKU has
  no private-endpoint support; pulls are managed-identity-authenticated; prod upgrades to Premium + PE
  per the HLD dev-vs-prod scaling)
- Known accepted exposure: the dev gateway serves **HTTP (port 80) without WAF** and the API is
  **unauthenticated** behind it — accepted for the M1 proof window per the agent-backend plan
  (§security note); WAF_v2/prevention + auth are prod-milestone items

**Test suites (all green, 2026-07-09):**
- `dotnet test src/Smx.Functions.sln` → **70/70 passed** (SDS + Reg + Reference)
- `dotnet test src/Smx.Backend.sln` → **40/40 passed** (Domain, Backend API, Orchestrator, Eval)

## Deferred / follow-up register (not M1 blockers)

1. **Resource Policy Contributor** grant for the deployer → flip `deployPolicyGuardrails=true` in
   `infra/env/dev.bicepparam` and redeploy (SMX-009 live-audit half).
2. **Anthropic TPM quota** on the subscription → flip `deployClaude=true` (M2 model enablement).
3. **Frontend app** is the placeholder image until the M3 expert portal is built.
4. Prod spoke deployment + WAF prevention + API auth + hardening items — M5.

## Sign-off

- [ ] **Operator (Project Leader):** M1 accepted as complete for the dev environment under the agreed
      descopes listed above.

*Prepared by Claude Code, 2026-07-09, from live-environment verification in session; evidence commands
and outputs preserved in the session transcript.*
