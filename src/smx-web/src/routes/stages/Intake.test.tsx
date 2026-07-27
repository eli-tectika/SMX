import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Intake } from './Intake';
import type { IntakeBrief as Brief, ProjectPayload, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeBrief: vi.fn(),
  startProject: vi.fn(),
  // The screen now mounts ProposedPool, which reads the pool on its own. These cases are about the
  // record zone, so it resolves to the not-yet-run state and renders one honest line.
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
      status: (over.intakeStatus ?? 'awaiting-confirmation') as ProjectSummary['stages'][string]['status'],
      attempts: 0,
    },
    discovery: { status: 'pending', attempts: 0 },
    regulatory: { status: 'pending', attempts: 0 },
    matrix: { status: 'pending', attempts: 0 },
  },
  ...(over.payload ? { payload: over.payload } : {}),
});

const brief: Brief = {
  projectId: 'proj-1',
  sessionId: 'isx-1',
  summary: 'Acme make a 500 ml PET bottle.',
  components: [
    { id: 'bottle', material: 'PET', application: 'food contact', markets: ['EU'], objective: 'brand' },
  ],
  dossier: [],
  attachments: [],
  transcript: [],
  createdAt: '2026-07-22T10:00:00Z',
};

const renderIntake = (p = project(), refreshProject = vi.fn()) => {
  render(
    <MemoryRouter>
      <Intake project={p} refreshProject={refreshProject} />
    </MemoryRouter>,
  );
  return refreshProject;
};

/** The record zone is the first `.screen`; the mock zone is the second. */
const realZone = () => document.querySelectorAll('.screen')[0] as HTMLElement;

beforeEach(() => {
  vi.mocked(api.getIntakeBrief).mockResolvedValue(brief);
  vi.mocked(api.startProject).mockResolvedValue({ status: 'pending' });
});

describe('the intake stage screen — the interview brief', () => {
  it('says a form-made project has no brief, rather than showing an empty panel', async () => {
    // NotFound is the NORMAL state for every project created through the form/API. Nothing was
    // fabricated and nothing failed — so the screen states the fact instead of rendering a void.
    vi.mocked(api.getIntakeBrief).mockResolvedValue(api.NotFound);
    renderIntake();

    expect(await screen.findByText(/created through the form/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /start processing/i })).not.toBeInTheDocument();
  });

  it('renders the brief and starts the analysis on the operator’s press', async () => {
    const refreshProject = renderIntake();

    await userEvent.click(await screen.findByRole('button', { name: /start processing/i }));

    expect(api.startProject).toHaveBeenCalledWith('proj-1');
    // The spine has to catch up with the record the press just changed.
    await waitFor(() => expect(refreshProject).toHaveBeenCalled());
  });

  it('shows an error when start finds no such project, instead of failing silently', async () => {
    vi.mocked(api.startProject).mockResolvedValue(api.NotFound);
    const refreshProject = renderIntake();

    await userEvent.click(await screen.findByRole('button', { name: /start processing/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/no project with id proj-1/i);
    expect(refreshProject).not.toHaveBeenCalled();
  });

  it('does not offer Start once the stage has left awaiting-confirmation', async () => {
    renderIntake(project({ intakeStatus: 'running' }));

    expect(await screen.findByText(/500 ml PET bottle/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /start processing/i })).not.toBeInTheDocument();
  });
});

