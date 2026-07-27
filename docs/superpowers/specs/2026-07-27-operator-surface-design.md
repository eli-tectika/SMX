# Operator surface — the merged timeline dock, Intake & pool, and live steering

**Status:** design, 2026-07-27
**Scope:** `src/smx-web` only. The dock becomes one merged timeline over the unified thread; `pool` gets a
home on Intake; regulatory's fan-out becomes legible; cancel/retry reach the UI.
**Base branch:** `feat/operator-usability-pass`.
**Depends on:** `2026-07-27-execution-core-design.md` **§7 only** — the API contract. Everything here is
buildable against mocks before the server lands, and integrates when A1 does.

---

## 1. Why this exists

The operator watches a stage pill turn amber and then green and is told nothing else. When several agents run
in sequence — which is the normal case — the screen is indistinguishable from a screen where nothing is
happening. The pool agent, which runs first and longest on a need-only project, has no screen at all: no
spine entry, no output surface, and `GET /projects/{id}/pool` has never had a caller.

Spec A makes the work observable and steerable. This spec is where the operator actually observes and steers
it.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| **B1** | The dock is **one merged timeline**, not a trail pane above a chat pane. | Spec A D4 makes the agent and the conversation one agent on one thread. Two panes would re-split on screen what was just unified underneath. |
| **B2** | A run renders as a **collapsible group**: expanded while running, collapsed to a one-line summary when it lands. | A finished run is history; a running one is the thing you are watching. Auto-collapse keeps a seven-stage project readable without hiding anything. |
| **B3** | Regulatory's children group under their **parent run**, never interleaved. | Fourteen concurrent trails interleaved chronologically would be strictly worse than today's nothing. `parentRunId` (A §7.1) makes the grouping explicit rather than inferred. |
| **B4** | Intake and pool become **one spine entry**, "Intake & pool". `backedBy` becomes an ordered list and the pill folds both statuses, with **attention beating completion**. | The operator's whole input is the need; intake transcribes it and pool turns it into a hypothesis. They are one step from the operator's side. A failed pool must never hide behind a done intake. |
| **B5** | The dock there carries a **two-tab composer, Intake / Pool**, defaulting to Pool once a pool exists. | The backend genuinely has two threads (one per stage). An untabbed composer would silently post to a stage the operator did not mean. |
| **B6** | The VP gate screen gets a **read-only trail** — trail, no composer. | A Decision agent does run and its trail should be visible, but `surface: 'record'` exists because a signature is not a conversation. |
| **B7** | The stream is an **accelerator**; the client always seeds and can always fall back to polling. | A dead stream must cost latency, never content. |
| **B8** | Every step rendered is code-authored (A D7); **no `MockBadge` on any surface this spec adds.** | The badge is load-bearing: a fabricated verdict must never pass for an agent-produced one. Everything here reads a real endpoint. |

---

## 3. The transport layer

**`useThread(projectId, stage)`** — one hook, the only thing that talks to A §7.1–7.2.

1. Seeds from `GET /projects/{id}/stages/{stage}/thread`.
2. Opens `GET …/thread/stream?since={lastId}` via `fetch` + a stream reader — **not `EventSource`**, which
   cannot carry the auth header. The existing `createSseParser` ([api/sse.ts](../../../src/smx-web/src/api/sse.ts))
   parses the frames and already skips `:` heartbeats, with a test for it.
3. Reconciles on `(runId, seq)` for steps and on entry `seq` for entries, so a replayed frame after a
   reconnect is idempotent.
4. On stream error: exponential backoff reconnect with the last `id` as `since`. After repeated failure,
   degrades to polling `GET …/thread` on the existing [`usePolling`](../../../src/smx-web/src/hooks/usePolling.ts).

The hook exposes `{ entries, live, error }`. `live` drives a small "connected" affordance — the operator
should be able to tell "nothing is happening" from "I am not being told what is happening".

---

## 4. The dock

Replaces `AgentPanel`'s `LiveChat`. One scroll, in `seq` order, of two entry kinds.

**A message** renders as it does today (`bub ba` / `bub bu`).

**A run** renders as a group:

```
▸ Pool agent · proposed 11 markers across 3 components · 4 tool calls · 38s
```

expanded while running:

```
▾ Pool agent — working
    Proposing a marker pool for 3 components: bottle (PET), label (paper), liquid (fuel oil)
    Searched the SMX reference corpus for "zirconium oxide solubility in PET" — 6 hits
    Searched the web for "inorganic taggants for fuel oil" — 8 results
    Output rejected: suggestion references unknown component 'lid'. Retrying, attempt 2 of 3
```

- `agent: null` runs are labelled by stage with no agent name and a distinct icon — a deterministic step is
  not an agent, and the operator should not learn to read arithmetic as reasoning.
