# Conversational Intake — Design

**Status:** approved design, not yet implemented
**Supersedes:** the form-based project creation in `src/smx-web/src/routes/NewProject.tsx` and the
`components` / `elementPools` preconditions in `src/Smx.Backend/Api/CreateProjectRequest.cs`
**Related:** UX spec §4.1 (intake & scoping), `2026-07-12-chemistry-backend-end-to-end-design.md` §5
(the conversational surface), `2026-07-08-agent-backend-design.md`

---

## 1. Why this changes

Creating a project today means filling a form: a components table, an element-pool table with per-row
emission lines and V/L statuses, a client restricted list. It is the only screen in the product that
writes to the record, and it demands the operator arrive already knowing the answers in the system's
vocabulary.

Three things are wrong with that.

**It is the least user-friendly moment in the product, and it is the first one.** Every other screen
reads; this one interrogates, in tables.

**It asks the human for work that is the agent's.** Candidate elements are not the operator's to
supply — discovery of candidate chemistry is exactly what the Discovery agent exists for, over the
catalogue, the knowledge layer, and (separately, not in this change) model knowledge and the web. A
form field for them invites the operator to pre-empt the analysis.

**Intake has no fixed fields.** What actually matters at the start of a project is a picture: what the
client makes, how they make it, what the marker has to survive, where it can be introduced, how it
will be detected. That is a conversation and a pile of documents, not a schema.

So project creation becomes an **interview**: a chat that opens on "New project", driven by a
dedicated agent whose job is to draw out as complete a picture as the operator can give, accept files
of any kind, and — when the picture is clear, or when the operator says so — **create the project with
a structured intake package already written into it**. The operator then opens the project, reads what
is there, and presses **Start Processing** to run the pipeline.

### What is out of scope

- Candidate discovery from model knowledge or the open web. Noted as coming, implemented elsewhere.
- OCR, image understanding, and scanned-PDF extraction. Deferred to its own change; the extractor
  interface below is the seam they plug into.
- Any change to Discovery, Regulatory, Dosing, Cost or Decision.

---

## 2. The shape of the change

```
POST /api/intake-sessions                 → IntakeSessionDoc      (container: intake-sessions, PK /sessionId)
POST /api/intake-sessions/{id}/messages   → SSE, InterviewAgent streams; turn persisted to the session
POST /api/intake-sessions/{id}/attachments→ blob + server-side text extraction
        …
   agent calls create_project(…)          → ProjectDoc  (intake stage = "awaiting-confirmation")
                                          + IntakeBriefDoc (container: record, PK /projectId)
        …
POST /api/projects/{id}/start             → intake stage = "pending" → change feed → the pipeline runs
```

### 2.1 The interview is a pre-project session, not a half-created project

Two shapes were considered: create the project immediately and make the interview the intake stage's
own chat (reusing everything), or hold the interview in a pre-project session that composes a project
on completion. **The session wins**, with one condition that removes its only real drawback.

The drawback of a pre-project session is that the interview transcript — where the most consequential
scoping judgments actually get made — would be orphaned from the project it produced, in a system
whose whole audit story is that the record is the trail. The condition: **the transcript is copied
into the project as part of the intake package.** The session is a scratchpad; the project gets the
finished dossier *and* the conversation that produced it.

In exchange, the projects list contains only real projects, and today's `IntakeAgent` — the
constraint-intake agent that derives regulatory scope with citations — is untouched.

### 2.2 Sessions live outside the `record` container

`IntakeSessionDoc` goes in a new Cosmos container `intake-sessions`, partitioned by `/sessionId`.

This is structural, not organisational. The `record` container's change feed is the dispatch bus:
`RecordDocRouter` reads a document's `type` discriminator and `StageDispatcher` runs a stage. A session
document sitting in `record` would be a document the router must be taught to ignore — a rule that
holds until someone forgets it, at which point an unfinished interview dispatches a stage. A separate
container makes the mistake unavailable. It also means a session has no `projectId`, which is honest:
it doesn't have one.

Sessions carry a Cosmos TTL (30 days) so abandoned drafts expire on their own.

### 2.3 Project creation stops being the pipeline trigger

