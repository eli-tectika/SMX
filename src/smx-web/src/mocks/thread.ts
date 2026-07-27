import type { RunStep, ThreadEntry } from '../api/thread';

/**
 * Dev-only scaffolding for the thread contract (execution-core-design §7).
 *
 * Not fixture data in the MockBadge sense: the badge marks fabricated CONTENT presented as an
 * agent's real output. This is a stand-in for an agreed wire format during parallel development,
 * excluded from every build that is not VITE_ENABLE_DEMO=true (vite.config.ts publicDir).
 */

export const mockThread = (stage: string): ThreadEntry[] => [
  {
    seq: 1,
    at: '2026-07-27T10:00:00.000Z',
    kind: 'run',
    run: {
      runId: `run|proj-demo|${stage}|1`,
      stage,
      agent: stage,
      subject: null,
      parentRunId: null,
      trigger: 'pipeline',
      startedAt: '2026-07-27T10:00:00.000Z',
      endedAt: '2026-07-27T10:00:38.000Z',
      outcome: 'done',
      error: null,
      steps: [
        {
          seq: 1,
          at: '2026-07-27T10:00:00.000Z',
          kind: 'started',
          text: 'Proposing a marker pool for 3 components: bottle (PET), label (paper), liquid (fuel oil).',
        },
        {
          seq: 2,
          at: '2026-07-27T10:00:11.000Z',
          kind: 'tool-call',
          text: 'Searched the SMX reference corpus for "zirconium oxide solubility in PET" — 6 hits.',
          detail: {
            tool: 'search_reference',
            query: 'zirconium oxide solubility in PET',
            resultCount: 6,
          },
        },
        {
          seq: 3,
          at: '2026-07-27T10:00:31.000Z',
          kind: 'rejected',
          text: "Output rejected: suggestion references unknown component 'lid'. Retrying, attempt 2 of 3.",
          detail: { attempt: 2, of: 3 },
        },
        {
          seq: 4,
          at: '2026-07-27T10:00:37.000Z',
          kind: 'output',
          text: 'Proposed 11 markers across 3 components — Zr/compound, Y/compound, Ce/organocomplex…',
          detail: { recordId: 'proj-demo|pool' },
        },
        { seq: 5, at: '2026-07-27T10:00:38.000Z', kind: 'outcome', text: 'Done.' },
      ],
    },
  },
];

/** The steps the scripted stream emits, one every `STEP_MS`, into a freshly-opened run. */
const LIVE_STEPS: RunStep[] = [
  {
    seq: 1,
    at: '',
    kind: 'started',
    text: 'Screening 4 candidate substances against 3 target markets.',
  },
  {
    seq: 2,
    at: '',
    kind: 'tool-call',
    text: 'Searched the regulatory corpus for "zirconium dioxide REACH Annex XVII" — 3 hits.',
    detail: { tool: 'search_regulatory', resultCount: 3 },
  },
  {
    seq: 3,
    at: '',
    kind: 'tool-call',
    text: 'Looked up CAS 1314-23-4 in the reference catalog — 2 supplier cards.',
    detail: { tool: 'lookup_catalog', resultCount: 2 },
  },
  {
    seq: 4,
    at: '',
    kind: 'output',
    text: 'Wrote 4 verdicts — 3 pass, 1 flagged for review.',
    detail: { recordId: 'proj-demo|verdicts' },
  },
];

const STEP_MS = 1800;
const encoder = new TextEncoder();

const frame = (event: string, id: string, data: unknown) =>
  encoder.encode(`event: ${event}\nid: ${id}\ndata: ${JSON.stringify(data)}\n\n`);

/**
 * A live run that opens, emits its steps on a timer, then lands. Deliberately timed rather than
 * flushed at once — the dock's expansion, stick-to-bottom and auto-collapse are timing behaviours
 * and a synchronous fixture would not exercise any of them.
 */
export function scriptedStream(stage: string): ReadableStream<Uint8Array> {
  const runId = `run|proj-demo|${stage}|live`;
  let i = 0;
  let timer: ReturnType<typeof setInterval>;

  return new ReadableStream({
    start(controller) {
      const startedAt = new Date().toISOString();
      controller.enqueue(
        frame('entry', 'e2', {
          seq: 2,
          at: startedAt,
          kind: 'run',
          run: {
            runId,
            stage,
            agent: stage,
            subject: null,
            parentRunId: null,
            trigger: 'pipeline',
            startedAt,
            endedAt: null,
            outcome: 'running',
            error: null,
            steps: [],
          },
        }),
      );

      timer = setInterval(() => {
        if (i < LIVE_STEPS.length) {
          const step = { ...LIVE_STEPS[i], at: new Date().toISOString() };
          controller.enqueue(frame('step', `e2.s${step.seq}`, { runId, step }));
          i++;
          return;
        }
        controller.enqueue(
          frame('run', 'e2.r', {
            runId,
            endedAt: new Date().toISOString(),
            outcome: 'done',
            error: null,
          }),
        );
        clearInterval(timer);
        controller.close();
      }, STEP_MS);
    },
    cancel() {
      clearInterval(timer);
    },
  });
}
