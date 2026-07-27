# Execution core — the pipeline runner, the unified stage agent, and the visible run

**Status:** design, 2026-07-27
**Scope:** merge `Smx.Orchestrator` into `Smx.Backend`; replace change-feed dispatch with a sequential
pipeline runner; merge `ChatAgent` into the stage agents so each stage has ONE agent and ONE thread; add a
persisted, streamable run trail; convert hard gates into sign-offs that gate only export and order.
**Base branch:** `feat/operator-usability-pass` (38 commits ahead of `main`, almost entirely frontend — the
current UI lives only there).
**Companion spec:** `2026-07-27-operator-surface-design.md` (Spec B) consumes §7's contract and can be built
in parallel against mocks.
**Supersedes:** the record-as-bus dispatch model of `2026-07-08-agent-backend-design.md` §3. The *record*
survives; the *bus* does not.

---

## 1. Why this exists

**The operator cannot see the agents work, and the architecture is the reason.**

A stage run is dispatched off the Cosmos change feed (`StageDispatcher.OnRecordChangedAsync`). Its entire
observable output is three fields on the project record — `status`, `attempts`, `error`. `MafAgent.SendAsync`
awaits `RunAsync` and returns `response.Text`; every tool call, every search, every rejected attempt is
discarded on the floor. The stage pill turns amber, then green. There is nothing else to render, however good
the UI is.

**The chat is a different agent wearing the stage agent's name.** `ChatAgent.Instructions` opens with *"You
are the same agent that produced this stage's analysis, and you are answering for it."* It is not. The stage
agent starts a fresh MAF thread (`ValidatedAgentRunner.cs:29`), emits JSON, and the thread is destroyed.
`ChatAgent` is a separate agent with separate instructions that has never seen how the analysis was reached;
it reconstructs an answer from the stored output and the same read tools. In a system whose founding premise
is that confident wrongness causes harm, "why did you pick this?" currently returns a plausible
reconstruction rather than a recollection.

**The pool agent runs invisibly.** On a need-only project the pool agent is the *first* agent to run
(`StageDispatcher.cs:102-106`) and it is slow — its instructions require model reasoning plus a reference
search plus a web search per component. `GET /projects/{id}/pool` exists (`ProjectEndpoints.cs:186`) and
nothing in the frontend calls it. `domain/stages.ts:9-12` deliberately hides `pool` from the spine as
"derived server-side". It is not an implementation detail; it is a minutes-long agent whose hypothesis
determines everything downstream.

**The bus does not deliver what it costs.** Its purpose is durable dispatch. The dispatcher's own comments
record three times that it does not achieve that: *"the failure mode is checkpoint-and-lose: the stage sits
silently stuck at `pending`, nothing left on the feed to redeliver it"* (`StageDispatcher.cs:583-586`, and
again at `:306-313`, `:145-158`). Meanwhile it costs 1,149 lines, a large fraction of which is at-least-once
idempotency bookkeeping — guard on status not doc-existence, re-read rather than trust the fed snapshot,
latch the transition, order the mutations. Every one of those comments marks a bug that was hit or narrowly
avoided.

And because a run is dispatched with no HTTP request open, on an arbitrary replica, **nothing can stream**.

### The priority change this design encodes

