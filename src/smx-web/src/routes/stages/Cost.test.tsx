import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Cost } from './Cost';
import type { CostDoc, MsdsEntry, PriceQuote, ProjectSummary, SupplierAudit } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getCost: vi.fn(),
  getMsdsRegistry: vi.fn(),
}));
import * as api from '../../api/client';

const quote = (over: Partial<PriceQuote> = {}): PriceQuote => ({
  usdPerGram: 12.5,
  currency: 'USD',
  supplier: 'Fisher Scientific',
  pack: '25 g',
  citation: {
    source: 'supplier catalog',
    reference: 'FIS-AC201',
    retrievedAt: '2026-07-01T00:00:00Z',
  },
  ...over,
});

const audit = (over: Partial<SupplierAudit> = {}): SupplierAudit => ({
  cas: '1314-23-4',
  element: 'Zr',
  suppliers: ['Fisher Scientific'],
  bestQuote: quote(),
  priceNote: 'quoted from the 25 g listing',
  risks: [],
  ...over,
});

const costDoc = (substances: unknown[]): CostDoc =>
  ({
    id: 'cost-proj-1',
    projectId: 'proj-1',
    type: 'cost',
    substances,
    generatedAt: '2026-07-20T09:00:00Z',
  }) as unknown as CostDoc;

const sheet = (over: Partial<MsdsEntry> = {}): MsdsEntry => ({
  id: 'msds-1',
  cas: '1314-23-4',
  supplier: 'Fisher Scientific',
  version: '3.0',
  date: '2026-01-04',
  // The order predicate is the SHEET's existence, not a signature over it (design 2026-07-29, D8/D9).
  documentId: 'sds|1314-23-4',
  linkedProjects: [],
  ...over,
});

const project = (): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    cost: { status: 'done', attempts: 1 },
  },
});

const renderCost = () =>
  render(
    <MemoryRouter>
      <Cost project={project()} refreshProject={vi.fn()} />
    </MemoryRouter>,
  );

/** A section, found by its heading — the way the operator (and a screen reader) navigates them. */
const section = (name: RegExp) =>
  within(screen.getByRole('heading', { name }).closest('section') as HTMLElement);

/** Wait for the async reads to land. Both sections are up once the audit renders. */
const ready = () => screen.findByRole('heading', { name: /what each substance costs/i });

/** One stat tile, by its label — the figures repeat elsewhere on the screen, the tiles do not. */
const tile = (container: HTMLElement, label: string) =>
  [...container.querySelectorAll('.stat')].find((n) =>
    n.querySelector('.stat__label')?.textContent?.includes(label),
  ) as HTMLElement;

beforeEach(() => {
  vi.mocked(api.getCost).mockResolvedValue(costDoc([audit()]));
  vi.mocked(api.getMsdsRegistry).mockResolvedValue([sheet()]);
});

describe('Cost — the shape of the screen', () => {
  it('reads as two sections: what can be ordered, then what it costs', async () => {
    renderCost();
    await ready();
    const headings = screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent);
    expect(headings).toEqual(['What can be ordered', 'What each substance costs']);
  });

  /**
   * Two tiles, not four. "Substances audited" is already carried by "x of y", and "single-source" is
   * a property of a row that nobody acts on in aggregate — neither changes what the operator does.
   * The two that do: chase a price, or chase a safety data sheet.
   */
  it('carries exactly the two figures that change what the operator does', async () => {
    const { container } = renderCost();
    await ready();
    const labels = [...container.querySelectorAll('.stat__label')].map((n) => n.textContent);
    expect(labels).toEqual(['Priced', 'Not orderable']);
    expect(screen.queryByText(/substances audited/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/single-source/i)).not.toBeInTheDocument();
  });

  /** The header and stepper say the project and the stage. Neither is repeated here. */
  it('repeats neither the project identity nor the stage status', async () => {
    const { container } = renderCost();
    await ready();
    expect(container.querySelectorAll('.cap, .stagecard')).toHaveLength(0);
    expect(screen.queryByText('proj-1')).not.toBeInTheDocument();
  });

  it('speaks in domain words rather than record tokens', async () => {
    renderCost();
    await ready();
    expect(screen.queryByText(/spec §/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/bestQuote/)).not.toBeInTheDocument();
  });

  /**
   * The screen used to claim "there is no agent here to ask". It is false — one chat agent serves
   * every stage, cost included. What is true is narrower, and the difference matters: "nobody to
   * ask" sends the operator away from the one place they can question a figure.
   */
  it('never claims there is no agent to ask, and says what is actually true instead', async () => {
    renderCost();
    await ready();
    expect(screen.queryByText(/no agent here to ask/i)).not.toBeInTheDocument();
    expect(screen.getByText(/no analysis agent authored these figures/i)).toBeInTheDocument();
  });

  it('says the same true thing when nothing has been costed yet', async () => {
    vi.mocked(api.getCost).mockResolvedValue(api.NotFound);
    renderCost();
    expect(await screen.findByText(/nothing costed yet/i)).toBeInTheDocument();
    expect(screen.queryByText(/no agent here to ask/i)).not.toBeInTheDocument();
    expect(screen.getByText(/deterministic catalog lookup/i)).toBeInTheDocument();
  });
});