Today, writing the `ProjectDoc` *is* the dispatch: `StageDispatcher.OnProjectAsync` runs intake when
`stages[intake].Status == "pending"`, which is the status `ProjectDoc.Create` assigns. If the agent
could create a project under those rules, an agent decision would silently launch the entire pipeline
on a dossier no human had read.

So the intake stage gains one status — **`awaiting-confirmation`** — and `OnProjectAsync` continues to
fire only on `pending`. `POST /projects/{id}/start` is the only writer that flips
`awaiting-confirmation` → `pending`.

This is the safety line of the whole design, and it is worth stating plainly:

> **The agent may create. Only the operator may start.**

Creating a project runs no analysis, produces no verdict, and commits nothing — it is safe to
delegate. Starting the pipeline is the operator asserting the dossier is right, and there is **no
agent tool for it**, in the same way there is no gate tool (Law 9).

### 2.4 `CreateProjectRequest` relaxes rather than disappears

`CreateProjectRequest.Validate()` currently requires at least one component and either element pools or
explicit candidates. Those preconditions move:

- `client` and `product` stay required.
- `components` becomes optional at creation. The interview supplies them, and `create_project`'s own
  gate (§4.3) refuses without them.
- `elementPools` becomes optional at creation, permanently. The physicist's XRF run lands days later
  (Law 6) and now has its own entry point (§6).
- **The pool-or-candidates requirement is dropped, not relocated.** It must not reappear at
  `POST /start`: under this design a project reaches `start` with a confirmed component set and no
  measured background at all, which is the normal case, not an error. Missing physics is already
  handled the way Law 6 requires — the stage that needs it **parks** in an `awaiting-*` state, exactly
  as Dosing already parks on an absent measured background. `POST /start` therefore validates only
  what intake itself owns: at least one component, each with material, application, objective and at
  least one market.

**Existing callers must keep starting immediately.** `tools/Smx.Eval` and the backend tests create
fully-specified projects through `POST /projects` and expect the pipeline to run; if creation
universally landed in `awaiting-confirmation`, the eval harness would silently stop evaluating
anything. So the **initial intake status is a parameter of creation**, defaulting to `pending` —
today's behaviour, unchanged for every existing caller. `create_project` is the one caller that passes
`awaiting-confirmation`, because it is the one caller that is a language model.

---

## 3. Records

### `IntakeSessionDoc` — container `intake-sessions`, PK `/sessionId`

The scratchpad. Disposable.

| Field | Notes |
|---|---|
| `sessionId`, `createdAt`, `updatedAt` | |
| `status` | `interviewing` \| `created` \| `abandoned` |
| `client`, `product` | captured early, needed by `create_project` |
| `turns[]` | `{ role, text, toolCalls[], createdAt }` — fixed-width ISO 8601 `"O"` timestamps, as `ChatMessageDoc` already requires, because the transcript is sorted lexicographically |
| `attachments[]` | `{ fileId, filename, contentType, sizeBytes, blobPath, textBlobPath?, status, error? }` |
| `dossier[]` | working answers — see §4.1 |
| `proposedComponents[]` | `ComponentSpec` shape |
| `summary` | the prose brief |
| `createdProjectId?` | set when `create_project` succeeds; makes the tool idempotent |
| `ttl` | 30 days |

### `IntakeBriefDoc` — container `record`, PK `/projectId`, `type: "intake-brief"`

The deliverable, and the thing the intake screen renders and downstream agents read.

`summary` · `dossier[]` · `components[]` · `attachments[]` (referencing the session's blob paths —
see §5.3) · `transcript[]` · `sessionId` · `createdAt`.

It is written **once**, by `create_project`, in the same logical operation as the `ProjectDoc`. It is
not a stage output and does not trigger dispatch — `RecordDocRouter` ignores `intake-brief` explicitly,
and a test pins that.

### `ProjectDoc` — modified

`stages[intake].Status` accepts `awaiting-confirmation`. Nothing else changes.

---

## 4. The interview agent

`src/Smx.Orchestrator/Agents/InterviewAgent.cs` + `InterviewTools.cs`. Like `ChatAgent`, it is **not**
run through `ValidatedAgentRunner`: a turn is prose plus tool calls, not a JSON document, and MAF's
function invocation already runs the tools and returns the final text.