The project's stated priority has changed: interactivity, clarity and simplicity now outrank the maximal
correctness apparatus. This is a deliberate product decision, recorded here so the removals below read as
intent rather than erosion. It is scoped, not total — §9 keeps every rail that costs the operator nothing and
§8 keeps a signature in front of both irreversible acts.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | Keep the record; drop the bus. Every stage still persists its output doc. What goes is "a doc write triggers the next stage." | The docs are the history, the audit trail and the resume point, and they cost the UX nothing. The *trigger* semantics are what forbid streaming and force the idempotency bookkeeping. |
| **D2** | One `Smx.Backend` service runs the API, the agents and the live stream. `Smx.Orchestrator` is deleted. | The HTTP relay (`IntakeSessionEndpoints.cs:44-60`), `ORCHESTRATOR_BASE_URL` and the leases container exist *only* because the backend cannot run an agent. With a live runner the split is the last thing between the browser and the process doing the work. One operator makes the "API responsiveness" argument for splitting moot. |
| **D3** | A **sequential pipeline runner** per project, owned by one in-process task. | Plain sequential code replaces at-least-once dispatch. It streams directly because it *is* the live process; cancel is one `CancellationTokenSource`; the mailbox is a queue it drains. |
| **D4** | **One agent and one thread per stage.** `ChatAgent` folds into each stage agent's instructions. Turns are either `produce` (schema-validated) or `converse` (prose). | Law 9 forbids *Discovery and Regulatory* sharing a thread — cross-stage isolation. It never forbade the Discovery agent and "the thing you talk to on the Discovery screen" being one agent; the ChatAgent prompt already claims they are. Unifying removes a fabrication surface. |
| **D5** | Unification is at the **transcript** level, not the session level. | MAF sessions cannot be rehydrated — stated in both `ChatAgent.cs:49-50` and `AgentRuns.cs:72-74`. A persisted thread re-rendered into every prompt is how chat already works; the produce turn simply joins it. |
| **D6** | Operator messages are **queued**, answered within seconds by a concurrent converse turn, and the converse agent holds a `restart_stage` tool. | The user's chosen default is queued, with the requirement that the agent actually sees it soon. A concurrent converse turn answers immediately; `restart_stage` lets the *agent* decide whether the message warrants discarding in-flight work, rather than always or never. |
| **D7** | Run steps are written **by code, from observed facts**. No model-authored narration anywhere in the trail. | A step that claims a search that never happened is the same class of harm as a fabricated verdict. Code-written steps can be wrong only if the code is wrong, which is testable. |
| **D8** | The run trail lives in a **new `runs` container**, partitioned by `/projectId`. | Keeps high-volume append-only telemetry off the project record, and out of any query that reads project state. |
| **D9** | Trail writes are **best-effort**; a failure is logged and swallowed. | Telemetry must never be what fails a regulatory screen. The stage's own status and error remain authoritative; the trail explains, it does not adjudicate. |
| **D10** | Gates become **sign-offs**. The pipeline never parks. Signatures gate exactly two irreversible acts: exporting the compliance package and placing an order. | The operator sees a complete proposed answer in one sitting. Nothing upstream of an irreversible act needs a human to unblock it. |
| **D11** | Validation splits: **structural** errors still reject and retry; **evidence-quality** errors become flags on the output. | A transposed CAS is provably wrong and costs microseconds to catch. An uncited claim is a judgement call that currently burns three multi-minute attempts and can fail the run outright. Flagging shows the operator *more*, sooner. |
| **D12** | **No migration** for in-flight project records. | Dev project records are disposable; re-running an interview is cheaper than a migration. Corpora, reference data, SDS registry and the knowledge layer are untouched. |

---

## 3. The pipeline runner

### 3.1 Shape

```
POST /projects/{id}/start        →  PipelineSupervisor.Start(projectId)
                                    └─ one Task per project: PipelineRunner.RunAsync
```

`PipelineRunner.RunAsync(projectId, ct)` walks the stages in order and calls them as ordinary sequential
code:

```
intake → pool → background → discovery → regulatory → matrix → dosing → cost → decision
```

For each stage: skip if its output doc already exists (this is what makes resume free), otherwise open a run,
execute, persist the output, close the run. `background` remains the pass-through it is today; `matrix` is a
deterministic assembly (`TryAssembleAsync`) and gets a run with `agent: null` like Cost.

**`Stages.All` gains `Pool`.** It is the hand-maintained list of chattable stages (`RecordIds.cs:43`), and
Spec B's Intake/Pool composer needs the pool thread to be postable. `Background` stays out — it is a
pass-through with no agent to talk to. `ChatEndpointsTests` reflects over the class and will fail if the two
part company, which is the intended tripwire.

