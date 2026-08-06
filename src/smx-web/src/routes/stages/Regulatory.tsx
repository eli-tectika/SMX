import { Fragment, useCallback, useEffect, useState } from 'react';
import { NotFound, getMatrix } from '../../api/client';
import type {
  Determination,
  DimensionVerdict,
  MatrixCell,
  MatrixDoc,
  RegulatoryCells,
  SubstanceSpec,
  VerdictStatus,
} from '../../api/types';
import { VERDICT_DIMENSIONS, VERDICT_SEVERITY } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { verdictClass } from '../../domain/matrix';
import { agentProposal, operatorRuling, reviewStance } from '../../domain/proposal';
import {
  AbsentCells,
  ConfidenceCell,
  DroppedRows,
  GroupBand,
  IdentityCell,
  SourcesCell,
  TableError,
  WhyCell,
  byComponentRows,
  citationsOf,
  confidencesOf,
  useProjectTable,
  type PhaseGroup,
  type ReadRow,
} from './projectTable';
import type { ScreenProps } from '../ProjectLayout';

const cellKey = (cas: string, componentId: string) => `${cas}|${componentId}`;

/**
 * The Regulatory column group, minus identity: state, why, confidence, sources, and the two rulings.
 *
 * SIX, and it used to be seven — four of which were one glyph each, one per dimension, under a
 * subtitle explaining what the element gate was. The fold is worst-wins, so the cell's verdict IS its
 * worst dimension's: naming that dimension in `Why` and folding the four confidences into one number
 * loses nothing an operator could read off four glyphs, and an unassessed dimension is still called
 * out by name.
 */
const REGULATORY_SPAN = 6;

/**
 * The band above the headers. The two ruling columns sit INSIDE the regulatory group because that is
 * what they are — the agent's reading of it and the operator's ruling over it, still two columns.
 */
const GROUPS: PhaseGroup[] = [
  { group: 'identity', label: 'Material', span: 1 },
  { group: 'regulatory', label: 'Regulatory', span: REGULATORY_SPAN },
  { group: 'actions', label: '', span: 1 },
];

/**
 * A verdict this build can read, or `NeedsReview`.
 *
 * The safe direction is the whole point: an unreadable verdict must never become `Pass`. A status
 * string this enum has never heard of is not evidence of compliance, and the cost of over-flagging is
 * a person opening a cell they did not need to.
 */
const asVerdict = (v: unknown): VerdictStatus =>
  VERDICT_SEVERITY.includes(v as VerdictStatus) ? (v as VerdictStatus) : 'NeedsReview';

/**
 * The projection types both determinations as free strings, because that is what the wire carries.
 *
 * This carries the string across UNVALIDATED and that is deliberate: `domain/proposal.ts` is the one
 * place allowed to decide whether a string is a ruling, and it refuses anything that is not exactly
 * one of the two constants. So a value this build has never heard of arrives here, fails there, and
 * reads as NO ruling — which withholds rather than grants, the only safe direction for the field that
 * lets a chemical into a customer's product. Validating it a second time here would be a second
 * authority on the same question, which is how two answers drift apart.
 */
const asDeterminationField = (v: string | null | undefined): Determination | undefined =>
  v === null || v === undefined ? undefined : (v as Determination);

/*
 * `Signer`, `signerOf` and `signedOn` lived here, folding an approved gate's `approvedBy` through an
 * allow-list so that a machine signature could never read as a person's.
 *
 * THE GATE IS GONE (spec §16.4) — removed rather than demoted — so there is no signer to fold and no
 * `auto-approve` alarm to raise on this screen. The same discipline survives verbatim on Sign-off,
 * which is now the ONLY human checkpoint before procurement, and that is the file to look in.
 */

