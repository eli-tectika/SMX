import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  NotFound,
  approveRegulatory,
  getMatrix,
  getRegulatoryGate,
} from '../../api/client';
import type {
  Determination,
  DimensionVerdict,
  MatrixCell,
  MatrixDoc,
  RegulatoryCells,
  RegulatoryGate,
  SubstanceSpec,
  VerdictDimension,
  VerdictStatus,
} from '../../api/types';
import { VERDICT_DIMENSIONS, VERDICT_SEVERITY } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { cellBlockerKey, parseBlocker, type CellBlocker } from '../../domain/gate';
import { verdictClass, verdictGlyph } from '../../domain/matrix';
import { agentProposal, operatorRuling, reviewStance } from '../../domain/proposal';
import {
  AbsentCells,
  DroppedRows,
  IdentityCell,
  TableError,
  byComponentRows,
  useProjectTable,
  type ReadRow,
} from './projectTable';
import type { ScreenProps } from '../ProjectLayout';

const cellKey = (cas: string, componentId: string) => `${cas}|${componentId}`;

/** The Regulatory column group, minus identity: verdict, four dimensions, proposal, determination. */
const REGULATORY_SPAN = 7;

/** Short heads for the four dimension columns. The full name is on each chip's accessible label. */
const DIMENSION_HEAD: Record<VerdictDimension, string> = {
  Compatibility: 'Compat.',
  ElementGate: 'Element gate',
  ApplicationCheck: 'Application',
  Hazard: 'Hazard',
};

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

/**
 * WHO signed, folded to three cases — and `null` and `undefined` collapse to the same one.
 *
 * `null` is "the record does not say"; an absent key is an older API build, or a frontend and backend
 * that have skewed in a deploy. Neither is evidence that a human ruled, so both land on `unknown`. The
 * fold is written as an allow-list rather than `?? 'unknown'` on purpose: a signer string this build
 * has never heard of — a future `'vp'`, a typo in a seed script — must also fall to unknown rather
 * than through a default that happens to read as a person.
 *
 * Only meaningful on an APPROVED gate. A locked gate can carry a stale signer for one write (both the
 * revision path and an amendment void `approvedAt`/`approvedBy` as a PAIR), and a screen that rendered
 * the signer whenever it was non-null would print a signature over a gate that was deliberately voided.
 */
type Signer = 'operator' | 'auto-approve' | 'unknown';

function signerOf(gate: RegulatoryGate): Signer {
  if (gate.approvedBy === 'operator') return 'operator';
  if (gate.approvedBy === 'auto-approve') return 'auto-approve';
  return 'unknown';
}

/** A date, or nothing. Never a slice of whatever the wire happened to carry. */
function signedOn(at: string | null | undefined): string | null {
  return typeof at === 'string' && at.length >= 10 ? at.slice(0, 10) : null;
}

/**
 * Regulatory — the matrix, ruled on, and the signature over it.
 *
 * The matrix is not a phase of its own any more (spec §4): it was never a stage's OUTPUT, it is the
 * shape every phase's output takes, so the regulatory column group IS the matrix on this screen. What
 * used to be two destinations — a grid you read and a gate you signed, each fetching its own slice of
 * the same record — is one.
 *
 * WHAT THE SIGNATURE NOW MEANS, and it changed. It used to release dosing, cost and procurement,
 * because nothing downstream ran until it was given. The pipeline runs end to end now (execution-core
 * §8): Dosing has already computed, over `ProvisionalSet` — the agent's proposals folded in — and that
 * DosingDoc is stamped provisional and blocks the order. So this signature releases exactly one thing:
 * the compliance-package export. The order is released by the VP's, and both remain refused over an
 * incomplete analysis.
 *
 * Three states, one screen. Locked with cells unruled (the work is in the table), locked and armable
 * (the work is the signature), and approved — where an approved gate is not one fact but three:
 *
 *   operator      — the R.E.'s determination, recorded by the operator. Signed, normal, quiet.
 *   auto-approve  — REGULATORY_AUTO_APPROVE signed it. NO HUMAN REVIEWED ANYTHING, and it wrote the
 *                   same cell fields an operator's ruling writes. The loudest thing on the screen.
 *   unknown       — an approved gate whose record does not name a signer. It may have been a person
 *                   and it may have been the machine; the record cannot tell us, so we do not guess.
 *
 * The gate arms on SERVER truth. The button reads `gate.armable`, never a browser-side tally, and the
 * backend re-checks on approve — so a concurrent revise can refuse a button that looked live, in which
 * case we re-read and show the fresh blockers.
 */
