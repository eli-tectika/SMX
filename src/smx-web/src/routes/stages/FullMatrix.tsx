import { matrixXlsxUrl } from '../../api/client';
import type { DiscoveryCells, DosingCells, OutcomeCells, RegulatoryCells } from '../../api/types';
import { VERDICT_DIMENSIONS, VERDICT_SEVERITY } from '../../api/types';
import { Loading } from '../../components/Loading';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { verdictClass, verdictGlyph } from '../../domain/matrix';
import { fmtPpm } from '../../domain/dosing';
import { Data } from '../../components/ui/Data';
import {
  AbsentCells,
  AmountValue,
  AvailabilityValue,
  BoundValue,
  DroppedRows,
  IdentityCell,
  TableError,
  byComponentRows,
  useProjectTable,
  type ReadRow,
} from './projectTable';
import type { ScreenProps } from '../ProjectLayout';
import type { VerdictStatus } from '../../api/types';

/** Column counts per group, for the absent-cells span. Keep in step with the headers below. */
const SPAN = { discovery: 4, regulatory: 7, dosing: 4, outcome: 2 } as const;
const TOTAL_COLUMNS = 1 + SPAN.discovery + SPAN.regulatory + SPAN.dosing + SPAN.outcome;

const asVerdict = (v: unknown): VerdictStatus =>
  VERDICT_SEVERITY.includes(v as VerdictStatus) ? (v as VerdictStatus) : 'NeedsReview';

/**
 * The full matrix — every column group of the one project table, in one sheet.
 *
 * This is the artifact a customer would be forwarded, and it is the strongest argument for the whole
 * idea (spec §5.3): the record from Discovery onward is keyed on (component, CAS), so it has always
 * BEEN one wide table. It was rendered as five screens that each fetched a slice.
 *
 * It also TRANSPOSES what the old matrix drew. `MatrixDoc` put substances down the side and components
 * ACROSS THE TOP, which works only while a cell holds one glyph. With five columns per phase, components
 * cannot stay columns — they become row groups.
 *
 * Two rendering laws hold this screen together:
 *
 *  1. **The page body never scrolls sideways.** The table is far wider than the artifact column, and the
 *     scroll lives INSIDE `.mxscroll__pane`. `.mxscroll` carries `min-width: 0` for the reason spelled
 *     out in craft.css: without it the grid track sizes itself to the table and the column overflows
 *     instead of the pane.
 *  2. **Identity is frozen.** `[data-rowhead]` is `position: sticky; left: 0`, so element / form / CAS
 *     stay on screen at every horizontal scroll position. They are ONE cell rather than three, because a
 *     second sticky column needs a measured left offset and a width guessed here is exactly the class of
 *     bug the type-floor pass turned up — two fixed boxes that could no longer hold their contents, with
 *     no test to notice.
 *
 * And the invariant that outranks both: a dropped row spans its unreached columns with an explicit
 * statement of where it stopped. Blank cells would read as "not done yet" on the one screen where the
 * whole journey is visible at once.
 */