`PipelineSupervisor` is a hosted service holding `projectId → RunningPipeline { Task, Cts, Mailbox,
EventStream }`. It is the single registry the cancel endpoint, the message endpoint and the SSE endpoint all
resolve against.

**Start is still the operator's.** The existing intake `awaiting-confirmation` → **Start Processing** flow is
unchanged; `POST /projects/{id}/start` is what launches the runner. The agent may create a project; only the
operator may start one.

**One pipeline per project.** A second start while one is live returns `409`.

### 3.2 Regulatory fan-out

Regulatory remains parallel across (candidate × component) — it is the one stage where serial execution would
be a real wall-clock regression. The runner opens a **parent run** (`subject: null`) for the stage and one
child run per substance carrying `parentRunId`. Grouping in the UI is therefore explicit in the data, not
inferred from timing.

Parallelism stays bounded by the existing `regulatoryParallelism` setting.

### 3.3 Resume, cancel, retry

**Resume.** On startup the supervisor queries for projects holding any stage in `running` and re-enters the
runner for each. Because per-stage skip is keyed on the output doc's existence, resume restarts at the stage
that was interrupted. Any run left `running` by a dead process is stamped `interrupted` first, so the trail
shows the gap rather than hiding it. This is strictly better than today, where such a project stalls silently
and permanently.

**Cancel.** `POST /projects/{id}/runs/{runId}/cancel` cancels that run's linked CTS. Operator-cancellation
and host-shutdown must be distinguished at the `catch`: the run's own CTS is linked to the host token, and
only `runCts.IsCancellationRequested && !hostToken.IsCancellationRequested` is an operator cancel. A cancelled
run stamps `cancelled`; a shutdown leaves the stage resumable. Cancel is offered only while `outcome ==
running`, and cannot un-write what already landed. For regulatory, cancel is offered on the **parent** run
only — cancelling one substance of fourteen leaves a candidate set that looks screened and is not.

**Retry.** `POST /projects/{id}/stages/{stage}/rerun` re-enters the runner at that stage, allowed only from
`failed`, `needs-review` or `cancelled`. From `done` it is a `422`: re-running a landed stage is
revise-with-reason's job, which records why. Retry must not become a backdoor that discards analysis with no
reason on file.

---

## 4. The unified stage agent

### 4.1 One identity, one thread, two turn kinds

Each stage has a single agent identity and a single persisted thread per `(projectId, stage)`. `ChatAgent`
is deleted; its conversational duties — answer only from tools and record, cite what you relied on, you can
never sign a gate, `apply_revision` requires the operator's reason — are appended to every stage agent's
`Instructions`, once, from a shared constant.

**A produce turn** is the autonomous one. Task line: *"produce this stage's output now — reply with ONLY the
JSON object."* It runs through `ValidatedAgentRunner` with the stage's `Validate` intact (§9). Its steps
append to the thread as they happen.

**A converse turn** answers an operator message. Prose, no schema, may call `apply_revision` or
`restart_stage`.

Both use the same agent, the same tools, and the same thread as memory. What differs is the task line and
whether the reply is schema-validated.

### 4.2 What this changes about safety

Folding operator messages into the produce turn's context means an operator can argue with an agent
mid-analysis. That is the requested control. It is acceptable because **every rail is in code, not in the
prompt** — `CasNumber.IsValid`, the web-only-⇒-Tier-B ceiling, the detection floor, the compliant-set
restriction. A persuasive operator can change what an agent proposes; they cannot talk it past a check digit.

---

## 5. The mailbox

`POST /projects/{id}/stages/{stage}/messages` appends the operator's message to that stage's thread
immediately (so it is visible at once) and enqueues it on the project's mailbox.

The runner drains the mailbox in two places:

1. **Concurrently, on arrival.** A converse turn runs against the stage's thread and answers within seconds.
   The operator is never left talking to a wall. This turn runs *alongside* the in-flight produce turn; the
   thread's `seq` is assigned server-side at append, so concurrent writers cannot corrupt ordering.