Like `ChatTools`, its tools are **constructed per turn, closed over `sessionId`**. The model has no
session parameter and cannot acquire one; a test asserts the emitted `AIFunction` schemas contain no
`sessionId`.

### 4.1 The dossier and its question catalogue

`IntakeQuestions` is a static, versioned catalogue in `Smx.Domain`. The `record_finding` tool's
**description is derived from it**, not hand-listed beside it — the same discipline as
`IntakeAnswers.ComponentFields`, and for the same reason: a field the schema accepts but the
description omits is a field the model never offers to record, so the operator's answer is silently
lost. That drift has already happened once in this codebase, to a dosing multiplier.

Each question carries `id`, `prompt` (plain language, what the agent asks), and `why` — one sentence
naming the downstream stage that consumes it. The `why` is in the agent's context, not just in the
source: it is how the agent judges whether an answer is *sufficient* rather than merely present.

**The catalogue (v1):**

*Process and product*
`raw-materials` · `product-objectives` · `process-steps` · `chemical-reactions` ·
`intermediates-byproducts` · `quality-parameters` · `qc-tests` · `equipment`

*Marking*
`marker-addition-point` (optimal stage to introduce the marker, given the client's objectives) ·
`durability-challenges` · `detection-challenges`

*Structurally required by the pipeline*
`component-breakdown` (everything downstream runs per component) · `component-material` ·
`component-application` (application × markets selects the regulation lists) · `component-markets` ·
`component-objective` (brand go/no-go vs quantification — this flips the meaning of a conditional XRF
verdict at Background) · `client-restrictions` · `sample-status` (sets background mode: measured vs
provisional)

**Each dossier entry:**

| Field | Values |
|---|---|
| `questionId` | from the catalogue |
| `state` | `answered` \| `agent-proposed` \| `unknown` \| `not-applicable` |
| `answer` | free text |
| `provenance` | `operator` \| `file:{fileId}` \| `agent` |
| `confidence` | required when `state = agent-proposed` |
| `recordedAt` | |

There is deliberately **no state for "never asked"**. A question with no entry is a question the agent
has not reached, and the creation gate refuses while any exist. This is the whole point of the dossier
layer: the headline harm in this system is a *false pass*, and prose cannot distinguish "the client
says there are no by-products" from "we never got to that question". Both read as silence.

### 4.2 Tools

| Tool | Purpose |
|---|---|
| `write_summary(text)` | the prose brief; may be rewritten any time |
| `record_finding(questionId, answer, provenance)` | fills a dossier entry |
| `mark_unknown(questionId, reason)` | explicit gap |
| `mark_not_applicable(questionId, reason)` | explicit non-gap |
| `propose_components(components[])` | the per-component breakdown |
| `read_attachment(fileId, page)` | extracted text, **paged and on demand** |
| `create_project()` | §4.3 |
| `search_marker_library(...)` | existing `ToolBox` read — surfaces prior approved codes for reuse, as UX spec §4.1 requires |
| `search_learned_conclusions(...)` | existing `ToolBox` read — prior findings as evidence, never as ground truth |

**Deliberately absent: `search_web` and `search_regulatory`.** The interview elicits what the operator
knows. A regulatory verdict must trace to the synced corpus — that is why the Regulatory agent has no
web tool and never will — and open-ended search belongs to Discovery, where deterministic rails cap a
web-only candidate at Tier B. Giving the product's front door a web tool would put uncited chemistry
into the record at the earliest and least-reviewed point in the project. A test pins the absence.

**Also absent: any capability to start the analysis.** An agent can only act through its tools.

### 4.3 The creation gate is code, not prompt

`create_project` refuses — returning an error the model reads and acts on, never throwing — unless
all of:

1. `client` and `product` are non-blank.
2. At least one proposed component, each with `material`, `application`, `objective`, and **at least
   one market**.
3. A summary has been written.
4. **Every** question in the catalogue has a dossier entry. `unknown` and `not-applicable` pass;
   absence does not.

The markets rule reuses the rationale already written into `IntakeAnswers.BlankValue`: a component
with zero target markets has an empty regulatory screen, which is a false-pass mechanism. The error
text says so.

`create_project` is idempotent on `createdProjectId` — a retried tool call returns the existing
project rather than creating a second one.

### 4.4 Agent instructions — the rules that carry weight

- **Elicit, never assert.** No chemical, regulatory, or product fact from memory. The operator is the
  source; files are the source; the two knowledge tools are the source.
- **One or two questions at a time, in plain language. Never render a form.**
- **Accept "I don't know."** The operator is the Project Leader, not this client's process chemist.
  Record it as `unknown` and move on; do not press.
- **Read an attachment before asking what might be in it.**
- **Never infer one answer from another.** An inference is `agent-proposed` with its reasoning and a
  confidence, or it is not recorded.
- **Say what is still open before creating the project.**
- **You cannot start the analysis** and must never say or imply you have.

---

## 5. Attachments and extraction

### 5.1 Extraction is server-side and model-agnostic

Files are turned into text by **code, before any agent sees them**. Relying on a model's native
document or vision input would couple a data-ingestion decision to the choice of model, and the model
is not fixed.

`ITextExtractor`:

```
bool CanHandle(string contentType, string extension);
Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct);   // (text, status, error)
```

v1 implementations: PDF text layer · `.docx` · `.xlsx` (ClosedXML — already a dependency, used by
`tools/Smx.ReferenceData.Transform`) · `.csv`/`.tsv` · `.txt`/`.md`/`.json`/`.xml`.

Extraction runs in the **backend, synchronously at upload**. No agent, no dispatch, no queue.

### 5.2 An unreadable file is a visible fact, not silence

Every attachment carries `status`: `extracted` · `unsupported` · `failed`. An `unsupported` or
`failed` attachment appears in the agent's context by name, type and status, and the agent asks about
it — *"there's a `line-photo.jpg` I can't read; what does it show?"* The answer lands in the dossier
with provenance `operator`, annotated as describing that file.

This is the same principle as the open-questions list: a gap the system knows about gets filled by
conversation. It is also what keeps attachments from being decorative without requiring vision.

OCR, image, and scanned-PDF extractors are added later behind the same interface, with no schema
change and no agent change.

### 5.3 Storage

Blobs go to the existing StorageV2 account's `bronze` container (provisioned in
`infra/modules/data.bicep`), at `intake/{sessionId}/{fileId}/{filename}`, with extracted text as a
sibling `.txt` blob.

