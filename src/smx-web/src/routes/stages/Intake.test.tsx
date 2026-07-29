import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Intake } from './Intake';
import type { IntakeBrief as Brief, ProjectPayload, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeBrief: vi.fn(),
  // The screen mounts ProposedPool, which reads the pool on its own. These cases are about the
  // record, so it resolves to the not-yet-run state and renders one honest line.
  getPool: vi.fn().mockResolvedValue(Symbol.for('NotFound')),
}));
import * as api from '../../api/client';

const PAYLOAD: ProjectPayload = {
  components: [
    {
      id: 'bottle',
      material: 'HDPE',
      application: 'packaging',
      markets: ['EU'],
      objective: 'brand',
      batchMassKg: 250,
    },
  ],
  elementPools: [{ component: 'bottle', element: 'Zr', line: 'Kα', status: 'V' }],
  providedCandidates: [],
  clientRestrictedList: ['Pb'],
  measuredBackground: [],
};

const project = (
  over: { intakeStatus?: string; payload?: ProjectPayload } = {},
): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: {
      status: (over.intakeStatus ??
        'awaiting-confirmation') as ProjectSummary['stages'][string]['status'],
      attempts: 0,
    },
    discovery: { status: 'pending', attempts: 0 },
    regulatory: { status: 'pending', attempts: 0 },
    matrix: { status: 'pending', attempts: 0 },
  },
  ...(over.payload ? { payload: over.payload } : {}),
});

const brief = (over: Partial<Brief> = {}): Brief => ({
  projectId: 'proj-1',
  sessionId: 'isx-1',
  summary: 'Acme make a 500 ml PET bottle.',
  components: [
    {
      id: 'lid',
      material: 'PP',
      application: 'food contact',
      markets: ['EU'],
      objective: 'brand',
    },
  ],
  dossier: [],
  attachments: [],
  transcript: [],
  createdAt: '2026-07-22T10:00:00Z',
  ...over,
});

const renderIntake = (p = project()) =>
  render(
    <MemoryRouter>
      <Intake project={p} refreshProject={vi.fn()} />
    </MemoryRouter>,
  );

/**
 * A section, found by its heading rather than by a class.
 *
 * The screen is three sections and this is how the operator navigates them — and how a
 * screen-reader user does, which is why each one is a real <h3> and not a styled <span>.
 */
const section = (name: RegExp) =>
  within(screen.getByRole('heading', { name }).closest('section') as HTMLElement);

/** Open a disclosure by its summary text, the way the operator would. */
const open = async (name: RegExp) => userEvent.click(screen.getByText(name));

beforeEach(() => {
  vi.mocked(api.getIntakeBrief).mockResolvedValue(brief());
});