describe('Cost — prices', () => {
  it('renders a quote with its currency, unit, supplier, pack and listing', async () => {
    renderCost();
    await ready();
    const costs = section(/what each substance costs/i);
    expect(costs.getByText('USD 12.50')).toBeInTheDocument();
    expect(costs.getByText('per gram')).toBeInTheDocument();
    expect(costs.getByText(/Fisher Scientific ·/)).toBeInTheDocument();
    expect(costs.getByText('25 g')).toBeInTheDocument();
    expect(costs.getByText('FIS-AC201')).toBeInTheDocument();
  });

  /**
   * THE rule of this screen. Procurement cuts a purchase order against this figure, so an absent
   * price renders as an absence with the record's own words — never as a zero, a dash that reads
   * like one, or an average of the substances around it.
   */
  it('renders an absent price as an absence with the record’s note, never as zero', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([audit({ bestQuote: undefined, priceNote: 'price is free text on this listing' })]),
    );
    const { container } = renderCost();
    await ready();
    const costs = section(/what each substance costs/i);
    expect(costs.getByText('No price')).toBeInTheDocument();
    expect(costs.getByText('price is free text on this listing')).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/USD\s*0/);
    expect(container.textContent).not.toMatch(/0\.00/);
  });

  /**
   * A quote on the record whose figure is not a finite number. It must not fall through to the
   * absence path (whose note would explain the wrong thing) and it must certainly not be formatted:
   * `Intl` renders a non-number as "NaN" and a missing one as "0", and both would be a fabricated
   * price on the line that gets ordered.
   */
  it('never formats an unreadable quote into a number', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([audit({ bestQuote: { ...quote(), usdPerGram: null as unknown as number } })]),
    );
    const { container } = renderCost();
    await ready();
    expect(screen.getByText(/figure could not be read/i)).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/NaN/);
    expect(container.textContent).not.toMatch(/USD\s*0/);
    expect(container.textContent).not.toMatch(/0\.00/);
  });

  /** The tile counts real quotes. An unreadable one is not a price and must not inflate it. */
  it('counts only substances that actually have a readable price', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([
        audit(),
        audit({ cas: '1314-13-2', element: 'Zn', bestQuote: undefined }),
        audit({
          cas: '1309-37-1',
          element: 'Fe',
          bestQuote: { ...quote(), usdPerGram: Number.NaN },
        }),
      ]),
    );
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([
      sheet(),
      sheet({ id: 'm2', cas: '1314-13-2' }),
      sheet({ id: 'm3', cas: '1309-37-1' }),
    ]);
    renderCost();
    await ready();
    expect(screen.getByText('1 of 3')).toBeInTheDocument();
    expect(screen.getByText(/2 with no price on the record/i)).toBeInTheDocument();
  });

  /** Names only — the catalog holds no per-supplier price and no lead time, so neither is drawn. */
  it('lists suppliers without inventing a comparison or a lead time', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([audit({ suppliers: ['Fisher Scientific', 'Alfa Aesar'] })]),
    );
    const { container } = renderCost();
    await ready();
    const costs = section(/what each substance costs/i);
    expect(costs.getByText(/Suppliers on file/i)).toBeInTheDocument();
    expect(costs.getByText('Alfa Aesar')).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/lead time/i);
  });
});

