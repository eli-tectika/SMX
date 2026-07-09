# SMX — Delivery Summary

Two subsystems are complete and delivered: the **Regulatory Knowledge Base** that grounds every
regulatory determination the system makes, and the **Operator Web Application** through which the
Project Leader runs the marker-selection journey end to end.

---

## Part 1 — The Regulatory Knowledge Base

### What was built

A complete system for populating, maintaining, and updating SMX's regulatory corpus — the body of
source material from which the system derives the basis for every regulatory decision about a marker.

### What was delivered

**A live, searchable regulatory corpus**

- **107 regulatory documents** ingested, processed, and fully indexed.
- **10,057 text passages** mapped and available for retrieval.
- **13 regulatory regions** covered: the European Union, the United States, Switzerland, the United
  Kingdom, China, Japan, Korea, Australia and New Zealand, metals and jewellery standards, global food
  contact, sustainability, and additional markets (Brazil, Canada, India, Mexico, Singapore, Taiwan,
  Türkiye, the Gulf states).

**Intelligent search**

The system supports both exact keyword search and semantic search — locating the relevant regulation by
meaning, even when the wording differs from the query.

**Full traceability**

Every passage in the corpus carries a complete, structured citation:

| Field | Purpose |
| --- | --- |
| Regulation name | What the rule is |
| Issuing authority | Who published it |
| Official date | When it took effect |
| Last sync date | When we last verified it |
| Direct source link | Where to read it |

This is the foundation of the correctness requirement: **no regulatory claim exists without a cited
source.**

**Automatic monthly synchronization**

The system connects directly to an approved list of official sources and refreshes the corpus each
month:

- California Proposition 65 (OEHHA)
- FDA food and food-contact regulations (21 CFR)
- EPA regulations (40 CFR)

It detects automatically whether a document has changed, and updates only what actually changed.

**A built-in safety mechanism**

Routine updates are ingested automatically. An anomalous change, however — a sharp jump in the volume of
edits, a parsing failure, or a structural change at the official source — halts the process and requires
Regulatory Expert sign-off before it enters the corpus. Day-to-day efficiency is preserved without
giving up the safety gate.

### Principles that guided the work

**Official sources only.** The system performs no open web search. It pulls exclusively from a maintained
list of official regulators. This is a substantive line of defence against ingesting non-authoritative or
outdated material.

**Precision before coverage.** Where an official source did not supply the full updated text, we declined
to populate the corpus with partial information. In a system where a wrong marker recommendation causes
real-world harm, abstaining is preferable to an imprecise answer.

**Quality control.** The system is covered by **70 automated tests** and was run end to end in the cloud
environment before hand-off — a run that surfaced and corrected defects that lab testing alone would not
have exposed.

### What this means for you

From here on, SMX's regulatory agent works against a corpus that is current, automatically maintained,
and fully cited. Every answer the system gives can be traced back to the exact regulatory source and the
date on which it was verified.

---

## Part 2 — The Operator Web Application

### What was delivered

The full SMX web application — the interface through which the Project Leader runs the taggant-selection
process from beginning to end. It covers all eight journey stages and the three cross-project knowledge
surfaces, in accordance with the approved UX specification and mockups.

### The screens

**The per-project journey**

Project intake and component definition (bottle, label, lid, liquid) · XRF background analysis with a
per-component V/L/X matrix · candidate discovery and screening with A/B/C ranking · the regulatory gate
with its screening table and evidence · ppm windows and code combinations · cost and supplier
availability · the decision matrix and the VP gate.

**The cross-project knowledge layer**

The Marker Library · Learned Conclusions · the MSDS Registry, including procurement blocking when no
valid safety data sheet is on file.

### What already runs on real data

Three screens are connected to the live system today: project creation, real-time stage-progress tracking,
and the compatibility matrix — including an evidence panel that shows, for every verdict, its reasoning,
confidence level, and cited sources, plus export to Excel.

The remaining screens are fully built and render demonstration data until their corresponding AI
components are connected. Each such screen is clearly marked, so that no ambiguity can arise between data
produced by the system and data shown for illustration.

### Principles embedded in the product

**Every verdict traces to a source.** The evidence panel neither summarizes nor abbreviates — it presents
all four assessment dimensions (element gate, application check, compatibility, hazard) with the full
citation and source date.

**The strictest verdict wins.** If one dimension fails, the entire cell is marked as a failure. The
interface verifies this independently against what the server returned, and raises a clear alert on any
mismatch. A green cell that conceals a red dimension is not possible.

**Gates cannot be clicked by accident.** Regulatory approval and VP approval are records the operator
signs explicitly. They await the signing mechanism and cannot be approved by mistake.

**Procurement blocking.** A material without a valid safety data sheet is flagged as blocking an order.

### Quality and engineering

The interface was tested end to end against the real server code in an automated browser: project
creation, record retrieval, matrix rendering, and verification that the strictest-verdict rule actually
holds. All type checks, unit tests, and builds pass.

The visual design derives directly from the design tokens of the approved mockups, so the appearance is
faithful to the original. The application is packaged as a container and is ready to deploy to the
existing Azure environment — no infrastructure change required.

### The next step

As each stage's AI components come online, every demonstration screen is swapped for a real data
connection and its marking removed. The architecture was designed so that this swap is a short, contained
operation per screen, with no rewrite.