2. **Between stages.** The next stage's produce turn is prompted with the full thread, so the message is in
   context by construction.

**Why not between tool calls.** MAF's `UseFunctionInvocation` runs the whole tool loop inside one `RunAsync`
call; there is no seam to interject at without hand-rolling the function-invocation loop. That cost is not
worth paying, because the converse turn already answers in seconds and `restart_stage` already covers the
case where the message genuinely invalidates in-flight work.

**`restart_stage`** is a tool available only on converse turns. When the operator's message materially
changes the current stage's inputs (*"wrong component — the lid is aluminium, not steel"*), the agent calls
it: the runner cancels the in-flight run for that stage and re-enters it with the message now in the thread.
The agent, not a blanket policy, decides whether a message is a question or a correction.

**Ordering guarantee for the UI:** a message posted while a run is live returns `queued: true`, which is what
lets the dock say *"the agent is working — it'll see this when it finishes"* rather than implying silence.

---

## 6. The run trail

### 6.1 Records

A **`RunDoc`** per agent invocation, in the `runs` container (partition `/projectId`), *not* the `record`
container:

| field | meaning |
|---|---|
| `id` | `run\|{projectId}\|{stage}\|{ordinal}` |
| `projectId`, `stage` | |
| `agent` | `pool`, `discovery`, … or `null` for a deterministic stage |
| `subject` | `null`, or `{cas}\|{componentId}` for a regulatory child run |
| `parentRunId` | `null`, or the stage's parent run (regulatory fan-out) |
| `trigger` | `pipeline` \| `operator-retry` \| `revision` \| `restart` |
| `startedAt`, `endedAt` | ISO 8601, `endedAt` null while running |
| `outcome` | `running` \| `done` \| `needs-review` \| `failed` \| `cancelled` \| `interrupted` |
| `error` | the same string that lands on the stage |
| `steps[]` | append-only |

A **`RunStep`**: `seq` (monotonic within the run), `at`, `kind`, `text` (display-ready, code-written), and an
optional structured `detail`. Five kinds:

- **`started`** — what the agent was handed. *"Proposing a marker pool for 3 components: bottle (PET), label
  (paper), liquid (fuel oil)."*
- **`tool-call`** — *"Searched the SMX reference corpus for 'zirconium oxide solubility in PET' — 6 hits."*
- **`rejected`** — *"Output rejected: suggestion references unknown component 'lid'. Retrying, attempt 2 of
  3."* These happen today and vanish entirely.
- **`output`** — a code-generated summary of the doc produced. *"Proposed 11 markers across 3 components —
  Zr/compound, Y/compound, Ce/organocomplex…"*
- **`outcome`** — terminal, mirroring the stage stamp.

**Deterministic stages get runs too.** Cost is a catalog lookup, Matrix is a fold, `DecisionAssembler` is
arithmetic. If only agent stages produced a trail, the operator would learn that a silent stage means
"broken" when it means "this one is arithmetic". They open runs with `agent: null` and write their own
code-observed steps.

### 6.2 Where steps are written

| Point | Writes |
|---|---|
| `PipelineRunner` | `started`, `output`, `outcome` |
| `MafAgent.SendAsync` | `tool-call`, read off `response.Messages` (`FunctionCallContent` / `FunctionResultContent`) — beside the citation scan already living there |
| `ValidatedAgentRunner` | `rejected`, at `:48-50` where `lastError` is currently swallowed |

The seam: `ISmxAgent` gains `IRunTrail Trail { get; }`. The runner passes an `IRunTrail` into
`IAgentRuns.RunXAsync`; `AgentRuns` hands it to the `MafAgent` constructor; everything downstream reads
`agent.Trail`. The seven agents' own `RunAsync` statics are untouched. Explicit parameters, not ambient
`AsyncLocal` — forgetting one should be a compile error.

**Known limit, accepted:** a step lands when a tool call *returns*, not while it runs. Inside a single
40-second `search_reference` the trail is quiet. The `started` step carries the inputs so the dock can say
*"Discovery, working — 2 tool calls so far"* rather than going blank. No sub-tool-call progress is invented.

