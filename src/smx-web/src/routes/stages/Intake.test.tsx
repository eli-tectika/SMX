import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ProjectPayload, ProjectSummary } from '../../api/types';
import { Intake } from './Intake';

const STAGES: ProjectSummary['stages'] = {
  intake: { status: 'done', attempts: 1 },
  discovery: { status: 'pending', attempts: 0 },
  regulatory: { status: 'pending', attempts: 0 },
  matrix: { status: 'pending', attempts: 0 },
};

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

const project = (payload?: ProjectPayload): ProjectSummary => ({
  projectId: 'proj-test',
  client: 'MUFE',
  product: 'clear bottle',
  stages: STAGES,
  ...(payload ? { payload } : {}),
});

const intake = (payload?: ProjectPayload) => render(<Intake project={project(payload)} />);

/** The record zone is the first `.screen`; the mock zone is the second. */
const realZone = () => document.querySelectorAll('.screen')[0] as HTMLElement;

describe('Intake — the record zone', () => {
  it('renders what the operator actually submitted, from the projection', () => {
    intake(PAYLOAD);
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
    intake(PAYLOAD);
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
    intake({ ...PAYLOAD, components: [{ ...PAYLOAD.components[0], batchMassKg: undefined }] });
    const real = within(realZone());
    expect(real.getByText('not given')).toBeInTheDocument();
    expect(real.queryByText('0 kg')).not.toBeInTheDocument();
  });

  /**
   * This screen once carried a ParkSlot claiming "this stage parks here in the real system —
   * awaiting client samples". Every word was fiction: the dispatcher drives intake
   * pending → running → done | needs-review | failed (StageDispatcher.cs:60-77) and can never park
   * it, and no `awaiting client samples` token exists anywhere in the backend. It sat in the REAL
   * zone, which is the worst place to invent a capability.
   */
  it('never claims intake parks, because intake has no park state', () => {
    intake(PAYLOAD);
    expect(screen.queryByText(/parks here in the real system/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/awaiting client samples/i)).not.toBeInTheDocument();
  });
});

describe('Intake — the physics inputs', () => {
  /**
   * An empty background with no device is not a blank screen: it is the exact precondition Dosing
   * parks on. The screen reports it as a fact about the record, and names the state the operator
   * will see downstream — without claiming intake itself is parked.
   */
  it('reports absent physics as a fact, and names the park it causes downstream', () => {
    intake(PAYLOAD);
    const real = within(realZone());
    expect(real.getByText(/No XRF background and no device are on the record yet/i)).toBeInTheDocument();
    expect(real.getByText('awaiting-physics')).toBeInTheDocument();
  });

  it('renders the background with its unit, and the device with its LODs', () => {
    intake({
      ...PAYLOAD,
      measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
      device: { model: 'Olympus Vanta M', lods: [{ element: 'Zr', lod: 1.5, unit: 'ppm' }] },
    });
    const real = within(realZone());
    // The unit travels with the level — a bare "4" is the confusion DetectionFloor refuses to make.
    expect(real.getByText('4 ppm')).toBeInTheDocument();
    expect(real.getByText('Olympus Vanta M')).toBeInTheDocument();
    expect(real.getByText(/Zr LOD 1.5 ppm/)).toBeInTheDocument();
    expect(real.queryByText(/will park on/i)).not.toBeInTheDocument();
  });

  /**
   * The half-entered record. DetectionFloor needs the background AND the device's LOD together
   * (DetectionFloor.cs:40,49), so a background with no device still parks Dosing. Showing the one
   * that arrived without saying the floor is still uncomputable would read as "physics: done".
   */
  it('still reports incompleteness when only one of the two inputs is on file', () => {
    intake({
      ...PAYLOAD,
      measuredBackground: [{ component: 'bottle', element: 'Zr', level: 4, unit: 'ppm' }],
    });
    const real = within(realZone());
    expect(real.getByText('4 ppm')).toBeInTheDocument();
    expect(real.getByText(/no XRF device is/i)).toBeInTheDocument();
    expect(real.getByText('awaiting-physics')).toBeInTheDocument();
  });
});