**Blobs are written once and never re-parented.** `IntakeBriefDoc.attachments` references the
session-scoped path. Copying blobs into a project-scoped path on creation would add a partial-failure
mode — project created, half the blobs moved — for no benefit: the session *document* is disposable,
the bytes never were.

Extracted text lives in blob storage, not in the Cosmos document, because a long PDF would exceed the
2 MB item limit.

Limits: 25 MB per file, 20 files per session. `read_attachment` is paged so a large document does not
enter the prompt whole.

---

## 6. XRF entry on Background

Removing the creation form removes the only place the physicist's measured XRF background — the
element pools — could be entered. Chat cannot fill the hole: `IntakeAnswers` already refuses element
pools by name, because an LLM transcribing measured numbers is a mechanism by which a shaved
background ships a marker under the detection floor that nobody can read in the field.

So the element pools get their own entry point, on the Background stage where they belong:

1. The operator uploads the physicist's result.
2. A **deterministic parser** — not a model — maps columns to `component / element / line / status /
   signalNote`.
3. Parsed rows render as **proposals**. They become the element pool only when the operator confirms
   them.
4. A manual grid is the fallback, and is where anything unparseable lands.

Two deliberate limits. v1 parses a **defined column shape** with a downloadable template rather than
pretending to understand arbitrary vendor exports — a parser that silently mis-maps a column is worse
than one that refuses. And the existing anti-rubber-stamping rule is enforced at confirmation: a
conditional (`L`) row cannot be confirmed until its signal-character note exists.

This is the one surface in the redesign that is still a table, and it should be: these are measured
numbers a human is signing for.

---

## 7. Streaming, and the orchestrator as a web host

The existing chat is record-as-bus with polling: a Cosmos write, change-feed pickup, an agent run,
another write, and a client poll. That is right for an occasional question on a stage. It is wrong for
a 15-plus-turn interview that is now the product's front door — multi-second silences with no partial
output, compounding across the conversation.

So interview turns **stream**, and are **still persisted**:

- The orchestrator moves from `Microsoft.NET.Sdk.Worker` to a web host and gains **internal ACA
  ingress** (today `infra/modules/compute.bicep` gives it ingress only while it is the placeholder
  image). The change-feed processor keeps running alongside.