export function Regulatory({ project, refreshProject }: ScreenProps) {
  const status = project.stages.regulatory?.status;
  const { state, reload: reloadTable } = useProjectTable(project.projectId, status);

  const [gate, setGate] = useState<RegulatoryGate | null>(null);
  const [gateRead, setGateRead] = useState(false);
  /**
   * The assembled matrix, read ALONGSIDE the table and for one reason: the projection carries each
   * cell's determinations but not the two REASONS behind them, and a proposal shown without the
   * agent's stated reason is a verdict the operator is asked to confirm blind. The table drives every
   * row and every column; this fills the evidence panel.
   */
  const [matrix, setMatrix] = useState<MatrixDoc | 'absent' | null>(null);
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [signBusy, setSignBusy] = useState(false);
  const [signError, setSignError] = useState<string | null>(null);
  const [reviseNonce, setReviseNonce] = useState(0);
  const rowRefs = useRef<Record<string, HTMLTableRowElement | null>>({});

  const reloadGate = useCallback(
    async (signal?: { cancelled: boolean }) => {
      const [g, m] = await Promise.all([
        getRegulatoryGate(project.projectId).then(
          (r) => ({ ok: true as const, gate: r }),
          () => ({ ok: false as const, gate: null }),
        ),
        getMatrix(project.projectId).then(
          (r) => (r === NotFound ? 'absent' : r),
          () => null,
        ),
      ]);
      if (signal?.cancelled) return;
      setGate(g.gate);
      setGateRead(true);
      setMatrix(m);
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void reloadGate(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [reloadGate, status]);

  const reload = useCallback(async () => {
    await Promise.all([reloadTable(), reloadGate()]);
  }, [reloadTable, reloadGate]);

  const openCell = useCallback((key: string) => {
    setOpenKey(key);
    // `?.scrollIntoView?.()` — the METHOD is optional too, not only the element. It is unimplemented in
    // jsdom, and a scroll convenience that throws asynchronously out of a rAF callback would take down
    // the screen that rules on verdicts over a nicety.
    requestAnimationFrame(() =>
      rowRefs.current[key]?.scrollIntoView?.({ block: 'center', behavior: 'smooth' }),
    );
  }, []);

  const sign = useCallback(async () => {
    setSignBusy(true);
    setSignError(null);
    try {
      await approveRegulatory(project.projectId);
      await reload();
      // The signature releases the compliance-package export, and the shell reads the PROJECT rather
      // than the gate — without this it goes on describing an unsigned project.
      refreshProject();
    } catch (err) {
      // The server re-checks armability; a concurrent revise can refuse a button that looked live.
      setSignError(err instanceof ApiError ? err.message : String(err));
      await reload();
    } finally {
      setSignBusy(false);
    }
  }, [project.projectId, reload, refreshProject]);

  if (state.kind === 'loading' || !gateRead) return <Loading what="the verdicts and the gate" />;

  const g = gate;
  const blockers: string[] = Array.isArray(g?.blockers) ? g!.blockers : [];
  const approved = g?.status === 'approved';
  const signer: Signer = g ? signerOf(g) : 'unknown';
  const when = signedOn(g?.approvedAt);

  /**
   * Whether the DETERMINATIONS below can be read as a person's.
   *
   * Under auto-approve the pipeline adopts the agent's proposals as final and marks every verdict
   * reviewed, writing the same fields an operator's ruling writes. The cell record cannot tell them
   * apart; the gate can, and this is the only place that knowledge exists. Teal is the operator's
   * colour in the token grammar, and a machine-written determination may not wear it.
   */
  const machineRuled = approved && signer === 'auto-approve';

  const parsed = blockers.map(parseBlocker);
  const cellBlockers = parsed.filter((b): b is CellBlocker => b.kind === 'cell');
  const incomplete = parsed.some((b) => b.kind === 'message');
  const armable = g?.armable === true;

  /*
   * Re-signing over a machine or unattributed signature is a real, supported act — the endpoint
   * replaces the signer and moves the timestamp — so the remedy sits with the alarm that reports the
   * problem. An operator-signed gate gets no button: a second press would be idempotent and would
   * only invite the operator to re-sign what they already signed.
   */
  const canStillSign = !approved || signer !== 'operator';

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
        <SectionHeader title="The regulatory sign-off" headingLevel={3} />

        {/*
          A gate read that did not answer. It cannot be rendered as "locked" — that would be the screen
          asserting a state it does not know — and it must not show an unarmable sign block whose
          checklist reads all-met, which is what a defaulted empty gate produces.
        */}
        {!g ? (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The gate could not be read.</b> Nothing came back where the gate should be, so this
              screen cannot say whether it is signed or by whom. Do not act on the verdicts below until
              it can.
            </div>
          </div>
        ) : (
          <>
            {approved && <SignedPanel signer={signer} when={when} />}
            {canStillSign && (
              <SignBlock
                approved={approved}
                armable={armable}
                incomplete={incomplete}
                unreviewed={cellBlockers.length}
                busy={signBusy}
                onSign={() => void sign()}
                onOpenFirst={
                  cellBlockers.length > 0 ? () => openCell(cellBlockerKey(cellBlockers[0])) : undefined
                }
              />
            )}
          </>
        )}

        {signError && (
          <div className="banner danger" role="alert" style={{ marginTop: 'var(--s3)' }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The gate was not signed.</b>
              <div>{signError}</div>
            </div>
          </div>
        )}
      </section>

      <section className="screen">
        <SectionHeader
          title="Verdicts"
          headingLevel={3}
          count={state.kind === 'ready' ? state.read.rows.length : undefined}
          hint="one row per substance and component — rule on each, then the gate can arm"
        />

        {state.kind === 'error' && <TableError message={state.message} />}

        {state.kind === 'ready' && (
          <>
            <DroppedRows n={state.read.dropped} />

            {reasonsUnavailable && state.read.rows.length > 0 && (
              <div className="banner warn" role="alert">
                <i className="ti ti-alert-triangle" aria-hidden="true" />
                <div>
                  The assembled matrix could not be read (or has not been written yet), so the{' '}
                  <b>reasons</b> behind each proposal and each determination are not shown below. The
                  verdicts themselves come from the project table and are unaffected.
                </div>
              </div>
            )}

            {state.read.rows.length === 0 ? (
              <EmptyState
                icon="ti-gavel"
                title="No verdicts to rule on yet."
                body="Screening has produced no rows for this project. The gate opens once it does."
              />
            ) : (
              byComponentRows(state.read.rows).map(([componentId, rows]) => (
                <div key={componentId} style={{ marginBottom: 'var(--s5)' }}>
                  <SectionHeader
                    eyebrow="Component"
                    title={componentId}
                    headingLevel={4}
                    count={rows.length}
                    hint="the element gate is product-wide; the application check is this component's"
                  />
                  <table className="mx">
                    <thead>
                      <tr>
                        <th>Substance</th>
                        <th>Verdict</th>
                        {VERDICT_DIMENSIONS.map((d) => (
                          <th key={d} style={{ textAlign: 'center' }}>
                            {DIMENSION_HEAD[d]}
                          </th>
                        ))}
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
                          machineRuled={machineRuled}
                          blocking={cellBlockers.some(
                            (b) => cellBlockerKey(b) === cellKey(row.cas, row.componentId),
                          )}
                          isOpen={openKey === cellKey(row.cas, row.componentId)}
                          onToggle={(k) => (openKey === k ? setOpenKey(null) : openCell(k))}
                          onWrote={() => void reload()}
                          onRevised={() => {
                            setReviseNonce((n) => n + 1);
                            void reload();
                          }}
                          rowRef={(el) => {
                            rowRefs.current[cellKey(row.cas, row.componentId)] = el;
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
  machineRuled,
  blocking,
  isOpen,
  onToggle,
  onWrote,
  onRevised,
  rowRef,
}: {
  row: ReadRow;
  projectId: string;
  matrixCells: MatrixCell[];
  matrixRows: SubstanceSpec[];
  machineRuled: boolean;
  blocking: boolean;
  isOpen: boolean;
  onToggle: (key: string) => void;
  onWrote: () => void;
  onRevised: () => void;
  rowRef: (el: HTMLTableRowElement | null) => void;
}) {
  const key = cellKey(row.cas, row.componentId);

  if (row.regulatory.kind !== 'cells') {
    return (
      <tr ref={rowRef}>
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
  const substance: SubstanceSpec | undefined =
    matrixRows.find((r) => r.cas === row.cas) ?? { element: row.element, form: row.form, cas: row.cas };

  return (
    <Fragment>
      <tr ref={rowRef} className={blocking && !isOpen ? 'hatch-danger' : undefined}>
        <IdentityCell row={row} />
        <td>
          <span className={`chip ${verdictClass(cell.overall)}`}>{cell.overall}</span>
        </td>
        {VERDICT_DIMENSIONS.map((d) => {
          const dim = dimensions.find((x) => x?.dimension === d);
          return (
            <td key={d} style={{ textAlign: 'center' }}>
              {dim ? (
                <span
                  className={`chip ${verdictClass(asVerdict(dim.status))}`}
                  title={`${d} — ${asVerdict(dim.status)}`}
                >
                  {verdictGlyph(asVerdict(dim.status))}
                  <span className="sr-only">
                    {' '}
                    {d} {asVerdict(dim.status)}
                  </span>
                </span>
              ) : (
                /* An unassessed dimension is NOT a pass. It gets its own mark and its own words. */
                <span className="small" style={{ color: 'var(--text-warning)' }} title={`${d} — not assessed`}>
                  ?<span className="sr-only"> {d} not assessed</span>
                </span>
              )}
            </td>
          );
        })}
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
            <span className="tiny muted">unsigned</span>
          ) : machineRuled ? (
            <span className="small" style={{ color: 'var(--text-warning)', fontWeight: 600 }}>
              <span className="data">{signed.determination}</span>
              <span style={{ fontWeight: 400 }}> · adopted by the machine</span>
            </span>
          ) : (
            <span
              className="small"
              style={{ color: 'var(--text-teal)', fontWeight: 600, fontFamily: 'var(--font-mono)' }}
            >
              {signed.determination}
              {stance === 'overridden' && <span className="muted"> (overrode agent)</span>}
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

/**
 * Who signed, and when, in one line — or, where the answer is bad, in as many as it takes.
 *
 * The three branches are deliberately not one parameterised box. They differ in tone, in size, in what
 * they claim and in whether they say anything at all about a person, and a shared shell with a `tone`
 * prop is how three different facts come to look like one fact rendered three ways.
 */
function SignedPanel({ signer, when }: { signer: Signer; when: string | null }) {
  if (signer === 'operator') {
    return (
      <div
        className="banner"
        data-signer="operator"
        style={{
          background: 'var(--bg-teal)',
          borderColor: 'var(--border-teal)',
          color: 'var(--text-teal)',
        }}
      >
        <i className="ti ti-writing-sign" aria-hidden="true" />
        <div className="prose">
          <b>You signed the Regulatory Expert&rsquo;s determination{when ? ` on ${when}` : ''}.</b> The
          compliance package can now be exported. Procurement is a separate signature.
        </div>
      </div>
    );
  }

  if (signer === 'auto-approve') {
    return (
      /*
       * The loudest thing on the screen, and it should be. Every other red surface in this app reports
       * a claim that might be wrong; this one reports that the safety property the whole product is
       * built around — a human signs the hard gate — was not upheld, on this project, already.
       *
       * `role="alert"` because it is: the panel arrives on an async read, which is a mutation the
       * screen reader announces, and an operator who cannot see the hatching gets the same news.
       */
      <div
        className="hatch-danger"
        data-signer="auto-approve"
        role="alert"
        style={{
          border: '1px solid var(--border-danger)',
          borderRadius: 'var(--r3)',
          padding: 'var(--s4)',
        }}
      >
        <p
          style={{
            margin: 0,
            fontFamily: 'var(--font-serif)',
            fontSize: 'var(--t-title)',
            fontWeight: 'var(--w-semibold)',
            lineHeight: 'var(--lh-tight)',
            color: 'var(--text-danger)',
          }}
        >
          <i className="ti ti-robot" aria-hidden="true" /> Signed by the machine. No human reviewed
          anything.
        </p>
        <p className="prose" style={{ margin: 'var(--s3) 0 0', color: 'var(--text-danger)' }}>
          Automatic approval adopted the agent&rsquo;s proposed determinations as final and marked every
          verdict below as reviewed{when ? `, on ${when}` : ''}. No Regulatory Expert ruled on them and
          nobody signed for them.
        </p>
        <p className="prose" style={{ margin: 'var(--s2) 0 0', color: 'var(--text-danger)' }}>
          The compliance package can be exported on that basis. Treat every determination below as
          unmade, open the evidence, and sign this gate for real &mdash; your signature replaces the
          machine&rsquo;s.
        </p>
      </div>
    );
  }

  return (
    /*
     * Approved, signer unrecorded. The temptation is to read this as the operator, because in practice
     * it usually was — and that is precisely why it must not: a gate written before the record named
     * its signer cannot be retroactively claimed as a person's, and the one build that signs this gate
     * without a person is the one whose gates would go unattributed. Withhold.
     */
    <div className="banner warn" data-signer="unknown">
      <i className="ti ti-help-circle" aria-hidden="true" />
      <div className="prose">
        <b>Approved{when ? ` on ${when}` : ''} &mdash; the record does not say by whom.</b> It may have
        been the Regulatory Expert&rsquo;s determination and it may have been an automatic approval; the
        record cannot tell you which, and neither can the determinations below. Do not read it as a
        human ruling. Signing again attributes it, to you, now.
      </div>
    </div>
  );
}

/**
 * The signature, with the arming requirements ATTACHED to the button rather than floating above the
 * table in a card of their own.
 *
 * `armable` is the SERVER's answer and it is what enables the press. The checklist is the server's
 * blockers, parsed, and it EXPLAINS the answer — it does not compute it. That distinction is the whole
 * reason armability is computed in `RegulatoryGate.Armable` and not here: a tally assembled in the
 * browser can disagree with the endpoint that is about to refuse it, and the direction it disagrees in
 * is a live-looking button over an unarmed gate.
 */
function SignBlock({
  approved,
  armable,
  incomplete,
  unreviewed,
  busy,
  onSign,
  onOpenFirst,
}: {
  approved: boolean;
  armable: boolean;
  incomplete: boolean;
  unreviewed: number;
  busy: boolean;
  onSign: () => void;
  onOpenFirst?: () => void;
}) {
  return (
    <div style={{ marginTop: approved ? 'var(--s4)' : 0 }}>
      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        {approved
          ? 'Signing records the Regulatory Expert’s determination under your name, and replaces the standing signature.'
          : 'The analysis ran without this signature — nothing waits for it. What it holds back is the compliance package: it cannot be exported until the Regulatory Expert has ruled and you have recorded that ruling here.'}
      </p>

      <ul style={{ listStyle: 'none', margin: '0 0 var(--s3)', padding: 0 }}>
        <Check
          met={!incomplete}
          label="Every candidate has a verdict"
          detail={incomplete ? 'Screening is still running — some cells have not been screened.' : undefined}
        />
        <Check
          met={unreviewed === 0}
          label="Every flagged verdict opened and reviewed"
          detail={
            unreviewed > 0
              ? `${unreviewed} still unopened. Opening the evidence is what records the review.`
              : undefined
          }
          action={
            unreviewed > 0 && onOpenFirst ? { label: 'Open the first', onClick: onOpenFirst } : undefined
          }
        />
      </ul>

      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--s3)', flexWrap: 'wrap' }}>
        <button
          className="btn primary"
          type="button"
          disabled={!armable || busy}
          onClick={onSign}
          title={
            armable
              ? undefined
              : 'Locked until the checks above pass — the server refuses a signature until then'
          }
        >
          <i className={`ti ${busy ? 'ti-loader' : 'ti-signature'}`} aria-hidden="true" />{' '}
          {busy ? 'Signing…' : 'Sign the R.E. determination'}
        </button>
        <span className="small" style={{ color: 'var(--text-warning)' }}>
          {armable
            ? 'This releases the compliance-package export.'
            : 'Locked until the checks above pass.'}
        </span>
      </div>
    </div>
  );
}

/** One arming requirement. Met is a statement of fact, not a celebration — no green tick. */
function Check({
  met,
  label,
  detail,
  action,
}: {
  met: boolean;
  label: string;
  detail?: string;
  action?: { label: string; onClick: () => void };
}) {
  return (
    <li
      style={{
        display: 'flex',
        alignItems: 'flex-start',
        gap: 'var(--s2)',
        padding: 'var(--s2) 0',
        borderTop: '1px solid var(--border)',
      }}
    >
      {/*
        The state is CARRIED by the glyph, so the glyph is named rather than hidden. A tick and a cross
        that differ only in shape and colour say nothing to a screen reader and little to a colour-blind
        operator — and this is a checklist whose entire content is which items are outstanding.
      */}
      <i
        className={`ti ${met ? 'ti-check' : 'ti-x'}`}
        role="img"
        aria-label={met ? 'met' : 'not met'}
        style={{ color: met ? 'var(--text-muted)' : 'var(--text-warning)', marginTop: 2 }}
      />
      <span style={{ flex: 1, minWidth: 0 }}>
        <span className="small" style={{ color: met ? 'var(--text-muted)' : 'var(--text-primary)' }}>
          {label}
        </span>
        {detail && (
          <span className="small" style={{ display: 'block', color: 'var(--text-warning)' }}>
            {detail}
          </span>
        )}
      </span>
      {action && (
        <button className="btn" type="button" onClick={action.onClick} style={{ flex: 'none' }}>
          {action.label}
        </button>
      )}
    </li>
  );
}
