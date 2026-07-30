import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  NotFound,
  approveRegulatory,
  getMatrix,
  getRegulatoryGate,
} from '../../api/client';
import type { MatrixCell, MatrixDoc, RegulatoryGate, SubstanceSpec } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { cellBlockerKey, parseBlocker, type CellBlocker } from '../../domain/gate';
import { verdictClass } from '../../domain/matrix';
import { operatorRuling, reviewStance } from '../../domain/proposal';
import type { ScreenProps } from '../ProjectLayout';

const cellKey = (cas: string, componentId: string) => `${cas}|${componentId}`;

/**
 * WHO signed, folded to three cases — and `null` and `undefined` collapse to the same one.
 *
 * `null` is "the record does not say"; an absent key is an older API build, or a frontend and
 * backend that have skewed in a deploy. Neither is evidence that a human ruled, so both land on
 * `unknown`. The fold is written as an allow-list rather than `?? 'unknown'` on purpose: a signer
 * string this build has never heard of — a future `'vp'`, a typo in a seed script — must also fall
 * to unknown rather than through a default that happens to read as a person.
 *
 * Only meaningful on an APPROVED gate. A locked gate can carry a stale signer for one write
 * (PipelineRunner voids `approvedAt`/`approvedBy` together when a revision breaks the gate), and a
 * screen that rendered the signer whenever it was non-null would print a signature over a gate that
 * revision deliberately voided.
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
 * Regulatory — ruling on verdicts, and signing the hard gate.
 *
 * Three states, one screen. Two of them are locked and differ in what the operator must do next:
 * with cells still unruled the work is in the table below, and with the server saying `armable` the
 * work is the signature. The third is approved — and approved is where this screen earns its keep,
 * because an approved gate is not one fact but three:
 *
 *   operator      — the R.E.'s determination, recorded by the operator. Signed, normal, quiet.
 *   auto-approve  — REGULATORY_AUTO_APPROVE signed it. NO HUMAN REVIEWED ANYTHING, and the verdicts
 *                   below already flowed to dosing, cost and procurement on that basis. This is not
 *                   a warning borrowed from elsewhere; it is the exact failure this gate exists to
 *                   prevent, having happened, so it is the loudest thing on the screen.
 *   unknown       — an approved gate whose record does not name a signer. It may have been a person
 *                   and it may have been the machine; the record cannot tell us, so the screen does
 *                   not guess. Rendering it as a human signature would be the system inventing the
 *                   one fact it exists to protect.
 *
 * The two unattributed states keep the sign control. That is not decoration: `POST
 * /regulatory/approve` REPLACES a machine or unattributed signature with the operator's, and moves
 * the timestamp with it — so the remedy for a machine-signed gate is a real signature, and it is
 * one press away from the alarm that reports it.
 *
 * The gate arms on SERVER truth. The button reads `gate.armable`, never a browser-side tally of the
 * rows, and the backend re-checks on approve — so a concurrent revise can still refuse a button that
 * looked live, in which case we re-read the gate and show the fresh blockers.
 */