describe('Intake — the record zone', () => {
  // No interview brief in these, so the interview-brief `.screen` never renders and the record zone
  // (the payload read-back) stays the FIRST `.screen` — which is what realZone() indexes.
  beforeEach(() => vi.mocked(api.getIntakeBrief).mockResolvedValue(api.NotFound));

  it('renders what the operator actually submitted, from the projection', () => {
    renderIntake(project({ payload: PAYLOAD }));
    const real = within(realZone());
    expect(real.getByText('HDPE')).toBeInTheDocument();
    expect(real.getByText('packaging')).toBeInTheDocument();
    expect(real.getByText('250 kg')).toBeInTheDocument();
    expect(real.getByText('Zr')).toBeInTheDocument();
    expect(real.getByText('Pb')).toBeInTheDocument();
  });

  /**
   * The submitted inputs are the operator's OWN, not an agent's, so they belong in the record zone
   * and carry no badge. The bar this guards is the opposite one: they must never drift into the
   * mock surface, where a real value would print under "MOCK DATA — NOT FOR REGULATORY USE".
   */
  it('keeps the submitted inputs out of the mock-provenance surface', () => {
    renderIntake(project({ payload: PAYLOAD }));
    expect(realZone()).not.toHaveAttribute('data-provenance', 'mock');
    const mock = document.querySelector('.screen[data-provenance="mock"]') as HTMLElement;
    expect(mock).toBeInTheDocument();
    expect(within(mock).queryByText('HDPE')).not.toBeInTheDocument();
  });

  /**
   * ppm is mg/kg, so an absent batch mass yields no order amount at all. Absent is NOT zero, and a
   * rendered 0 is a number a human could act on — OrderAmount.Compute exists to refuse exactly this.
   */
  it('says a missing batch mass is not given, and never renders it as zero', () => {
    renderIntake(
      project({ payload: { ...PAYLOAD, components: [{ ...PAYLOAD.components[0], batchMassKg: undefined }] } }),
    );
    const real = within(realZone());
    expect(real.getByText('not given')).toBeInTheDocument();
    expect(real.queryByText('0 kg')).not.toBeInTheDocument();
  });

  /**
   * This screen once carried a ParkSlot claiming "this stage parks here in the real system —
   * awaiting client samples". Every word was fiction: the dispatcher drives intake
   * pending → running → done | needs-review | failed and can never park it, and no
   * `awaiting client samples` token exists anywhere in the backend.
   */
  it('never claims intake parks, because intake has no park state', () => {
    renderIntake(project({ payload: PAYLOAD }));
    expect(screen.queryByText(/parks here in the real system/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting client samples/i)).not.toBeInTheDocument();
  });
});

describe('Intake — the physics inputs', () => {
  beforeEach(() => vi.mocked(api.getIntakeBrief).mockResolvedValue(api.NotFound));

  /**
   * An empty background with no device is not a blank screen: it is the exact precondition Dosing
   * parks on. The screen reports it as a fact about the record, and names the state the operator
   * will see downstream — without claiming intake itself is parked.
   */
  it('reports absent physics as a fact, and names the park it causes downstream', () => {
    renderIntake(project({ payload: PAYLOAD }));
    const real = within(realZone());
    expect(real.getByText(/No XRF background and no device are on the record yet/i)).toBeInTheDocument();
    expect(real.getByText('awaiting-physics')).toBeInTheDocument();
  });

  it('renders the background with its unit, and the device with its LODs', () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
          device: { model: 'Olympus Vanta M', lods: [{ element: 'Zr', lod: 1.5, unit: 'ppm' }] },
        },
      }),
    );
    const real = within(realZone());
    // The unit travels with the level — a bare "4" is the confusion DetectionFloor refuses to make.
    expect(real.getByText('4 ppm')).toBeInTheDocument();
    expect(real.getByText('Olympus Vanta M')).toBeInTheDocument();
    expect(real.getByText(/Zr LOD 1.5 ppm/)).toBeInTheDocument();
    expect(real.queryByText(/will park on/i)).not.toBeInTheDocument();
  });

  /**
   * The half-entered record. DetectionFloor needs the background AND the device's LOD together, so a
   * background with no device still parks Dosing. Showing the one that arrived without saying the
   * floor is still uncomputable would read as "physics: done".
   */
  it('still reports incompleteness when only one of the two inputs is on file', () => {
    renderIntake(
      project({
        payload: {
          ...PAYLOAD,
          measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
        },
      }),
    );
    const real = within(realZone());
    expect(real.getByText('4 ppm')).toBeInTheDocument();
    expect(real.getByText(/no XRF device is/i)).toBeInTheDocument();
    expect(real.getByText('awaiting-physics')).toBeInTheDocument();
  });
});