- A `rejected` step is visually distinct but **not** an error — it is the system working correctly.
- `outcome: 'failed' | 'cancelled' | 'interrupted'` shows the error inline, with the retry control (§6).
- `detail.recordId` renders as the existing audit chip — the link from a step to the record it wrote.

**Regulatory** renders its parent run as the group, with a progress count in the header
(*"screening 14 substances — 9 done"*) and each child expandable beneath it by `subject`.

`useStickToBottom` already provides the scroll behaviour, keyed on the entry count plus the live step count.

**Accessibility** follows what `AgentPanel` established: a one-shot `sr-only` beacon on a run reaching a
terminal state, announcing *its own outcome* rather than assuming success — the existing code has a long
comment explaining why that distinction matters, and it applies unchanged.

---

## 5. Screens

**Intake & pool** (`/p/{id}/intake`). The spine entry becomes `{ slug: 'intake', label: 'Intake & pool',
backedBy: ['intake', 'pool'] }`; `backendStage()` returns the list and `pillClass` folds the statuses —
`running` if either runs, `failed`/`needs-review` if either is, `done` only when both are.

Below the existing brief, a **Proposed pool** section reading `GET /projects/{id}/pool`: per component, the
element, form-class, rationale and citations, with uncited suggestions flagged (A §9). This is the answer to
"where do the pool's results land".

The dock's composer carries the Intake / Pool tabs (B5). The timeline above spans both stages' threads,
merged by timestamp.

**VP gate** (`/p/{id}/decision`). Read-only trail, no composer (B6).

**Projects list.** Each card gains a live line — *"Pool agent — proposing markers, 2 tool calls"* — from the
project's most recent running run. This is the cheap answer to "where should I even look". Polled with the
existing list refresh; no stream.

**Every other stage screen** keeps its current content and swaps `AgentPanel` for the new dock.

---

## 6. Steering controls

| Control | Where | Behaviour |
|---|---|---|
| **Send** | composer | `POST …/messages`. On `queued: true`, the entry renders with a quiet note — *"the agent is working; it'll see this when it finishes"* — and no spinner, because nothing is pending on it. |
| **Cancel** | run group header, only while `outcome: 'running'` | `POST /projects/{id}/runs/{runId}/cancel`. On a regulatory child the control is absent — it lives on the parent only (A §3.3). |
| **Retry** | run group header, on `failed` / `needs-review` / `cancelled` | `POST …/stages/{stage}/rerun`. Absent on `done`; the server `422`s it anyway. |

All three are optimistic-free: they post, then let the stream deliver the truth. No control fakes a state the
server has not confirmed.

---

## 7. Testing

- Render: run group collapse/expand; a running group auto-expanded and a landed one auto-collapsed;
  regulatory children grouped under the parent and never at top level; `agent: null` runs labelled as
  deterministic.
- Reconciliation: a replayed `step` frame after reconnect does not duplicate; an out-of-order frame lands in
  `seq` order.
- Degradation: with the stream failing, the dock still fills from `GET …/thread` and `live` reads false.
- Spine: the folded Intake & pool pill shows `running` when only pool runs, and `failed` when pool failed and
  intake is done.
- Controls: cancel absent unless running; retry absent on `done`; cancel absent on a regulatory child.
- Composer: posting with a run in flight renders the queued note.

**Not coverable in jsdom:** the merged timeline's scroll/stick behaviour under a live stream, and the group
collapse animation. If those need verifying rather than assuming, they go to the Windows Playwright runner
(see the E2E notes in `src/smx-web/README.md`).

---

## 8. Mocks — how Track 2 runs ahead of Track 1

The repo already carries an MSW layer ([src/smx-web/src/mocks](../../../src/smx-web/src/mocks/)) built for
exactly this. Track 2 adds handlers for A §7.1–7.3, including a **scripted stream** that emits entries and
steps on a timer so the dock's live behaviour — group expansion, step arrival, stick-to-bottom, a run landing
— is developed and tested against real timing rather than a static fixture.

These handlers are development scaffolding, not shipped fixture data: `vite.config.ts` already excludes the
MSW worker from any build that is not `VITE_ENABLE_DEMO=true`. No `MockBadge` is involved and none is added
(B8) — the badge marks *fabricated content presented as real*, which a dev-only mock of an agreed contract is
not.

---

## 9. Non-goals

- A project-wide activity stream. The dock is per-stage by decision; the spine pills and the projects-list
  line are how the operator knows where to look.
- Removing the existing `MockBadge` screens (Discovery, Dosing, Cost, Decision) — tracked in
  `2026-07-27-remove-mock-data-design.md`.
- Rendering citation chips as links. `Citation` still carries no `documentId`; deriving one by parsing the
  free-text reference would produce links that are usually right, and a chip that opens the wrong regulation
  is worse than one that opens nothing.