describe('Intake — the shape of the screen', () => {
  /**
   * The header carries product and client, the stepper carries every stage's state. Repeating
   * either here was the screen's whole opening: a client/product/id table, then four status cards,
   * before a word about the work.
   */
  it('repeats neither the project identity nor the stage states', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    await screen.findByRole('heading', { name: /what we're marking/i });

    expect(screen.queryByText('Acme')).not.toBeInTheDocument();
    expect(screen.queryByText('proj-1')).not.toBeInTheDocument();
    expect(document.querySelectorAll('.stat, .stagecard').length).toBe(0);
    expect(screen.queryByText(/discovery agent/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/matrix assembler/i)).not.toBeInTheDocument();
  });

  it('reads as three sections, in the order the operator needs them', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    const headings = (await screen.findAllByRole('heading', { level: 3 })).map(
      (h) => h.textContent,
    );
    expect(headings).toEqual([
      "What we're marking",
      'The proposed pool',
      "What's still missing",
    ]);
  });

  /**
   * Start Processing is the next-action block's button and NOWHERE else (Law 9 — the agent may
   * create, only the operator may start). A second one on this screen would be a competing primary
   * action, and it is how the press came to be buried three sections down in the first place.
   */
  it('carries no Start control of its own, on any intake status', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    await screen.findByRole('heading', { name: /what we're marking/i });
    expect(screen.queryByRole('button', { name: /start/i })).not.toBeInTheDocument();
  });

  /**
   * The screen used to be two zones: a real record band and a fixture band behind a mock badge.
   * The fixture band is gone, and nothing here is fabricated.
   */
  it('carries no mock provenance marker', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    await screen.findByRole('heading', { name: /what we're marking/i });
    expect(document.querySelectorAll('[data-provenance="mock"]').length).toBe(0);
    expect(screen.queryByText(/Mock data/i)).not.toBeInTheDocument();
  });

  /**
   * No `spec §`, no endpoint names, no record tokens. The operator reads a product brief, not the
   * backend's vocabulary.
   */
  it('speaks in domain words rather than record tokens', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    await screen.findByRole('heading', { name: /what we're marking/i });
    expect(screen.queryByText(/spec §/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting-physics/)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting-confirmation/)).not.toBeInTheDocument();
  });
});

describe("Intake — what we're marking", () => {
  it('renders what the operator actually submitted, from the record', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    const marking = section(/what we're marking/i);
    expect(marking.getByText('HDPE')).toBeInTheDocument();
    expect(marking.getByText('packaging')).toBeInTheDocument();
    expect(marking.getByText('250 kg')).toBeInTheDocument();
    expect(marking.getByText('Pb')).toBeInTheDocument();
    await screen.findByText(/500 ml PET bottle/);
  });

  /**
   * ppm is mg/kg, so an absent batch mass yields no order amount at all. Absent is NOT zero, and a
   * rendered 0 is a number a human could act on — OrderAmount.Compute exists to refuse exactly this.
   */
  it('says a missing batch mass is not given, and never renders it as zero', async () => {
    renderIntake(
      project({
        payload: { ...PAYLOAD, components: [{ ...PAYLOAD.components[0], batchMassKg: undefined }] },
      }),
    );
    const marking = section(/what we're marking/i);
    expect(marking.getByText('not given')).toBeInTheDocument();
    expect(marking.queryByText('0 kg')).not.toBeInTheDocument();
    expect(marking.queryByText('0')).not.toBeInTheDocument();
  });

  /**
   * A project whose payload has not loaded still has a subject: the interview said what the
   * components are. Rendering an empty section instead would make the screen silent about the one
   * thing it is for.
   */
  it('falls back to the brief’s components when the record carries no payload', async () => {
    renderIntake(project());
    expect(await screen.findByText('lid')).toBeInTheDocument();
    expect(screen.getByText('PP')).toBeInTheDocument();
  });

  /** Law 4: nothing the agent wrote is hand-editable. A stray input would be a silent edit. */
  it('offers no way to edit anything', async () => {
    const { container } = renderIntake(project({ payload: PAYLOAD }));
    await screen.findByRole('heading', { name: /what we're marking/i });
    expect(container.querySelectorAll('input, textarea, select')).toHaveLength(0);
    expect(container.querySelectorAll('[contenteditable="true"]')).toHaveLength(0);
  });
});

describe('Intake — the proposed pool', () => {
  /**
   * The pools the physicist already recorded are prior input, not the agent's proposal, so they sit
   * behind a disclosure rather than competing with it — but they are one click away, never further.
   */
  it('keeps the recorded element pools one interaction away, with their signal note', async () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          elementPools: [
            { component: 'bottle', element: 'Zr', line: 'Kα', status: 'L', signalNote: 'overlaps Sr' },
          ],
        },
      }),
    );
    await open(/pools already on the record/i);
    const pool = section(/the proposed pool/i);
    expect(pool.getByText('Zr')).toBeInTheDocument();
    expect(pool.getByText('L')).toBeInTheDocument();
    expect(pool.getByText('overlaps Sr')).toBeInTheDocument();
  });
});