export function FullMatrix({ project }: ScreenProps) {
  // Any stage moving can change any group, so the clock is the whole stage map rather than one status.
  const clock = Object.values(project.stages)
    .map((s) => s.status)
    .join('|');
  const { state } = useProjectTable(project.projectId, clock);

  if (state.kind === 'loading') return <Loading what="the project table" />;

  return (
    <section className="screen">
      <SectionHeader
        title="Full matrix"
        headingLevel={3}
        hint="every phase's columns, one row per substance and component"
        actions={
          /* The export reads the SAME projection this table does, which is the point of doing the join
             server-side: the sheet a customer is forwarded and the screen an operator signs against
             cannot disagree. */
          <a className="btn" href={matrixXlsxUrl(project.projectId)} download>
            <i className="ti ti-download" aria-hidden="true" /> .xlsx
          </a>
        }
      />

      {state.kind === 'error' && <TableError message={state.message} />}

      {state.kind === 'ready' && (
        <>
          <DroppedRows n={state.read.dropped} />

          {state.read.rows.length === 0 ? (
            <EmptyState
              icon="ti-table-off"
              title="The table has no rows."
              body="Discovery has produced no candidates for this project, so there is nothing keyed on (component, CAS) to show."
            />
          ) : (
            <div className="mxscroll">
              <div className="mxscroll__count small secondary">
                {state.read.rows.length} row{state.read.rows.length === 1 ? '' : 's'} ·{' '}
                {byComponentRows(state.read.rows).length} component
                {byComponentRows(state.read.rows).length === 1 ? '' : 's'} · scrolls sideways inside this
                pane
              </div>
              <div className="mxscroll__pane">
                <table className="mx mx--sticky">
                  <caption className="sr-only">
                    Every substance in every component, with what each phase found. A row that stopped
                    spans its remaining columns with the phase it stopped at and why.
                  </caption>
                  <thead>
                    <tr>
                      <th scope="col" data-rowhead>
                        Substance
                      </th>
                      <th scope="col">Tier</th>
                      <th scope="col">Preferred</th>
                      <th scope="col">Rationale</th>
                      <th scope="col">Sources</th>
                      <th scope="col">Verdict</th>
                      {VERDICT_DIMENSIONS.map((d) => (
                        <th key={d} scope="col" style={{ textAlign: 'center' }}>
                          {d}
                        </th>
                      ))}
                      {/* Never one column. The proposal is the agent's and carries no weight; the
                          determination is the operator's and is the only field CompliantSet reads. */}
                      <th scope="col">Proposed</th>
                      <th scope="col">Determination</th>
                      <th scope="col">ppm window</th>
                      <th scope="col">Recommended</th>
                      <th scope="col">Amount</th>
                      <th scope="col">Availability</th>
                      <th scope="col">In code</th>
                      <th scope="col">Order</th>
                    </tr>
                  </thead>
                  {byComponentRows(state.read.rows).map(([componentId, rows]) => (
                    <tbody key={componentId}>
                      <tr>
                        {/* Components are row GROUPS here, not columns — that is the transposition. */}
                        <th colSpan={TOTAL_COLUMNS} scope="colgroup" style={{ textAlign: 'left' }}>
                          {componentId}{' '}
                          <span className="tiny muted">
                            {rows.length} substance{rows.length === 1 ? '' : 's'}
                          </span>
                        </th>
                      </tr>
                      {rows.map((row) => (
                        <MatrixRow key={`${row.componentId}|${row.cas}`} row={row} />
                      ))}
                    </tbody>
                  ))}
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </section>
  );
}

function MatrixRow({ row }: { row: ReadRow }) {
  return (
    <tr>
      <IdentityCell row={row} />
      {row.discovery.kind === 'cells' ? (
        <DiscoveryGroup cells={row.discovery.cells} />
      ) : (
        <AbsentCells state={row.discovery} span={SPAN.discovery} phase="Discovery" />
      )}
      {row.regulatory.kind === 'cells' ? (
        <RegulatoryGroup cells={row.regulatory.cells} />
      ) : (
        <AbsentCells state={row.regulatory} span={SPAN.regulatory} phase="Regulatory" />
      )}
      {row.dosing.kind === 'cells' ? (
        <DosingGroup cells={row.dosing.cells} />
      ) : (
        <AbsentCells state={row.dosing} span={SPAN.dosing} phase="Dosing" />
      )}
      {row.outcome.kind === 'cells' ? (
        <OutcomeGroup cells={row.outcome.cells} />
      ) : (
        <AbsentCells state={row.outcome} span={SPAN.outcome} phase="Sign-off" />
      )}
    </tr>
  );
}

function DiscoveryGroup({ cells }: { cells: DiscoveryCells }) {
  const sources = typeof cells.sources === 'number' && Number.isFinite(cells.sources) ? cells.sources : 0;
  return (
    <>
      <td>{typeof cells.tier === 'string' && cells.tier ? cells.tier : <span className="muted">—</span>}</td>
      <td>{cells.preferred === true ? 'preferred' : <span className="muted">—</span>}</td>
      <td className="secondary" style={{ minWidth: 220 }}>
        {typeof cells.rationale === 'string' ? cells.rationale : ''}
      </td>
      <td style={sources === 0 ? { color: 'var(--text-warning)' } : undefined}>{sources}</td>
    </>
  );
}

function RegulatoryGroup({ cells }: { cells: RegulatoryCells }) {
  const dimensions = Array.isArray(cells.dimensions) ? cells.dimensions : [];
  const overall = asVerdict(cells.overall);
  return (
    <>
      <td>
        <span className={`chip ${verdictClass(overall)}`}>{overall}</span>
      </td>
      {VERDICT_DIMENSIONS.map((d) => {
        const dim = dimensions.find((x) => x?.dimension === d);
        const status = dim ? asVerdict(dim.status) : null;
        return (
          <td key={d} style={{ textAlign: 'center' }}>
            {status ? (
              <span className={`chip ${verdictClass(status)}`} title={`${d} — ${status}`}>
                {verdictGlyph(status)}
                <span className="sr-only">
                  {' '}
                  {d} {status}
                </span>
              </span>
            ) : (
              /* An unassessed dimension is NOT a pass, and it does not get an empty cell. */
              <span style={{ color: 'var(--text-warning)' }} title={`${d} — not assessed`}>
                ?<span className="sr-only"> {d} not assessed</span>
              </span>
            )}
          </td>
        );
      })}
      <td style={{ color: 'var(--text-pro)' }}>
        {cells.proposedDetermination ? (
          <Data kind="code">{cells.proposedDetermination}</Data>
        ) : (
          <span className="muted">—</span>
        )}
      </td>
      <td style={{ color: 'var(--text-teal)', fontWeight: 600 }}>
        {cells.determination ? (
          <Data kind="code">{cells.determination}</Data>
        ) : (
          <span className="muted">unsigned</span>
        )}
      </td>
    </>
  );
}

function DosingGroup({ cells }: { cells: DosingCells }) {
  const recommended = cells.recommendedPpm;
  return (
    <>
      <td style={{ whiteSpace: 'nowrap' }}>
        <BoundValue bound={cells.floor} />
        <span className="muted"> – </span>
        <BoundValue bound={cells.upper} />
      </td>
      <td style={{ fontWeight: 600 }}>
        {typeof recommended === 'number' && Number.isFinite(recommended) ? (
          <Data kind="ppm">{fmtPpm(recommended)}</Data>
        ) : (
          <span style={{ color: 'var(--text-danger)' }}>unreadable</span>
        )}
      </td>
      <td>
        <AmountValue cells={cells} />
      </td>
      <td>
        <AvailabilityValue cells={cells} />
      </td>
    </>
  );
}

function OutcomeGroup({ cells }: { cells: OutcomeCells }) {
  return (
    <>
      <td>
        {cells.inCode ? (
          <Data kind="code">{cells.inCode}</Data>
        ) : (
          <span className="small muted">in no signed code</span>
        )}
      </td>
      <td>
        {cells.ordered === true ? (
          <span className="chip chip--neutral">ordered</span>
        ) : (
          <span className="small muted">not ordered</span>
        )}
      </td>
    </>
  );
}