/**
 * Regulatory — the matrix, and the two acts an operator performs on a verdict.
 *
 * THERE IS NO SIGN-OFF ON THIS SCREEN ANY MORE (spec §16.4). The regulatory gate was not demoted, it
 * was removed: no `GateDoc`, no approve endpoint, no signature. The matrix carries confidence and
 * sources, and that IS the review surface — which is also why most of what this screen used to say in
 * prose has gone with it. Nearly all of that copy was explaining the gate.
 *
 * What is left is the matrix and two per-verdict acts, and both became load-bearing rather than
 * incidental when the gate they used to feed disappeared:
 *
 *   RULE  — `POST …/regulatory/determination`, recommended or rejected, reason mandatory. It is the
 *           operator's OVERRIDE: what may be dosed is now everything the agent did not reject minus
 *           anything the operator vetoed, so a `rejected` recorded here after dosing has run really
 *           does refuse the order server-side. The row says so once it is recorded.
 *   OPEN  — `POST …/regulatory/review`, a flagged finding read. The arming rule this used to feed
 *           still exists under a new owner: `EvidenceReview.Outstanding` refuses the VP determination
 *           and every order while a live non-Pass verdict is unopened. The consequence moved to the
 *           irreversible acts; the act itself is unchanged.
 *
 * THE ONE INVARIANT THAT SURVIVES EVERYTHING: the agent's PROPOSAL and the operator's DETERMINATION
 * are two columns and never one. Removing the gate did not remove the Law-9 line; it moved where the
 * line is enforced.
 */
export function Regulatory({ project }: ScreenProps) {
  const status = project.stages.regulatory?.status;
  const { state, reload: reloadTable } = useProjectTable(project.projectId, status);

  /**
   * The assembled matrix, read ALONGSIDE the table and for one reason: the projection carries each
   * cell's determinations but not the two REASONS behind them, and a proposal shown without the
   * agent's stated reason is a verdict the operator is asked to confirm blind. The table drives every
   * row and every column; this fills the evidence panel.
   */
  const [matrix, setMatrix] = useState<MatrixDoc | 'absent' | null>(null);
  const [matrixRead, setMatrixRead] = useState(false);
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [reviseNonce, setReviseNonce] = useState(0);

  const reloadMatrix = useCallback(
    async (signal?: { cancelled: boolean }) => {
      const m = await getMatrix(project.projectId).then(
        (r) => (r === NotFound ? ('absent' as const) : r),
        () => null,
      );
      if (signal?.cancelled) return;
      setMatrix(m);
      setMatrixRead(true);
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void reloadMatrix(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [reloadMatrix, status]);

  const reload = useCallback(async () => {
    await Promise.all([reloadTable(), reloadMatrix()]);
  }, [reloadTable, reloadMatrix]);

  if (state.kind === 'loading' || !matrixRead) return <Loading what="the verdicts" />;

  const matrixCells: MatrixCell[] =
    matrix && matrix !== 'absent' && Array.isArray(matrix.cells) ? matrix.cells : [];
  const matrixRows: SubstanceSpec[] =
    matrix && matrix !== 'absent' && Array.isArray(matrix.rows) ? matrix.rows : [];
  /**
   * The two reasons live only on the matrix doc, so a missing one costs the WORDS behind each ruling.
   * Both a failed read and a not-yet-assembled matrix cost them, and the screen says so rather than
   * rendering a proposal with no reason and letting it read as a proposal that had none.
   */
  const reasonsUnavailable = matrix === null || matrix === 'absent';

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="Verdicts"
          headingLevel={3}
          count={state.kind === 'ready' ? state.read.rows.length : undefined}
        />

        {state.kind === 'error' && <TableError message={state.message} />}

        {state.kind === 'ready' && (
          <>
            <DroppedRows n={state.read.dropped} />

            {reasonsUnavailable && state.read.rows.length > 0 && (
              <div className="banner warn" role="alert">
                <i className="ti ti-alert-triangle" aria-hidden="true" />
                <div>
                  The assembled matrix could not be read, so the <b>reasons</b> behind each proposal
                  and each determination are missing below.
                </div>
              </div>
            )}

            {state.read.rows.length === 0 ? (
              <EmptyState icon="ti-gavel" title="No verdicts on the record." />
            ) : (
              byComponentRows(state.read.rows).map(([componentId, rows]) => (
                <div key={componentId} style={{ marginBottom: 'var(--s5)' }}>
                  <SectionHeader
                    eyebrow="Component"
                    title={componentId}
                    headingLevel={4}
                    count={rows.length}
                  />
                  <table className="mx">
                    <thead>
                      <GroupBand groups={GROUPS} />
                      <tr className="mx__cols">
                        <th data-rowhead>Material</th>
                        <th>State in this phase</th>
                        <th>Why</th>
                        <th>Confidence</th>
                        <th>Sources</th>
                        {/*
                          TWO COLUMNS, AND THEY NEVER BECOME ONE. The proposal is the agent's and
                          carries no weight — nothing downstream reads it. The determination is the
                          operator's and is the only field CompliantSet reads. A UI that merged them
                          would be the agent signing this gate at the rendering layer, which is
                          precisely what the two-field split on the record exists to prevent.
                        */}
                        <th>Agent proposal</th>
                        <th>Your determination</th>
                        <th style={{ width: 80 }} />
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((row) => (
                        <VerdictRow
                          key={cellKey(row.cas, row.componentId)}
                          row={row}
                          projectId={project.projectId}
                          matrixCells={matrixCells}
                          matrixRows={matrixRows}
                          isOpen={openKey === cellKey(row.cas, row.componentId)}
                          onToggle={(k) =>
                            setOpenKey(openKey === k ? null : cellKey(row.cas, row.componentId))
                          }
                          onWrote={() => void reload()}
                          onRevised={() => {
                            setReviseNonce((n) => n + 1);
                            void reload();
                          }}
                        />
                      ))}
                    </tbody>
                  </table>
                </div>
              ))
            )}
          </>
        )}

        <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
      </section>
    </>
  );
}