describe("Intake — what's still missing", () => {
  /**
   * An empty background with no device is not a blank screen: it is the exact precondition Dosing
   * parks on. The screen reports it as a fact about the record — one line, naming what is absent —
   * without claiming intake itself is parked, because intake has no park state.
   */
  it('names both absent physics inputs in one line', async () => {
    renderIntake(project({ payload: PAYLOAD }));
    const missing = section(/what's still missing/i);
    expect(
      missing.getByText(/Not on the record yet: an XRF background and an XRF device with its LODs/i),
    ).toBeInTheDocument();
    expect(screen.queryByText(/parks here in the real system/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting client samples/i)).not.toBeInTheDocument();
  });

  /**
   * The half-entered record. DetectionFloor needs the background AND the device's LOD together, so
   * a background with no device still parks Dosing. Showing the one that arrived without saying the
   * floor is still uncomputable would read as "physics: done".
   */
  it('still reports incompleteness when only one of the two inputs is on file', async () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
        },
      }),
    );
    const missing = section(/what's still missing/i);
    expect(missing.getByText(/Not on the record yet: an XRF device with its LODs/i)).toBeInTheDocument();
    expect(missing.queryByText(/an XRF background and/i)).not.toBeInTheDocument();
  });

  /**
   * THE asymmetry: absence we can prove from the record, sufficiency we cannot. Both inputs present
   * still does not mean the floor computes — a unit mismatch or a missing per-element LOD each
   * refuse further down — so this screen may never say the physics is done.
   */
  it('never claims the physics is sufficient, even with both inputs on the record', async () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
          device: { model: 'Olympus Vanta M', lods: [{ element: 'Zr', lod: 1.5, unit: 'ppm' }] },
        },
      }),
    );
    const missing = section(/what's still missing/i);
    expect(missing.getByText(/settled at dosing, not here/i)).toBeInTheDocument();
    expect(missing.queryByText(/Not on the record yet/i)).not.toBeInTheDocument();
  });

  it('renders the measurement with its unit, and the device with its LODs', async () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
          device: { model: 'Olympus Vanta M', lods: [{ element: 'Zr', lod: 1.5, unit: 'ppm' }] },
        },
      }),
    );
    await open(/what the physicist has given so far/i);
    const missing = section(/what's still missing/i);
    // The unit travels with the level — a bare "4" is the confusion DetectionFloor refuses to make.
    expect(missing.getByText('4 ppm')).toBeInTheDocument();
    expect(missing.getByText('Olympus Vanta M')).toBeInTheDocument();
    expect(missing.getByText(/Zr LOD 1.5 ppm/)).toBeInTheDocument();
  });

  /** A stated gap travels INTO the analysis. It is what the operator is starting the run on. */
  it('counts the questions the interview left open', async () => {
    vi.mocked(api.getIntakeBrief).mockResolvedValue(
      brief({
        dossier: [
          { questionId: 'qc-tests', state: 'unknown', answer: 'no reply', provenance: 'operator', recordedAt: '…' },
        ],
      }),
    );
    renderIntake(project({ payload: PAYLOAD }));
    expect(await screen.findByText(/1 question goes into the analysis as an unknown/i)).toBeInTheDocument();
  });

  /**
   * NotFound is the NORMAL state for every project created through the form/API. Nothing was
   * fabricated and nothing failed — so the screen states the fact instead of rendering a void.
   */
  it('says a form-made project has no brief, rather than showing an empty panel', async () => {
    vi.mocked(api.getIntakeBrief).mockResolvedValue(api.NotFound);
    renderIntake(project({ payload: PAYLOAD }));
    expect(await screen.findByText(/created through the form/i)).toBeInTheDocument();
  });

  it('surfaces a failed brief read instead of pretending there was no brief', async () => {
    vi.mocked(api.getIntakeBrief).mockRejectedValue(new Error('503 Service Unavailable'));
    renderIntake(project({ payload: PAYLOAD }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/503 Service Unavailable/);
  });
});