### 6.3 Retention

A run doc lives as long as the project. This is the audit answer to *"how did this verdict get here"*, which
the system currently cannot answer at all.

---

## 7. The API contract

**This section is the interface Spec B codes against. Changes here are breaking changes for Track 2.**

All paths are under `/api` (same-origin via Vite's proxy in dev, App Gateway's `apiPathRule` in Azure —
unchanged).

### 7.1 The thread — one unified transcript per stage

`GET /projects/{id}/stages/{stage}/thread` → `ThreadEntry[]`, oldest first.

Because the agent and the conversation now share one thread, the thread **is** the timeline. The client does
not merge two sources.

```ts
type ThreadEntry =
  | { seq: number; at: string; kind: 'message'; role: 'operator' | 'agent';
      text: string; status: 'queued' | 'answered' | 'failed'; error: string | null }
  | { seq: number; at: string; kind: 'run'; run: RunSummary };

interface RunSummary {
  runId: string;
  stage: string;
  agent: string | null;          // null ⇒ deterministic stage, no model involved
  subject: string | null;        // "1314-23-4|bottle" for a regulatory child run
  parentRunId: string | null;    // set on regulatory child runs
  trigger: 'pipeline' | 'operator-retry' | 'revision' | 'restart';
  startedAt: string;
  endedAt: string | null;
  outcome: 'running' | 'done' | 'needs-review' | 'failed' | 'cancelled' | 'interrupted';
  error: string | null;
  steps: RunStep[];
}

interface RunStep {
  seq: number;                   // monotonic within the run
  at: string;
  kind: 'started' | 'tool-call' | 'rejected' | 'output' | 'outcome';
  text: string;                  // display-ready, written by code
  detail?: {
    tool?: string;
    query?: string;
    resultCount?: number;
    recordId?: string;           // the record this step wrote
    attempt?: number; of?: number;
  };
}
```

`seq` on `ThreadEntry` is monotonic within the thread and assigned server-side at append.

### 7.2 The stream

`GET /projects/{id}/stages/{stage}/thread/stream?since={cursor}` → `text/event-stream`.

| event | `data` | `id` |
|---|---|---|
| `entry` | a `ThreadEntry` (message landed, or run opened) | `e{seq}` |
| `step` | `{ runId, step: RunStep }` | `e{entrySeq}.s{stepSeq}` |
| `run` | `{ runId, endedAt, outcome, error }` — a run reached a terminal state | `e{entrySeq}.r` |

`?since=` takes the last `id` the client saw; the server replays everything after it. A `:` heartbeat every
15s keeps App Gateway from reaping an idle connection during a long tool call.

**The stream is an accelerator, never the source of truth.** The client seeds from §7.1, opens the stream,
and reconciles on `(runId, seq)` so a replayed frame is idempotent. If the stream fails, the client falls
back to polling §7.1. A dead stream costs latency, never content.

### 7.3 Control

| Endpoint | Behaviour |
|---|---|
| `POST /projects/{id}/start` | **Exists** (`ProjectEndpoints.cs:147`) with its readiness checks intact; it now launches the runner instead of writing a doc. `202`; `409` if a pipeline is already live. |
| `POST /projects/{id}/stages/{stage}/messages` `{text}` | `202 { messageId, seq, queued }`. `queued: true` ⇒ a run is in flight. `422` on blank text or unknown stage. |
| `POST /projects/{id}/runs/{runId}/cancel` | `202`; `409` unless `outcome == running`; `422` on a regulatory child run (cancel the parent). |
| `POST /projects/{id}/stages/{stage}/rerun` | `202` from `failed` / `needs-review` / `cancelled`; `422` from `done` or `running`. |
| `GET /projects/{id}/runs?stage=` | Every run for the project (or stage), oldest first — the replay/audit read. |