/**
 * One substance × component: its verdict, its four dimensions, and the two rulings side by side.
 *
 * The `MatrixCell` handed to the evidence panel is built from the TABLE and topped up from the matrix
 * doc, never the other way round: the table is the one projection every screen and the XLSX export
 * read, and a panel that quietly rendered a different document's copy of a cell would be a second
 * source of truth for the field that lets a chemical into a product.
 */
function VerdictRow({
  row,
  projectId,
  matrixCells,
  matrixRows,
  isOpen,
  onToggle,
  onWrote,
  onRevised,
}: {
  row: ReadRow;
  projectId: string;
  matrixCells: MatrixCell[];
  matrixRows: SubstanceSpec[];
  isOpen: boolean;
  onToggle: (key: string) => void;
  onWrote: () => void;
  onRevised: () => void;
}) {
  const key = cellKey(row.cas, row.componentId);

  if (row.regulatory.kind !== 'cells') {
    return (
      <tr>
        <IdentityCell row={row} />
        <AbsentCells state={row.regulatory} span={REGULATORY_SPAN} phase="Regulatory" />
        <td />
      </tr>
    );
  }

  const cells: RegulatoryCells = row.regulatory.cells;
  const dimensions: DimensionVerdict[] = Array.isArray(cells.dimensions) ? cells.dimensions : [];
  const fromMatrix = matrixCells.find((c) => c.cas === row.cas && c.componentId === row.componentId);

  const cell: MatrixCell = {
    cas: row.cas,
    componentId: row.componentId,
    overall: asVerdict(cells.overall),
    dimensions,
    proposedDetermination: asDeterminationField(cells.proposedDetermination),
    proposedReason: fromMatrix?.proposedReason,
    determination: asDeterminationField(cells.determination),
    determinationReason: fromMatrix?.determinationReason,
    evidenceReviewed: cells.evidenceReviewed === true,
  };

  const proposal = agentProposal(cell);
  const signed = operatorRuling(cell);
  const stance = reviewStance(cell);
  /*
   * The condition the two irreversible acts refuse over, mirrored — never computed as authority. The
   * server decides (`EvidenceReview.Outstanding`); this only shows the operator WHICH rows it is
   * talking about, which is what a generic "not armable" refusal could never do.
   */
  const needsOpening = cell.overall !== 'Pass' && !cell.evidenceReviewed && !isOpen;
  const substance: SubstanceSpec | undefined =
    matrixRows.find((r) => r.cas === row.cas) ?? { element: row.element, form: row.form, cas: row.cas };

  return (
    <Fragment>
      {/* An UNOPENED non-Pass verdict is hatched. It is not decoration: `EvidenceReview.Outstanding`
          refuses the VP determination and every order while one exists, so this row is what stands
          between the project and procurement. */}
      <tr className={needsOpening ? 'hatch-danger' : undefined}>
        <IdentityCell row={row} />
        <td>
          <span className={`chip ${verdictClass(cell.overall)}`}>{cell.overall}</span>
        </td>
        <WhyCell cells={cells} />
        {/* Four dimension confidences, folded worst-wins — see domain/confidence.ts. `expected` is
            the full dimension set, so a cell screened on three of four says the fold is partial
            rather than presenting a number as if it covered everything. */}
        <ConfidenceCell values={confidencesOf(cells)} expected={VERDICT_DIMENSIONS.length} />
        <SourcesCell citations={citationsOf(cells)} />
        {/* The agent's, in the agent's colour. It carries no weight and says so. */}
        <td>
          {proposal ? (
            <span className="small" style={{ color: 'var(--text-pro)', fontFamily: 'var(--font-mono)' }}>
              {proposal.determination}
            </span>
          ) : (
            <span className="tiny muted">none</span>
          )}
        </td>
        {/* The operator's. The only field that lets this substance be dosed for real. */}
        <td>
          {!signed ? (
            <span className="tiny muted">no ruling</span>
          ) : (
            <span
              className="small"
              style={{ color: 'var(--text-teal)', fontWeight: 600, fontFamily: 'var(--font-mono)' }}
            >
              {signed.determination}
              {stance === 'overridden' && <span className="muted"> (overrode agent)</span>}
              {/* A veto recorded after dosing ran refuses the order server-side. The row says so:
                  the consequence is real and arrives with no other visible sign. */}
              {signed.determination === 'rejected' && (
                <div className="tiny" style={{ color: 'var(--text-warning)', fontWeight: 400 }}>
                  ordering refused
                </div>
              )}
            </span>
          )}
        </td>
        <td>
          {/* Opening the evidence is what marks the cell reviewed — the write happens in the panel,
              on a real endpoint. Nothing self-marks: a row may not report itself reviewed because it
              was rendered. */}
          <button className="btn" onClick={() => onToggle(key)} aria-expanded={isOpen}>
            {isOpen ? 'Hide' : 'Rule'}
          </button>
        </td>
      </tr>
      {isOpen && (
        <tr>
          <td colSpan={REGULATORY_SPAN + 2} style={{ padding: 0, background: 'var(--surface-2)' }}>
            <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
              <EvidencePanel
                projectId={projectId}
                cell={cell}
                substance={substance}
                onClose={() => onToggle(key)}
                onWrote={onWrote}
              />
              {/* No direct edits: to change a verdict, tell the agent why (Law 4). */}
              <div style={{ marginTop: 'var(--s3)' }}>
                <ReviseForm
                  projectId={projectId}
                  stage="regulatory"
                  fixedTarget={`${row.element} ${row.form} on ${row.componentId}`}
                  cas={row.cas}
                  componentId={row.componentId}
                  onRequested={onRevised}
                />
              </div>
            </div>
          </td>
        </tr>
      )}
    </Fragment>
  );
}

/*
 * `SignedPanel`, `SignBlock` and `Check` lived here — the three signer treatments, the arming
 * checklist and the pen. All three are deleted with the gate they belonged to (spec §16.4).
 *
 * The anti-rubber-stamping machinery they carried was not thrown away, it CHANGED OWNER: the
 * unopened-flagged-verdict rule now refuses `POST /decision/determination` and `POST /orders/{cas}`,
 * and Sign-off is where the operator is shown which rows are outstanding and how to reach them. A
 * checklist attached to a button that no longer exists would have been the worse half of the trade:
 * one gate removed and the other left blind.
 */