export function Regulatory({ project, refreshProject }: ScreenProps) {
  const [gate, setGate] = useState<RegulatoryGate | null>(null);
  const [doc, setDoc] = useState<MatrixDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'unassembled' | 'error'>('loading');
  const [loadError, setLoadError] = useState<string>();
  const [openKey, setOpenKey] = useState<string | null>(null);
  const [signBusy, setSignBusy] = useState(false);
  const [signError, setSignError] = useState<string | null>(null);
  const [reviseNonce, setReviseNonce] = useState(0);
  const rowRefs = useRef<Record<string, HTMLTableRowElement | null>>({});

  const reload = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const [g, m] = await Promise.all([
          getRegulatoryGate(project.projectId),
          getMatrix(project.projectId),
        ]);
        if (signal?.cancelled) return;
        setGate(g);
        if (m === NotFound) {
          setDoc(null);
          setPhase('unassembled');
        } else {
          setDoc(m);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setLoadError(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  useEffect(() => {
    const signal = { cancelled: false };
    void reload(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [reload]);

  const openCell = useCallback((key: string) => {
    setOpenKey(key);
    requestAnimationFrame(() =>
      rowRefs.current[key]?.scrollIntoView({ block: 'center', behavior: 'smooth' }),
    );
  }, []);

  const sign = useCallback(async () => {
    setSignBusy(true);
    setSignError(null);
    try {
      await approveRegulatory(project.projectId);
      await reload();
      // The signature is what un-parks the stage and releases dosing. The spine and the next-action
      // block at the top of this column read the PROJECT, not the gate, so without this they would
      // go on saying the R.E. is owed a determination that has just been recorded.
      refreshProject();
    } catch (err) {
      // The server re-checks armability; a concurrent revise can refuse a button that looked live.
      setSignError(err instanceof ApiError ? err.message : String(err));
      await reload(); // refresh the blockers so the operator sees why
    } finally {
      setSignBusy(false);
    }
  }, [project.projectId, reload, refreshProject]);

  if (phase === 'loading') return <Loading what="the regulatory gate" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <SectionHeader title="The regulatory gate" headingLevel={3} />
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div className="prose">
            <b>Could not read the gate.</b>
            <div>{loadError}</div>
          </div>
        </div>
      </section>
    );
  }

  /*
   * Nothing to rule on yet. `unassembled` is the matrix 404 — screening has not produced one — and
   * `!doc` covers a 200 whose body was not a matrix at all. They render the same absence: a screen
   * that fell through to the table would call `.map` on nothing and take the whole column down.
   */
  if (phase === 'unassembled' || !doc) {
    return (
      <section className="screen">
        <SectionHeader title="The regulatory gate" headingLevel={3} />
        <EmptyState
          icon="ti-gavel"
          title="No verdicts to rule on yet."
          body="Screening has not produced a compatibility matrix. The gate opens once it does."
        />
      </section>
    );
  }

  /*
   * Everything below reads the payload defensively. `client.ts` casts responses with `as` and runs
   * no runtime validation, so a backend that drifts on any of these three arrays turns a `.map`
   * into a TypeError — and a throw here unmounts this screen (StageErrorBoundary catches it), which
   * is a worse answer than a screen that renders what it did get.
   */
  const g: RegulatoryGate | null = gate;
  const cells: MatrixCell[] = Array.isArray(doc.cells) ? doc.cells : [];
  const rows: SubstanceSpec[] = Array.isArray(doc.rows) ? doc.rows : [];
  const blockers: string[] = Array.isArray(g?.blockers) ? g!.blockers : [];

  const approved = g?.status === 'approved';
  const signer: Signer = g ? signerOf(g) : 'unknown';
  const when = signedOn(g?.approvedAt);
  /**
   * Whether the DETERMINATIONS below can be read as a person's.
   *
   * Under auto-approve the pipeline adopts the agent's proposals as final and marks every verdict
   * reviewed, writing the same fields an operator's ruling writes — so `operatorRuling(cell)`
   * returns a determination that no operator made. The cell record cannot tell them apart; the gate
   * can, and this is the only place that knowledge exists. Teal is the operator's colour in the
   * token grammar, and a machine-written determination may not wear it.
   */
  const machineRuled = approved && signer === 'auto-approve';

  const parsed = blockers.map(parseBlocker);
  const cellBlockers = parsed.filter((b): b is CellBlocker => b.kind === 'cell');
  const incomplete = parsed.some((b) => b.kind === 'message');
  const armable = g?.armable === true;

  const substanceOf = (cas: string) => rows.find((r) => r.cas === cas);

  /*
   * The sign control shows while the gate is locked AND on an approved gate nobody can attribute to
   * a person. Re-signing over a machine or unattributed signature is a real, supported act — the
   * endpoint replaces the signer and moves the timestamp — so the remedy sits with the alarm that
   * reports the problem. An operator-signed gate gets no button: a second press would be idempotent
   * and would only invite the operator to re-sign what they already signed.
   */
  const canStillSign = !approved || signer !== 'operator';

  return (
    <>
      <section className="screen">
        <SectionHeader title="The regulatory gate" headingLevel={3} />

        {/*
          A 200 whose body is not a gate. It cannot be rendered as "locked" — that would be the
          screen asserting a state it does not know — and it must not silently show an unarmable
          sign block whose checklist reads all-met, which is what a defaulted empty gate produces.
          Say the gate is unreadable; the verdicts below are unaffected and still render.
        */}
        {!g ? (
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div className="prose">
              <b>The gate could not be read.</b> Nothing came back where the gate should be, so this
              screen cannot say whether it is signed or by whom. Do not act on the verdicts below
              until it can.
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
                  cellBlockers.length > 0
                    ? () => openCell(cellBlockerKey(cellBlockers[0]))
                    : undefined
                }
              />
            )}
          </>
        )}

        {signError && (
          <div className="banner danger" style={{ marginTop: 'var(--s3)' }}>
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
          count={cells.length}
          hint="one per substance and component — rule on each, then the gate can arm"
        />

        {cells.length === 0 ? (
          <p className="prose" style={{ margin: 0 }}>
            The matrix carries no cells. Nothing has been screened yet.
          </p>
        ) : (
          <table className="mx">
            <thead>
              <tr>
                <th>Substance</th>
                <th>Component</th>
                <th>Verdict</th>
                <th>Evidence</th>
                <th>Determination</th>
                <th style={{ width: 90 }} />
              </tr>
            </thead>
            <tbody>
              {cells.map((cell) => {
                const key = cellKey(cell.cas, cell.componentId);
                const sub = substanceOf(cell.cas);
                const isOpen = openKey === key;
                const signedRuling = operatorRuling(cell);
                const stance = reviewStance(cell);
                const isBlocking = cellBlockers.some((b) => cellBlockerKey(b) === key);
                return (
                  <Fragment key={key}>
                    <tr
                      ref={(el) => {
                        rowRefs.current[key] = el;
                      }}
                      className={isBlocking && !isOpen ? 'hatch-danger' : undefined}
                    >
                      <td>
                        {sub ? (
                          <>
                            <span style={{ fontWeight: 500 }}>{sub.element}</span>{' '}
                            <span className="secondary">{sub.form}</span>
                            <div className="tiny muted data">{cell.cas}</div>
                          </>
                        ) : (
                          <span className="data">{cell.cas}</span>
                        )}
                      </td>
                      <td className="secondary">{cell.componentId}</td>
                      <td>
                        <span className={`chip ${verdictClass(cell.overall)}`}>{cell.overall}</span>
                      </td>
                      {/*
                        Server truth about the evidence — and NOT in green. Green means Pass in this
                        app's colour grammar and nothing else; a reviewed checkbox is not a verdict.
                        Amber is the gate's colour, so an unreviewed cell wears it, and a machine-
                        marked one wears it too: under auto-approve `evidenceReviewed` was stamped by
                        the pipeline, and rendering that as settled would be the record claiming a
                        human read something nobody read.
                      */}
                      <td>
                        {machineRuled ? (
                          <span className="tiny" style={{ color: 'var(--text-warning)' }}>
                            <i className="ti ti-robot" aria-hidden="true" /> marked by the machine
                          </span>
                        ) : cell.evidenceReviewed ? (
                          <span className="tiny secondary">
                            <i className="ti ti-eye-check" aria-hidden="true" /> reviewed
                          </span>
                        ) : (
                          <span className="tiny" style={{ color: 'var(--text-warning)' }}>
                            <i className="ti ti-eye-exclamation" aria-hidden="true" /> not reviewed
                          </span>
                        )}
                      </td>
                      <td>
                        {!signedRuling ? (
                          <span className="tiny muted">unsigned</span>
                        ) : machineRuled ? (
                          <span
                            className="tiny"
                            style={{ color: 'var(--text-warning)', fontWeight: 600 }}
                          >
                            <span className="data">{signedRuling.determination}</span>
                            <span style={{ fontWeight: 400 }}> · adopted by the machine</span>
                          </span>
                        ) : (
                          <span
                            className="tiny"
                            style={{
                              color: 'var(--text-teal)',
                              fontWeight: 600,
                              fontFamily: 'var(--font-mono)',
                            }}
                          >
                            {signedRuling.determination}
                            {stance === 'overridden' && (
                              <span className="muted"> (overrode agent)</span>
                            )}
                          </span>
                        )}
                      </td>
                      <td>
                        {/*
                          Rule → the evidence, inline. Opening it is what marks the cell reviewed —
                          the write happens in the panel, on a real endpoint. Nothing here self-marks:
                          a row may not report itself reviewed because it was rendered.
                        */}
                        <button
                          className="btn"
                          onClick={() => (isOpen ? setOpenKey(null) : openCell(key))}
                          aria-expanded={isOpen}
                        >
                          {isOpen ? 'Hide' : 'Rule'}
                        </button>
                      </td>
                    </tr>
                    {isOpen && (
                      <tr>
                        <td colSpan={6} style={{ padding: 0, background: 'var(--surface-2)' }}>
                          {/*
                            One click away and no further: every dimension, its per-dimension
                            confidence, and every citation live in here rather than on the row. They
                            are what the operator needs when defending a decision, and they are
                            exactly what turns a 30-row table into an unreadable one when spread
                            across it.
                          */}
                          <div
                            style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}
                          >
                            <EvidencePanel
                              projectId={project.projectId}
                              // A cell whose dimensions did not survive the wire still renders: the
                              // panel folds and sorts them, and neither survives `undefined`.
                              cell={
                                Array.isArray(cell.dimensions)
                                  ? cell
                                  : { ...cell, dimensions: [] }
                              }
                              substance={sub}
                              onClose={() => setOpenKey(null)}
                              onWrote={() => reload()}
                            />
                            {/* No direct edits: to change a verdict, tell the agent why (Law 4). */}
                            <div style={{ marginTop: 'var(--s3)' }}>
                              <ReviseForm
                                projectId={project.projectId}
                                stage="regulatory"
                                fixedTarget={`${
                                  sub ? `${sub.element} ${sub.form}` : cell.cas
                                } on ${cell.componentId}`}
                                cas={cell.cas}
                                componentId={cell.componentId}
                                onRequested={() => {
                                  setReviseNonce((n) => n + 1);
                                  void reload();
                                }}
                              />
                            </div>
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        )}

        <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
      </section>
    </>
  );
}

/**
 * Who signed, and when, in one line — or, where the answer is bad, in as many as it takes.
 *
 * The three branches are deliberately not one parameterised box. They differ in tone, in size, in
 * what they claim and in whether they say anything at all about a person, and a shared shell with a
 * `tone` prop is how three different facts come to look like one fact rendered three ways.
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
          <b>You signed the Regulatory Expert&rsquo;s determination{when ? ` on ${when}` : ''}.</b>{' '}
          The recommended substances are released to dosing, cost and procurement.
        </div>
      </div>
    );
  }

  if (signer === 'auto-approve') {
    return (
      /*
       * The loudest thing on the screen, and it should be. Every other red surface in this app
       * reports a claim that might be wrong; this one reports that the safety property the whole
       * product is built around — a human signs the hard gate — was not upheld, on this project,
       * already, downstream.
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
        {/*
          The red on these two is NOT redundant with `.hatch-danger .prose { color: inherit }`.
          `.hatch-danger` paints a background and sets no colour, so `inherit` resolves to the
          page's primary ink — dropping these would quietly repaint the loudest alarm in the
          product as ordinary body copy. (`.banner.danger` DOES set colour; a hatch does not.)
        */}
        <p className="prose" style={{ margin: 'var(--s3) 0 0', color: 'var(--text-danger)' }}>
          Automatic approval adopted the agent&rsquo;s proposed determinations as final and marked
          every verdict below as reviewed{when ? `, on ${when}` : ''}. No Regulatory Expert ruled on
          them and nobody signed for them.
        </p>
        <p className="prose" style={{ margin: 'var(--s2) 0 0', color: 'var(--text-danger)' }}>
          The recommended substances have already been released to dosing, cost and procurement on
          that basis. Treat every determination below as unmade, open the evidence, and sign this
          gate for real &mdash; your signature replaces the machine&rsquo;s.
        </p>
      </div>
    );
  }

  return (
    /*
     * Approved, signer unrecorded. The temptation is to read this as the operator, because in
     * practice it usually was — and that is precisely why it must not: a gate written before the
     * record named its signer cannot be retroactively claimed as a person's, and the one build that
     * signs this gate without a person is the one whose gates would go unattributed. Withhold.
     */
    <div className="banner warn" data-signer="unknown">
      <i className="ti ti-help-circle" aria-hidden="true" />
      <div className="prose">
        <b>Approved{when ? ` on ${when}` : ''} &mdash; the record does not say by whom.</b> It may
        have been the Regulatory Expert&rsquo;s determination and it may have been an automatic
        approval; the record cannot tell you which, and neither can the determinations below. Do not
        read it as a human ruling. Signing again attributes it, to you, now.
      </div>
    </div>
  );
}

/**
 * The signature, with the arming requirements ATTACHED to the button rather than floating above the
 * table in a card of their own.
 *
 * The requirements used to be a `Gate` panel with its own title, its own eyebrow and an arming
 * meter, sitting between the operator and the work — a second heading for one control. They are the
 * button's preconditions, so they are rendered as the button's preconditions.
 *
 * `armable` is the SERVER's answer and it is what enables the press. The checklist below is the
 * server's blockers, parsed, and it explains the answer — it does not compute it. That distinction
 * is the whole reason armability is computed in `RegulatoryGate.Armable` and not here: a tally
 * assembled in the browser can disagree with the endpoint that is about to refuse it, and the
 * direction it disagrees in is a live-looking button over an unarmed gate.
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
    /*
      On an approved gate this block sits directly under the panel reporting the signature it would
      REPLACE, and margin alone did not say they were two different things — the alarm and its
      remedy ran together as one surface. A hairline draws the boundary. On a locked gate there is
      no panel above, so there is nothing to divide and no rule is drawn.
    */
    <div
      style={{
        marginTop: approved ? 'var(--s4)' : 0,
        paddingTop: approved ? 'var(--s4)' : 0,
        borderTop: approved ? 'var(--hair) solid var(--border)' : undefined,
      }}
    >
      <p className="prose" style={{ margin: '0 0 var(--s3)' }}>
        {approved
          ? 'Signing records the Regulatory Expert’s determination under your name, and replaces the standing signature.'
          : 'A hard gate. Nothing is dosed, priced or ordered on a substance until the Regulatory Expert has ruled on it and you have recorded that ruling here.'}
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
            unreviewed > 0 && onOpenFirst
              ? { label: 'Open the first', onClick: onOpenFirst }
              : undefined
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
        {/*
          READ. What pressing the button DOES — the consequence the operator is signing for, and
          the last thing they read before signing. Amber stays stated inline: this sits in a plain
          flex row, not a banner, so there is no semantic colour for `.prose` to inherit.
        */}
        <span className="prose" style={{ color: 'var(--text-warning)' }}>
          {armable
            ? 'This releases the recommended substances to dosing, cost and procurement.'
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
        The state is CARRIED by the glyph, so the glyph is named rather than hidden. A tick and a
        cross that differ only in shape and colour say nothing to a screen reader and little to a
        colour-blind operator — and this is a checklist whose entire content is which items are
        outstanding.
      */}
      <i
        className={`ti ${met ? 'ti-check' : 'ti-x'}`}
        role="img"
        aria-label={met ? 'met' : 'not met'}
        style={{ color: met ? 'var(--text-muted)' : 'var(--text-warning)', marginTop: 2 }}
      />
      {/*
        READ, both lines. These are the hard gate's preconditions and the explanation of why one is
        not met — the exact copy an operator has to parse before signing — and they lived at the
        12px floor, the same size as a unit label.
      */}
      <span style={{ flex: 1, minWidth: 0 }}>
        {/*
          The met/unmet colour is a designed behaviour, not decoration: a met requirement recedes,
          an unmet one does not. `.prose` sets primary ink, so the muted case is restated inline —
          this is the one place prose is deliberately dimmed, and only ever when the check PASSES.
        */}
        <span className="prose" style={{ color: met ? 'var(--text-muted)' : 'var(--text-primary)' }}>
          {label}
        </span>
        {detail && (
          <span className="prose" style={{ display: 'block', color: 'var(--text-warning)' }}>
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