**Unchanged and reused by Spec B:** `GET /projects/{id}` (stage spine), `GET /projects/{id}/pool` (exists,
uncalled), `GET /projects`, and every existing stage read endpoint.

**Removed:** `GET`/`POST /projects/{id}/stages/{stage}/chat` — superseded by §7.1 and §7.3.

---

## 8. Gates become sign-offs

The pipeline runs end-to-end without waiting for a human. `GateDoc` survives as a **record of a signature**;
what it no longer does is stop execution.

| Was | Becomes |
|---|---|
| Regulatory `awaiting-RE` parks the pipeline until the R.E. determination is entered and the gate signed | The stage lands with its verdicts. The determination is recorded whenever the operator has it. The signed gate is a **precondition on `GET /exports/compliance-package`** and on placing an order. |
| Decision parks at `awaiting-VP` until the VP determination is signed | Decision lands with its proposed codes. The VP determination is a **precondition on `POST /projects/{id}/orders/{cas}`**, and signing it still writes the Marker Library entry and the Learned Conclusion. |
| Dosing parks at `awaiting-physics` when no measured background is on file | Dosing proceeds using a **declared default floor** from the device's generic detection limit, and flags the component *"estimated floor — no physicist measurement on file"*. The flag blocks the **order**, not the pipeline. |
| Dosing parks at `awaiting-operator` when a metal loading is unknown | Same treatment: proceed with the stoichiometric loading where derivable, else flag *"assumed loading"*, and block the order. |

Removed with them: the arming rules and the anti-rubber-stamping "every flagged item must be opened before
the gate arms" machinery. `RegulatoryGate.Armable` and `VpGate.Armable` survive as **order/export
preconditions** rather than pipeline blockers, so the two irreversible acts still refuse to proceed over an
incomplete analysis. The MSDS-before-order precondition is unchanged — it was always an order precondition,
never a pipeline park.

The four `awaiting-*` stage statuses are deleted. `StageStatus` becomes `pending | running | done |
needs-review | failed | cancelled`.

---

## 9. Validation: what stays, what relaxes

The split is between errors about **structure** and errors about **evidence quality**.

**Still rejects and retries** (deterministic, microseconds, zero operator friction):

- CAS check digits (`CasNumber.IsValid`) — a transposed digit is *provably* wrong before it reaches
  procurement.
- Closed enums (`formClass`, tier, verdict status), component-id binding, required fields.
- Schema/JSON parse failures.
- The web-only-⇒-Tier-B ceiling and the `preferred` prohibition — a rail over model claims.
- Dosing above the detection floor, and the compliant-set restriction.

**Becomes a flag on the output, not a rejection:**

- Missing or thin citations. The item lands with `uncited: true` and a reason; the UI shows it as flagged.
- "Every suggestion must name its basis" style completeness checks.

Rationale: an uncited claim currently burns up to three multi-minute attempts and can fail the run outright,
leaving the operator with nothing. Flagged-and-visible shows them more, sooner, and the flag is exactly the
thing they would have looked at anyway.

`ValidatedAgentRunner` keeps its 3-attempt loop for structural errors and emits a `rejected` step per
attempt, so retries stop being invisible.

---

## 10. The service merge

| Change | Detail |
|---|---|
| Projects | `Smx.Orchestrator/{Agents,Dispatch→Pipeline,Knowledge,Cost}` move into `Smx.Backend`. `Smx.Orchestrator.csproj` and its Container App are deleted. |
| Tests | `Smx.Orchestrator.Tests` merges into `Smx.Backend.Tests`. **Watch the TFM:** `Smx.Backend.Tests` targets `net10.0` while everything else is `net8.0 + RollForward=Major` (CLAUDE.md). The merged project keeps `net10.0`. |
| Interview | `InterviewEndpoints` moves into the backend; `IntakeSessionEndpoints`' SSE proxy and `ORCHESTRATOR_BASE_URL` are deleted. The interview streams directly. |
| Bicep | `compute.bicep` drops the `orchestrator` app and `orchestratorBaseUrlEnv`; the backend inherits the orchestrator's env (Search Proxy URL, Bronze) and its `minReplicas: 1`. `data.bicep` drops the `leases` container. |
| Scripts | `build-images.sh` builds two images, not three. `swap-images.sh`, `deploy.sh` and their `.ps1` twins updated in lockstep — they are twins, not alternatives. |
| Eval | `tools/Smx.Eval` drives the API and should keep working; its expectations around `awaiting-*` parks change with §8. |

