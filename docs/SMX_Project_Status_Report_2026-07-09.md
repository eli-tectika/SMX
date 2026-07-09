# SMX Marker Selection Platform — Project Status Report

**Date:** July 9, 2026
**Project:** SMX AI-Powered Taggant (Marker) Selection System — an internal tool for SMX R&D that automates the end-to-end marker-selection workflow, from XRF background analysis through candidate discovery, regulatory screening, dosing and code combinations, and cost analysis, to the final marker-library output.

---

## 1. Executive Summary

The project is on track. Milestone 1 — the Secure Cloud Foundation and Data Platform — is effectively complete: the full environment is deployed to Azure (Sweden Central) entirely as code, the development environment is live, and final integration verification and sign-off are in progress this week. Beyond the M1 scope, the team has delivered a significant head start on Milestone 2: the AI agent backend — including the constraint-intake and regulatory-screening agents, with every verdict backed by a cited source — is already implemented and running in the development environment. The next phase (M2, starting mid-July) completes AI model enablement, populates the retrieval indexes, and validates screening accuracy against known cases. The platform's guiding principle throughout is correctness: every regulatory verdict the system produces must trace to a cited source, and the evaluation framework treats an uncited answer as a failure.

---

## 2. What Has Been Delivered

### Secure Cloud Foundation

- **Private-by-default Azure landing zone** in Sweden Central, built on a hub-and-spoke network topology. All platform services (data stores, AI services, key management) are reachable only over private endpoints inside the virtual network; there is a single controlled public entry point (Application Gateway) and a single anonymizing outbound path.
- **Everything as code.** The entire environment — networking, private DNS, identity, data, AI, compute — is defined in infrastructure-as-code templates and deploys repeatably into a fresh Azure subscription. Two deployment variants are maintained (subscription-scoped hub-and-spoke, and a single-resource-group variant), so the system can be stood up under different subscription permission models.
- **Identity and access without secrets.** Services authenticate to each other with Azure managed identities and role-based access control; the few credentials that must exist live in Azure Key Vault. A post-deployment hardening step removes all public network access and disables key-based authentication.
- **Full observability.** A central Log Analytics workspace with Application Insights provides logging, metrics, and distributed tracing across every component — including end-to-end traces through the AI agents.

### Data Platform & Regulatory Ingestion

- **Medallion data architecture**: Azure Data Lake Storage Gen2 holds raw source documents; Azure Cosmos DB (private, serverless) holds the structured, query-ready data. Azure AI Search hosts the private retrieval indexes the AI reasons over.
- **Controlled-egress regulatory ingestion.** Regulatory and safety content is gathered exclusively from a curated, version-controlled catalog of approved official sources — never open web browsing. The ingestion path is the platform's single outbound route, requests are batched on a fixed schedule so no external request can be correlated with any specific project, and content passes through a staged validation-and-publish flow before it becomes queryable.
- **SDS (Safety Data Sheet) library subsystem** — delivered ahead of schedule. An automated pipeline that gathers, validates, and indexes supplier safety data sheets into a searchable private library, giving the AI direct access to hazard classifications (H-codes, CMR status) with source traceability.
- **Curated reference-data subsystem** — delivered ahead of schedule. SMX's marker compatibility knowledge base and supplier catalogs have been normalized and seeded into query-ready stores: four structured reference collections for exact, deterministic lookups, plus a dedicated search index for supporting prose (solubility, XRF cleanliness notes, bibliography-backed guidance).
- **Monthly regulatory sync.** A scheduled function keeps the private regulatory corpus current from the approved source catalog, so every screening verdict can cite both its source and the corpus sync date.

### Application & AI Layer — Milestone 2 Head Start

Although Milestone 2 formally begins mid-July, its core deliverables are substantially built and deployed to the development environment:

- **Agent backend deployed.** A backend API and an agent-orchestration service run as containers on Azure Container Apps, inside the private network, behind the Application Gateway.
- **Two working AI agents**: a **constraint-intake agent** that normalizes project constraints and derives the applicable regulatory scope per component (with citations), and a **regulatory-screening agent** that evaluates each candidate substance against each product component across four dimensions — substrate compatibility, the product-wide element gate, the per-component application check, and the hazard layer.
- **Citation-required verdicts.** Every claim an agent produces must carry a citation to a retrieved source; a response that fails this validation is retried and, if it cannot be corrected, is flagged for human review rather than silently accepted.
- **Record-as-bus orchestration.** Agents do not share a conversation; each stage reads its inputs from and writes its outputs to a persisted structured record, and completing a stage automatically triggers the next. This is the same architecture the full eight-stage workflow will run on.
- **Excel-style compatibility matrix output.** The screening results fold deterministically into the compatibility matrix SMX works with today, exportable as a spreadsheet.
- **Evaluation harness.** An automated test rig replays curated cases through the deployed system and measures agreement with known manual verdicts — reporting the false-pass rate (a wrongly "clean" verdict, the real-world harm case) as a separate, zero-tolerance metric, and counting any uncited verdict as a failure.