- The **backend proxies** the SSE stream. JWT validation stays in one place, and the frontend keeps
  talking only to `/api/*`, which App Gateway and the Vite dev proxy already make same-origin.
- The orchestrator **writes the turn to the session document as it completes.** The record remains the
  source of truth and the transcript survives a reload; streaming is a delivery detail, not a state
  location.

All agent execution stays in the orchestrator. The backend still cannot run an agent.

---

## 8. Frontend

**`/new` — the interview.** Replaces the form in `NewProject.tsx` entirely. A full-height conversation,
a composer that accepts dropped files, attachment chips showing extraction status, and a single
collapsed coverage line — *"11 of 17 covered · see what's open"* — that the operator can expand but is
never *presented with* as a checklist to fill. The **Create the project** button mirrors the server's
gate client-side (as today's form mirrors `CreateProjectRequest.Validate`) so a refusal is never a
surprise; the server remains the authority.

**`/p/{id}/intake` — the filled dossier.** Summary, the proposed component table, the question list
with states and provenance, attachments, the collapsible transcript, and **Start Processing** with the
open-question count stated beside it. **Nothing on this screen is directly editable** (Law 4): the
component table is changed by telling the agent why, which is also how the change earns a Learned
Conclusion. The `MockBadge` comes off this screen — every value is read from the record.

**`/p/{id}/background`** gains the XRF upload-and-confirm surface of §6.

**Projects list** distinguishes `awaiting-confirmation` projects: created, dossier written, not started.

---

## 9. Testing

Beyond the obvious per-unit coverage:

- **Every agent tool is exercised through the real `AIFunction` via `InvokeAsync`**, never the C#
  method. A tool shipped dead-on-arrival for a full release because its test called the method.
- **A test pins that the interview agent's tool list contains no web or regulatory search**, and no
  start/gate capability.
- **A test pins that the emitted tool schemas contain no `sessionId`.**
- **A dispatch test that a project written by `create_project` does not fire the change feed**, and
  that `POST /start` does.
- **A test that a plain `POST /projects` with a full payload still starts immediately** — the
  regression that would silently disable `tools/Smx.Eval` (§2.4).
- **A test that `POST /start` succeeds with no element pools**, and that the stage needing physics
  parks rather than failing.
- **The creation gate's refusals are pure-function tests** in `Smx.Domain.Tests`, beside
  `IntakeAnswersTests`.
- **A test pins that `RecordDocRouter` ignores `intake-brief`.**
- Extraction: one test per extractor plus `unsupported` and `failed` paths reaching the agent's
  context as facts.
- Frontend: vitest coverage of the interview screen's gate mirroring and the brief screen's rendering
  of every dossier state.

---

## 10. Risks

**Streaming through MAF + Foundry is unproven in this repo.** `MafAgent` exposes only `SendAsync`;
whether `ChatClientAgent.RunStreamingAsync` yields real token streaming through the Foundry
Anthropic-native endpoint has never been tested here. This is **task 0** — a spike, before anything is
built on it. Fallback if it does not stream: the turn still persists, and the UI surfaces tool-call
progress (*"reading acme-questionnaire.pdf…"*), which is most of the perceived responsiveness.

**The orchestrator becoming a web host** touches ingress, health probes, and the backend→orchestrator
route. Contained, but real infra work, and `infra/` is updated in the same change.

**Prompt growth.** A long interview plus file excerpts makes a large per-turn prompt. Paged, on-demand
`read_attachment` is the main mitigation; if a thread crosses a threshold the answer is summarisation
*in the record*, not session state in memory.

---

## 11. Implementation order

Four plans, each independently verifiable:

1. **Session, interview agent, streaming, creation gate** — the new records, the orchestrator web
   host, the agent and its tools, `POST /start`, the `awaiting-confirmation` status.
2. **Attachments and extraction** — blob storage and its RBAC, `ITextExtractor` and the five v1
   implementations, the upload endpoint, `read_attachment`.
3. **Frontend** — the interview screen and the intake-brief screen; deletes the creation form and
   drops that screen's `MockBadge`.
4. **XRF entry on Background** — the parser, the confirmation surface, the manual grid.
