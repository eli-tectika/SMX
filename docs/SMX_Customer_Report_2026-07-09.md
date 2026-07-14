# SMX Marker Selection Platform — Delivery & Status Report

**Date:** July 9, 2026
**Project:** SMX AI-Powered Taggant (Marker) Selection System — an internal tool for SMX R&D that
automates the end-to-end marker-selection workflow: from XRF background analysis through candidate
discovery, regulatory screening, dosing and code combinations, and cost analysis, to the final
marker-library output.

---

## 1. Executive Summary

**The project is on track.** Milestone 1 — the Secure Cloud Foundation and Data Platform — is
effectively complete: the full environment is deployed to Azure (Sweden Central) entirely as code, the
development environment is live, and final integration verification and sign-off are in progress this
week.

Two subsystems are already complete and delivered:

- **The Regulatory Knowledge Base** — the body of cited source material from which the system derives
  the basis for every regulatory decision about a marker.
- **The Operator Web Application** — the interface through which the Project Leader runs the
  marker-selection journey end to end.

Beyond the Milestone 1 scope, the team has delivered a significant **head start on Milestone 2**: the AI
agent backend — including the constraint-intake and regulatory-screening agents, with every verdict
backed by a cited source — is already implemented and running in the development environment.

The platform's guiding principle throughout is **correctness**: because a wrong marker recommendation
causes real-world harm, every regulatory verdict the system produces must trace to a cited source, and
the system treats an uncited answer as a failure — not an acceptable approximation.

The next phase (Milestone 2, starting mid-July) completes AI model enablement, populates the retrieval
indexes, and validates screening accuracy against known cases.

---

## 2. What Has Been Delivered

### 2.1 Secure Cloud Foundation

- **Private-by-default Azure landing zone** in Sweden Central, built on a hub-and-spoke network
  topology. All platform services — data stores, AI services, key management — are reachable only over
  private endpoints inside the virtual network. There is a single controlled public entry point
  (Application Gateway) and a single anonymizing outbound path.
- **Everything as code.** The entire environment — networking, private DNS, identity, data, AI, compute
  — is defined in infrastructure-as-code templates and deploys repeatably into a fresh Azure
  subscription. Two deployment variants are maintained (subscription-scoped hub-and-spoke, and a
  single-resource-group variant), so the system can be stood up under different subscription permission
  models.
- **Identity and access without secrets.** Services authenticate to one another with Azure managed
  identities and role-based access control; the few credentials that must exist live in Azure Key Vault.
  A post-deployment hardening step removes all public network access and disables key-based
  authentication.
- **Full observability.** A central Log Analytics workspace with Application Insights provides logging,
  metrics, and distributed tracing across every component — including end-to-end traces through the AI
  agents.

### 2.2 The Regulatory Knowledge Base

A complete system for populating, maintaining, and updating SMX's regulatory corpus — the source
material behind every regulatory determination the system makes.

**A live, searchable regulatory corpus**

- **107 regulatory documents** ingested, processed, and fully indexed.
- **10,057 text passages** mapped and available for retrieval.
- **13 regulatory regions** covered: the European Union, the United States, Switzerland, the United
  Kingdom, China, Japan, Korea, Australia and New Zealand, metals and jewellery standards, global food
  contact, sustainability, and additional markets (Brazil, Canada, India, Mexico, Singapore, Taiwan,
  Türkiye, the Gulf states).

**Intelligent search.** The system supports both exact keyword search and semantic search — locating the
relevant regulation by meaning, even when the wording differs from the query.

**Full traceability.** Every passage in the corpus carries a complete, structured citation:

| Field | Purpose |
| --- | --- |
| Regulation name | What the rule is |
| Issuing authority | Who published it |
| Official date | When it took effect |
| Last sync date | When we last verified it |
| Direct source link | Where to read it |

This is the foundation of the correctness requirement: **no regulatory claim exists without a cited
source.**

**Automatic monthly synchronization.** The system connects directly to an approved list of official
sources and refreshes the corpus each month:

- California Proposition 65 (OEHHA)
- FDA food and food-contact regulations (21 CFR)
- EPA regulations (40 CFR)

It detects automatically whether a document has changed, and updates only what actually changed.

**A built-in safety mechanism.** Routine updates are ingested automatically. An anomalous change,
however — a sharp jump in the volume of edits, a parsing failure, or a structural change at the official
source — halts the process and requires **Regulatory Expert sign-off** before it enters the corpus.
Day-to-day efficiency is preserved without giving up the safety gate.

**Two supporting knowledge stores, delivered ahead of schedule**

- **SDS (Safety Data Sheet) library.** An automated pipeline gathers, validates, and indexes supplier
  safety data sheets into a searchable private library, giving the AI direct access to hazard
  classifications (H-codes, CMR status) with full source traceability.
- **Curated reference data.** SMX's marker compatibility knowledge base and supplier catalogs have been
  normalized and seeded into query-ready stores — four structured reference collections for exact,
  deterministic lookups, plus a dedicated search index for supporting prose (solubility, XRF cleanliness
  notes, bibliography-backed guidance).