---

## 11. Failure modes

| Failure | Behaviour |
|---|---|
| Agent throws mid-stage | Run stamps `failed` with the message; stage stamps `failed`; the pipeline **stops** at that stage. The operator retries or messages the agent. |
| Process dies mid-run | On restart the supervisor stamps the orphaned run `interrupted` and re-enters the runner at that stage. Previously: a permanent silent stall. |
| Trail write fails | Logged and swallowed (D9). The run continues; the trail has a hole. Stage status and error remain authoritative. |
| Two clients stream the same thread | Both are served from the same in-process event stream; each has its own `since` cursor. |
| Operator cancels, then the host shuts down | Distinguished by the linked-CTS check (§3.3). Only an operator cancel stamps `cancelled`. |
| Message arrives for a stage the pipeline has passed | Answered by a converse turn on that stage's thread. `restart_stage` re-enters the pipeline at that stage; downstream stages re-run because their skip condition is keyed on output docs, which the restart clears. |

---

## 12. Testing

- **`FakeRunTrail`** asserting the exact step sequence per stage — the primary regression net for §6. Ports
  the assertions of `StageDispatcherTests`, not its change-feed harness.
- **`ValidatedAgentRunner`**: a validator failing twice then passing emits exactly two `rejected` steps; an
  evidence-quality failure emits zero and lands flagged (§9).
- **`MafAgent`**: tool-call capture off a synthetic response, beside the existing citation test.
- **Runner**: skip-on-existing-doc (resume); `failed` halts the pipeline; a restart clears downstream docs.
- **Cancel**: operator-cancel stamps `cancelled`; shutdown-cancel does not. **Retry**: from `failed`
  re-dispatches, from `done` `422`s.
- **Stream**: `?since=` replays exactly the frames after the cursor and none before; a project with no runs
  emits only heartbeats.
- **Mailbox**: a message posted mid-run returns `queued: true`, is answered by a converse turn, and appears
  in the next stage's produce prompt.
- **Gates**: export and order both refuse without their signature; no stage ever enters an `awaiting-*`
  status (the statuses no longer exist).

---

## 13. Non-goals

- **Making the pool revisable.** `PoolAgent.RunAsync` already accepts a `RevisionDoc` and the Discovery
  revise path already rehydrates from the `PoolDoc`, but `RevisionEffects.IsRevisable` excludes `pool` — and
  wiring it also means declaring `BreaksRegulatoryGate(pool)` and a `ConclusionKind`. Coherent work; not this
  spec. The levers on a bad pool here are message, restart and retry.
- **Sub-tool-call progress.** See §6.2.
- **Multi-project concurrency limits.** One operator, one project at a time in practice.
- **Migrating in-flight project records** (D12).
- **Removing the `MockBadge` screens** (Discovery, Dosing, Cost, Decision fixture data). Tracked separately
  in `2026-07-27-remove-mock-data-design.md`.

---

## 14. Plans

| Plan | Contents | Unblocks |
|---|---|---|
| **A1** | Service merge, pipeline runner, supervisor, run trail, stream endpoints, cancel/retry | Track 2 integration |
| **A2** | Unified stage agent, thread migration, mailbox, `restart_stage`, validation split (§9) | |
| **A3** | Gates → sign-offs, `awaiting-*` removal, export/order preconditions | |

A1 lands first so the dock has something real to read. A1 is substantially **deletion** — the leases
container, the relay, the idempotency guards, `ORCHESTRATOR_BASE_URL` — and deletion should be sequenced
before the new runner is written, not after.