describe('Cost — the safety-sheet blocker', () => {
  /**
   * The loudest element on the screen, because it is the one fact here that stops a purchase order
   * outright — a missing price only delays one.
   */
  it('states the blocker once, over every substance it applies to, and names why', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([audit(), audit({ cas: '1314-13-2', element: 'Zn' })]),
    );
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([
      // A registry row with no sheet behind it — governance-only, and refused exactly as if absent.
      sheet({ cas: '1314-13-2', documentId: null }),
    ]);
    const { container } = renderCost();
    await ready();

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent(/2 substances cannot be ordered/i);
    // The reason is per substance, and the two reasons are different claims: a substance with no
    // registry row at all has to have a sheet OBTAINED; one whose row carries no document has to
    // have a sheet LINKED. Collapsing them would send the operator to the wrong place.
    expect(within(alert).getByText(/no sheet is on file/i)).toBeInTheDocument();
    expect(within(alert).getByText(/registry entry has no sheet behind it/i)).toBeInTheDocument();
    expect(within(alert).getByText('1314-23-4')).toBeInTheDocument();
    expect(within(alert).getByRole('link', { name: /MSDS registry/i })).toBeInTheDocument();
    expect(tile(container, 'Not orderable').querySelector('.stat__value')).toHaveTextContent('2');
  });

  /** Singular reads as singular — "1 substances" is the tell of a screen nobody read. */
  it('counts one blocked substance as one', async () => {
    vi.mocked(api.getMsdsRegistry).mockResolvedValue([]);
    renderCost();
    await ready();
    expect(screen.getByRole('alert')).toHaveTextContent(/1 substance cannot be ordered/i);
  });

  it('says plainly when nothing is blocked, rather than showing an empty region', async () => {
    renderCost();
    await ready();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(
      screen.getByText(/every substance below has a safety data sheet on file/i),
    ).toBeInTheDocument();
  });

  /**
   * "Could not check" is NOT "cleared". Telling the operator a substance has no safety sheet when we
   * merely could not read the registry is a fabricated claim about an absence, on the screen where
   * the order is decided. So the count goes absent rather than to zero, and no substance is accused
   * of missing a sheet.
   */
  it('reports an unreadable registry as unknown, never as cleared', async () => {
    vi.mocked(api.getMsdsRegistry).mockRejectedValue(new Error('503'));
    const { container } = renderCost();
    await ready();

    expect(screen.getByText(/unknown, not cleared/i)).toBeInTheDocument();
    expect(screen.queryByText(/cannot be ordered/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/no MSDS on file/i)).not.toBeInTheDocument();
    expect(
      screen.queryByText(/every substance below has a safety data sheet on file/i),
    ).not.toBeInTheDocument();

    // The tile is the absence, not a zero: "0 not orderable" is a clearance.
    const blockedTile = tile(container, 'Not orderable');
    expect(blockedTile.className).toContain('stat--absent');
    expect(blockedTile.textContent).not.toMatch(/\b0\b/);
    // And the row itself says its status was not checked, for an operator who scrolled past the top.
    expect(screen.getByText(/MSDS status not checked/i)).toBeInTheDocument();
  });

  /**
   * A 200 whose body is not a list is a FAILED read, not an empty registry. Coercing it to `[]`
   * would report every substance as having no sheet on file — the same fabricated absence, arriving
   * through a shape drift instead of an error.
   */
  it('treats a registry that is not a list as unreadable, not as an empty registry', async () => {
    vi.mocked(api.getMsdsRegistry).mockResolvedValue({ items: [] } as unknown as MsdsEntry[]);
    renderCost();
    await ready();
    expect(screen.getByText(/unknown, not cleared/i)).toBeInTheDocument();
    expect(screen.queryByText(/no sheet is on file/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/cannot be ordered/i)).not.toBeInTheDocument();
  });
});

describe('Cost — guarding its own payload', () => {
  /**
   * `client.ts` casts every response with `as` and validates nothing, so `substances` is whatever the
   * wire carried. `.filter` on a non-array threw a TypeError that took the whole screen down. The
   * error boundary is a backstop; degrading inside the region is the plan.
   */
  it('degrades to a stated failure when the audit is not a list, instead of throwing', async () => {
    vi.mocked(api.getCost).mockResolvedValue(costDoc(undefined as unknown as unknown[]));
    const { container } = renderCost();
    await ready();
    expect(screen.getByText(/shape this screen cannot read/i)).toBeInTheDocument();
    // And it does not report the failure as a clearance: no "0 not orderable", no "all priced".
    expect(container.querySelectorAll('.stat--absent')).toHaveLength(2);
    expect(container.textContent).not.toMatch(/every substance has a price/i);
  });

  it('drops entries it cannot identify, and says how many it dropped', async () => {
    vi.mocked(api.getCost).mockResolvedValue(costDoc([audit(), null, { element: 'Zn' }]));
    renderCost();
    await ready();
    expect(screen.getByText(/2 entries in this audit could not be read/i)).toBeInTheDocument();
    expect(screen.getByText('1 of 1')).toBeInTheDocument();
  });

  it('survives a row whose risks and suppliers are not lists', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([
        audit({
          risks: 'single-source' as unknown as string[],
          suppliers: null as unknown as string[],
        }),
      ]),
    );
    renderCost();
    await ready();
    expect(screen.getByText(/no supplier on file/i)).toBeInTheDocument();
  });

  /** A quote with no usable citation must not take the price down with it. */
  it('renders a price whose citation is malformed, without the citation', async () => {
    vi.mocked(api.getCost).mockResolvedValue(
      costDoc([audit({ bestQuote: { ...quote(), citation: undefined as unknown as never } })]),
    );
    renderCost();
    await ready();
    expect(screen.getByText('USD 12.50')).toBeInTheDocument();
    expect(screen.queryByText('FIS-AC201')).not.toBeInTheDocument();
  });

  it('surfaces a failed audit read rather than an empty screen', async () => {
    vi.mocked(api.getCost).mockRejectedValue(new Error('503 Service Unavailable'));
    renderCost();
    expect(await screen.findByRole('alert')).toHaveTextContent(/503 Service Unavailable/);
  });
});