Together, the regulatory corpus, the SDS library, and the reference data sit on a **medallion data
architecture**: raw source documents in Azure Data Lake Storage, structured query-ready data in a
private Azure Cosmos DB, and private AI-Search indexes the AI reasons over.

### 2.3 The AI Agent Layer — Milestone 2 Head Start

Although Milestone 2 formally begins mid-July, its core deliverables are substantially built and deployed
to the development environment:

- **Agent backend deployed.** A backend API and an agent-orchestration service run as containers on Azure
  Container Apps, inside the private network, behind the Application Gateway.
- **Two working AI agents.** A **constraint-intake agent** that normalizes project constraints and
  derives the applicable regulatory scope per component (with citations), and a **regulatory-screening
  agent** that evaluates each candidate substance against each product component across four dimensions —
  substrate compatibility, the product-wide element gate, the per-component application check, and the
  hazard layer.
- **Citation-required verdicts.** Every claim an agent produces must carry a citation to a retrieved
  source. A response that fails this validation is retried and, if it cannot be corrected, is flagged for
  human review rather than silently accepted.
- **Record-as-bus orchestration.** Agents do not share a conversation; each stage reads its inputs from
  and writes its outputs to a persisted structured record, and completing a stage automatically triggers
  the next. This is the same architecture the full eight-stage workflow will run on.
- **Excel-style compatibility matrix output.** Screening results fold deterministically into the
  compatibility matrix SMX works with today, exportable as a spreadsheet.
- **Evaluation harness.** An automated test rig replays curated cases through the deployed system and
  measures agreement with known manual verdicts — reporting the false-pass rate (a wrongly "clean"
  verdict, the real-world harm case) as a separate, zero-tolerance metric, and counting any uncited
  verdict as a failure.

### 2.4 The Operator Web Application

The full SMX web application — the interface through which the Project Leader runs the taggant-selection
process from beginning to end. It covers all **eight journey stages** and the **three cross-project
knowledge surfaces**, in accordance with the approved UX specification and mockups.

**The per-project journey.** Project intake and component definition (bottle, label, lid, liquid) · XRF
background analysis with a per-component V/L/X matrix · candidate discovery and screening with A/B/C
ranking · the regulatory gate with its screening table and evidence · ppm windows and code combinations ·
cost and supplier availability · the decision matrix and the VP gate.

**The cross-project knowledge layer.** The Marker Library · Learned Conclusions · the MSDS Registry,
including procurement blocking when no valid safety data sheet is on file.

**What already runs on real data.** Three screens are connected to the live system today: project
creation, real-time stage-progress tracking, and the compatibility matrix — including an evidence panel
that shows, for every verdict, its reasoning, confidence level, and cited sources, plus export to Excel.

The remaining screens are fully built and render demonstration data until their corresponding AI
components are connected. **Each such screen is clearly marked**, so that no ambiguity can ever arise
between data produced by the system and data shown for illustration. As each stage's AI components come
online, every demonstration screen is swapped for a real data connection and its marking removed — a
short, contained operation per screen, with no rewrite.

---

## 3. Correctness by Design

Correctness is not a QA step at the end; it is built into how the system behaves. The principles below
are enforced in the delivered code today.

- **Official sources decide the law.** No regulatory verdict rests on an open web search — compliance
  draws exclusively from a maintained list of official regulators, a substantive line of defence against
  treating non-authoritative or outdated material as regulation. Candidate *discovery* may consult the open
  web to surface markers beyond the internal catalogue, but only through the single anonymizing, controlled
  egress, and a web-sourced candidate is flagged for validation and can never clear a compliance gate on
  that basis alone.
- **Precision before coverage.** Where an official source did not supply the full updated text, we
  declined to populate the corpus with partial information. Abstaining is preferable to an imprecise
  answer.
- **Every verdict traces to a source.** The application's evidence panel neither summarizes nor
  abbreviates — it presents all four assessment dimensions (element gate, application check,
  compatibility, hazard) with the full citation and source date.
- **The strictest verdict wins.** If one dimension fails, the entire cell is marked as a failure. The
  interface verifies this independently against what the server returned and raises a clear alert on any
  mismatch. A green cell that conceals a red dimension is not possible.
- **Gates cannot be clicked by accident.** Regulatory approval and VP approval are records the operator
  signs explicitly; they cannot be approved by mistake.
- **Procurement blocking.** A material without a valid safety data sheet is flagged as blocking an order.

---

## 4. Milestone Status

| Milestone | Timeframe | Status |
|---|---|---|
| **M1 · Secure Cloud Foundation & Data Platform** | Jun 15 – Jul 14, 2026 | **Effectively complete** — infrastructure live in dev; final integration verification and sign-off in progress this week |
| **M2 · AI Foundry & RAG + Chemistry Engine** | Jul 15 – Aug 14, 2026 | **Ahead-of-schedule head start** — agent backend built and deployed; model enablement, index population, and accuracy validation to complete in-milestone |
| **M3 · Chemistry End-to-End & UAT + Physics ML POC** | Aug 17 – Sep 15, 2026 | Planned |
| **M4 · Physics Portal & Spectral Processing** | Sep 16 – Oct 15, 2026 | Planned |
| **M5 · Final QA, Security, Performance & Handoff** | Oct 16 – Nov 13, 2026 | Planned |