---

## 3. Milestone Status

| Milestone | Timeframe | Status |
|---|---|---|
| **M1 · Secure Cloud Foundation & Data Platform** | Jun 15 – Jul 14, 2026 | **Effectively complete** — infrastructure live in dev; final integration verification and sign-off in progress this week |
| **M2 · AI Foundry & RAG + Chemistry Engine** | Jul 15 – Aug 14, 2026 | **Ahead-of-schedule head start** — agent backend built and deployed; model enablement, index population, and accuracy validation to complete in-milestone |
| **M3 · Chemistry End-to-End & UAT + Physics ML POC** | Aug 17 – Sep 15, 2026 | Planned |
| **M4 · Physics Portal & Spectral Processing** | Sep 16 – Oct 15, 2026 | Planned |
| **M5 · Final QA, Security, Performance & Handoff** | Oct 16 – Nov 13, 2026 | Planned |

---

## 4. Next Phases

### M2 — AI Foundry & RAG + Chemistry Engine (Jul 15 – Aug 14)

With the agent backend already deployed, M2 focuses on completing and proving the AI layer:

- **Model enablement.** Finalize the Claude Opus deployment on Azure AI Foundry, accessed over private endpoints with managed-identity authentication. This step is pending Anthropic model capacity allocation on the subscription; the infrastructure and application code are ready, and the deployment is parameter-gated so it activates without code changes once capacity is granted.
- **RAG index population.** Complete population of the private regulatory index alongside the live SDS and reference indexes, so retrieval-augmented generation draws on the full three-index corpus.
- **Chemistry screening validation.** Run the evaluation harness against the curated golden set of known cases: near-perfect agreement expected on deterministic compatibility lookups, measured agreement on reasoning-track cases (regulatory limits, application checks, hazards), zero false-passes, and full citation coverage.
- **Milestone sign-off** on the end-to-end proof: constraints in → screening → cited compatibility matrix out.

### M3 — Chemistry End-to-End & UAT + Physics ML POC (Aug 17 – Sep 15)

The expert portal UI (conversational interface, structured forms, and the interactive matrix dashboard), the explainability engine, and the ML feedback loop, culminating in Chemistry user-acceptance testing. In parallel, the Physics ML proof of concept begins: an Azure ML environment, ingestion of historical calibration and spectra data, 1D CNN models for peak identification and noise handling, and LightGBM-based calibration prediction.

### M4 — Physics Portal & Spectral Processing (Sep 16 – Oct 15)

The physics expert portal: XRF scan upload, parsing and validation, model serving for spectral processing, the calibration-parameter prediction API, spectral visualization dashboards, and Physics MVP validation.

### M5 — Final QA, Security, Performance & Handoff (Oct 16 – Nov 13)

Full regression QA, security and compliance review, performance and disaster-recovery validation, operational runbooks and documentation, knowledge transfer, production deployment, and final sign-off.

---

## 5. Scope Decisions & Risk Posture

The following decisions were agreed with the project sponsor and shape the current delivery:

- **Production deployment is scheduled for M5.** Current work targets a fully functional development/test environment; the infrastructure code already supports the production topology (WAF in prevention mode, dedicated compute, upgraded search tier), so production stand-up in M5 is a deployment exercise, not new engineering.
- **Enterprise hardening is deferred to the production-hardening phase by agreement.** Customer-managed keys / HSM, Microsoft Sentinel SIEM onboarding, and tenant-level MFA / Conditional Access policies are consciously scheduled for the production phase rather than the development environment.
- **Private-by-default remains the standing security posture.** All data and AI services are private-endpoint-only, with exactly one controlled public entry point (Application Gateway) and one anonymizing, allowlisted, schedule-batched egress path. This posture is already enforced in the development environment — it is not deferred.

---

## 6. Quality Approach

Three practices run through every deliverable on this project:

- **Test-driven development.** Application code is built test-first, with automated unit and integration suites covering the orchestration state machine, the citation-validation loop, matrix assembly, spreadsheet export, and the API contract.
- **Infrastructure as code.** The entire Azure footprint is version-controlled, reviewed, and repeatably deployable — the environment can be rebuilt from scratch, and infrastructure changes ship in the same change as the application changes they support.
- **Correctness-first evaluation.** Because a wrong marker recommendation causes real-world harm, the system is held to a citation-required standard: no verdict is accepted without a traceable source, wrongly-clean verdicts are measured as a distinct zero-tolerance metric, and anything the AI cannot substantiate is routed to human review rather than passed through.

We look forward to reviewing M1 sign-off with you this week and to demonstrating the cited screening matrix end-to-end as M2 proceeds.