---

## 5. Roadmap — Next Phases

### M2 — AI Foundry & RAG + Chemistry Engine (Jul 15 – Aug 14)

With the agent backend already deployed, M2 focuses on completing and proving the AI layer:

- **Model enablement.** Finalize the Claude Opus deployment on Azure AI Foundry, accessed over private
  endpoints with managed-identity authentication. This step is pending Anthropic model capacity
  allocation on the subscription; the infrastructure and application code are ready, and the deployment
  is parameter-gated so it activates without code changes once capacity is granted.
- **RAG index population.** Complete population of the private regulatory index alongside the live SDS and
  reference indexes, so retrieval-augmented generation draws on the full three-index corpus.
- **Chemistry screening validation.** Run the evaluation harness against the curated golden set of known
  cases: near-perfect agreement expected on deterministic compatibility lookups, measured agreement on
  reasoning-track cases (regulatory limits, application checks, hazards), zero false-passes, and full
  citation coverage.
- **Milestone sign-off** on the end-to-end proof: constraints in → screening → cited compatibility
  matrix out.

### M3 — Chemistry End-to-End & UAT + Physics ML POC (Aug 17 – Sep 15)

The expert portal UI (conversational interface, structured forms, and the interactive matrix dashboard),
the explainability engine, and the ML feedback loop, culminating in Chemistry user-acceptance testing. In
parallel, the Physics ML proof of concept begins: an Azure ML environment, ingestion of historical
calibration and spectra data, 1D CNN models for peak identification and noise handling, and LightGBM-based
calibration prediction.

### M4 — Physics Portal & Spectral Processing (Sep 16 – Oct 15)

The physics expert portal: XRF scan upload, parsing and validation, model serving for spectral
processing, the calibration-parameter prediction API, spectral visualization dashboards, and Physics MVP
validation.

### M5 — Final QA, Security, Performance & Handoff (Oct 16 – Nov 13)

Full regression QA, security and compliance review, performance and disaster-recovery validation,
operational runbooks and documentation, knowledge transfer, production deployment, and final sign-off.

---

## 6. Scope Decisions & Risk Posture

The following decisions were agreed with the project sponsor and shape the current delivery:

- **Production deployment is scheduled for M5.** Current work targets a fully functional development/test
  environment; the infrastructure code already supports the production topology (WAF in prevention mode,
  dedicated compute, upgraded search tier), so production stand-up in M5 is a deployment exercise, not new
  engineering.
- **Enterprise hardening is deferred to the production-hardening phase by agreement.** Customer-managed
  keys / HSM, Microsoft Sentinel SIEM onboarding, and tenant-level MFA / Conditional Access policies are
  consciously scheduled for the production phase rather than the development environment.
- **Private-by-default remains the standing security posture.** All data and AI services are
  private-endpoint-only, with exactly one controlled public entry point (Application Gateway) and one
  anonymizing, allowlisted, schedule-batched egress path. This posture is already enforced in the
  development environment — it is not deferred.

---

## 7. Our Quality Approach

Three practices run through every deliverable on this project:

- **Test-driven development.** Application code is built test-first, with automated unit and integration
  suites covering the orchestration state machine, the citation-validation loop, matrix assembly,
  spreadsheet export, and the API contract. The Regulatory Knowledge Base alone carries **70 automated
  tests** and was run end to end in the cloud environment before hand-off — a run that surfaced and
  corrected defects that lab testing alone would not have exposed. The web application was likewise tested
  end to end against the real server code in an automated browser, and all type checks, unit tests, and
  builds pass.
- **Infrastructure as code.** The entire Azure footprint is version-controlled, reviewed, and repeatably
  deployable — the environment can be rebuilt from scratch, and infrastructure changes ship in the same
  change as the application changes they support. The web application is packaged as a container and is
  ready to deploy to the existing Azure environment with no infrastructure change required.
- **Correctness-first evaluation.** Because a wrong marker recommendation causes real-world harm, the
  system is held to a citation-required standard: no verdict is accepted without a traceable source,
  wrongly-clean verdicts are measured as a distinct zero-tolerance metric, and anything the AI cannot
  substantiate is routed to human review rather than passed through.

---

## 8. In Summary

Milestone 1 is effectively complete, its integration sign-off is underway this week, and Milestone 2 is
already substantially built ahead of schedule. From here on, SMX's regulatory agent works against a
corpus that is current, automatically maintained, and fully cited, and the operator works through a
faithful, correctness-guarded web application. Every answer the system gives can be traced back to the
exact regulatory source and the date on which it was verified.

We look forward to reviewing M1 sign-off with you this week and to demonstrating the cited screening
matrix end-to-end as M2 proceeds.
