import { useCallback, useEffect, useState } from 'react';
import { ApiError, NotFound, getDosing, reviewDosing } from '../../api/client';
import type { DosingCells, DosingDoc, MarkerCode, PpmWindow } from '../../api/types';
import { Loading } from '../../components/Loading';
import { LoadingEntryForm } from '../../components/LoadingEntryForm';
import { PpmChart } from '../../components/PpmChart';
import { RevisionTrail } from '../../components/RevisionControls';
import { Data } from '../../components/ui/Data';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { XrfEntry } from '../../components/xrf/XrfEntry';
import { handOffToChat } from '../../domain/chatHandoff';
import { byComponent, fmtLoading, fmtMass, fmtPpm, readDosing } from '../../domain/dosing';
import {
  AbsentCells,
  AmountValue,
  AvailabilityValue,
  BoundValue,
  ConfidenceCell,
  DosingWhyCell,
  DroppedRows,
  GroupBand,
  IdentityCell,
  TableError,
  boundConfidences,
  byComponentRows,
  useProjectTable,
  type PhaseGroup,
  type ReadRow,
} from './projectTable';
import type { ScreenProps } from '../ProjectLayout';

/**
 * State (the window and the dose), Why, Confidence, Amount, Availability.
 *
 * There is NO Sources column, and its absence is the honest reading rather than an omission: Dosing
 * carries no `Citation` objects at all (api/types.ts) — each bound's provenance is free prose in
 * `basis`, which is what the Why column renders. A Sources column here would be a row of dashes on
 * every project, which is exactly the chrome the prose purge is removing.
 *
 * Amount and Availability are what survived Cost's deletion (spec §6). They are not decoration: with
 * no prices to be had, supply is the only procurement signal in the product, and the VP's third
 * criterion is computed from it.
 */
const DOSING_SPAN = 5;

const GROUPS: PhaseGroup[] = [
  { group: 'identity', label: 'Material', span: 1 },
  { group: 'dosing', label: 'Dosing', span: DOSING_SPAN },
];

/**
 * What a DosingDoc says about its own trustworthiness.
 *
 * `provisional` HAS SHRUNK, and the new meaning is narrower and sharper than the old one. It used to
 * include "rests on the agent's proposal rather than the operator's ruling" — that is the NORMAL
 * basis now that the regulatory gate is gone (spec §16.4), so it stopped being an exception. What is
 * left is exactly one claim: A NUMBER NOBODY MEASURED IS UNDER THIS PPM — a default detection floor,
 * a missing metal loading, or no floor at all.
 *
 * It still blocks the order, and it must never go unrendered: a window resting on a declared default
 * looks identical to one resting on the physicist's measurement, number for number. A ppm with no
 * provenance mark is the dangerous version of this feature (spec §10).
 */
interface ProvisionalFacts {
  provisional: boolean;
  reasons: string[];
}

function provisionalFacts(doc: DosingDoc | null): ProvisionalFacts {
  const d = doc as unknown as Record<string, unknown> | null;
  const reasons = Array.isArray(d?.provisionalReasons)
    ? d!.provisionalReasons.filter((r): r is string => typeof r === 'string')
    : [];
  // An absent flag is NOT read as "not provisional" when there are reasons on the record: the loud
  // reading wins, exactly as it does everywhere else a fact about trust is missing.
  return { provisional: d?.provisional === true || reasons.length > 0, reasons };
}

/**
 * Dosing — how much of each marker goes in, in what ratio, per component, and what to order.
 *
 * Three artifacts of three different grains, which is why this screen is not one table (spec §5.4):
 *
 *  1. **The dosing column group** of the project table — one row per (component, CAS), carrying the
 *     ppm window, the recommendation, the order amount and supply.
 *  2. **The codes table.** A code is a SET of 2–3 markers identified by its ratio signature; its
 *     identity is the ratio, which no per-substance cell can express. The table may say whether a
 *     substance is IN a code; membership is not the code.
 *  3. **The ppm chart**, which encodes each bound's provenance by FORM rather than hue.
 *
 * And it carries the XRF entry form, because this is where the measurement it collects is consumed:
 * `DetectionFloor.Compute` needs the measured background and the device LODs. Background is an input,
 * not a phase (spec §8) — a stage with no agent and a pass-through filter was never a step the
 * operator walked, and the form belongs where the number it collects is used.
 *
 * Procurement acts on the numbers here, so three things must stay right:
 *
 *  - **The floor and the upper bound are not the same kind of claim.** The floor is measured, from the
 *    physicist's XRF data. The upper bound is the agent's own `regulatory` or `estimate` — it may never
 *    be "measured", because an agent that could stamp its own guess as a measurement would launder it
 *    into the one field the operator trusts absolutely.
 *  - **The recommended ppm is one scalar** strictly inside the window, never a band. A band would
 *    invent a tolerance nobody computed.
 *  - **What you buy is the compound mass, not the element mass.** They are different numbers, and
 *    reading the wrong one under-doses an oxide by its non-metal fraction.
 */
export function Dosing({ project, refreshProject }: ScreenProps) {
  const status = project.stages.dosing?.status;
  const { state, reload: reloadTable } = useProjectTable(project.projectId, status);

  const [doc, setDoc] = useState<DosingDoc | null>(null);
  const [docState, setDocState] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [docError, setDocError] = useState<string>();
  const [reviewBusy, setReviewBusy] = useState(false);
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [note, setNote] = useState('');

  const loadDoc = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getDosing(project.projectId);
        if (signal?.cancelled) return;
        if (res === NotFound) {
          setDoc(null);
          setDocState('absent');
        } else {
          setDoc(res);
          setDocState('ready');
        }
      } catch (err) {
        if (signal?.cancelled) return;
        setDocError(err instanceof Error ? err.message : String(err));
        setDocState('error');
      }
    },
    [project.projectId],
  );

  // The project poll is this screen's clock: `useProject` re-polls while dosing is pending/running,
  // so a status change is exactly when the record may hold a new doc.
  useEffect(() => {
    const signal = { cancelled: false };
    void loadDoc(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [loadDoc, status]);

  /**
   * An operator write that re-runs dosing — a confirmed XRF measurement, or a metal loading.
   *
   * Both flip the stage back to `pending` server-side, so BOTH reads have to be re-issued: the project
   * (which is the shell's and this screen's clock) and the table (whose dosing columns were computed
   * from the number that just changed). Refreshing only the project would leave the old windows on
   * screen under a stage that says it is running again.
   */
  const afterRerunTrigger = useCallback(() => {
    refreshProject();
    void reloadTable();
    void loadDoc();
  }, [refreshProject, reloadTable, loadDoc]);

  const recordReview = useCallback(async () => {
    const text = note.trim();
    if (!text) {
      setReviewError('The note is required — it is what was reviewed.');
      return;
    }
    setReviewBusy(true);
    setReviewError(null);
    try {
      const res = await reviewDosing(project.projectId, { note: text });
      if (res === NotFound) {
        setReviewError('No dosing record to review.');
        return;
      }
      setNote('');
      await loadDoc();
    } catch (err) {
      setReviewError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setReviewBusy(false);
    }
  }, [project.projectId, note, loadDoc]);

  if (state.kind === 'loading' && docState === 'loading') return <Loading what="the dosing record" />;

  const { windows, codes, droppedWindows, droppedCodes } = readDosing(doc);
  const facts = provisionalFacts(doc);

  return (
    <>
      {/*
        Provisional is a fact about the whole record, so it is stated once, first, and not per cell.
        Nothing here is wrong when it is set — the arithmetic is real — but a bound it rests on was
        never measured, and that is what blocks the order.
      */}
      {facts.provisional && (
        <section className="screen">
          <div className="banner warn" role="alert" data-provisional="true">
            <i className="ti ti-ruler-measure" aria-hidden="true" />
            {/*
              THIS ONE STAYS, and it is the shape §16.1 keeps: it names a real condition of THIS
              record — the reasons below are the backend's own named lines about these substances —
              and it says what that condition costs. It is not an explanation of the app.
            */}
            <div className="prose">
              <b>A number nobody measured is under these windows. Every order stays refused.</b>
              {facts.reasons.length > 0 && (
                <ul style={{ margin: 'var(--s2) 0 0', paddingLeft: 18 }}>
                  {facts.reasons.map((r) => (
                    <li key={r} className="small">
                      {r}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </div>
        </section>
      )}

      <section className="screen">
        <SectionHeader title="How much goes in" headingLevel={3} />

        {state.kind === 'error' && <TableError message={state.message} />}

        {state.kind === 'ready' && (
          <>
            <DroppedRows n={state.read.dropped} />
            {state.read.rows.length === 0 ? (
              <EmptyState icon="ti-flask" title="No rows to dose." />
            ) : (
              byComponentRows(state.read.rows).map(([componentId, rows]) => (
                <DosingComponent
                  key={componentId}
                  componentId={componentId}
                  rows={rows}
                  windows={windows.filter((w) => w.componentId === componentId)}
                />
              ))
            )}
          </>
        )}

        {droppedWindows > 0 && <Unreadable n={droppedWindows} what="ppm window" />}

        {docState === 'error' && (
          <div className="banner warn" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The dosing document could not be read</b> — the chart and the codes are missing.{' '}
              {docError}
            </div>
          </div>
        )}
      </section>

      <section className="screen">
        <SectionHeader title="What to order" headingLevel={3} />

        {droppedCodes > 0 && <Unreadable n={droppedCodes} what="code" />}

        {docState === 'absent' || codes.length === 0 ? (
          <EmptyState icon="ti-package-off" title="The record holds no readable code." />
        ) : (
          byComponent(codes).map(([componentId, componentCodes]) => (
            <div key={componentId} style={{ marginBottom: 'var(--s5)' }}>
              <SectionHeader
                eyebrow="Component"
                title={componentId}
                headingLevel={4}
                count={componentCodes.length}
              />
              <div style={{ display: 'grid', gap: 'var(--s3)' }}>
                {componentCodes.map((c) => (
                  <CodeCard key={c.ratioSignature} code={c} />
                ))}
              </div>
            </div>
          ))
        )}
      </section>

      {/*
        The physicist's measurement, entered where it is CONSUMED. This is the only door XRF data has
        into the record, and it is not a gate: a project with no XRF does not wait, it doses on the
        declared default floor and carries the estimate flag, which blocks the order rather than the
        pipeline.
      */}
      <section className="screen">
        <SectionHeader title="The physicist's XRF background" headingLevel={3} />
        <XrfEntry projectId={project.projectId} onConfirmed={afterRerunTrigger} />
      </section>

      {/*
        The metal loading — the one number in no catalog. Entering it re-runs dosing, so it belongs
        beside the windows it changes.
      */}
      <section className="screen">
        <SectionHeader title="A metal loading the record does not have" headingLevel={3} />
        <LoadingEntryForm projectId={project.projectId} onEntered={afterRerunTrigger} />
      </section>

      <section className="screen">
        <SectionHeader title="Recording the review" headingLevel={3} />

        {doc?.reviewedAt ? (
          <div className="region">
            <div className="small" style={{ fontWeight: 500 }}>
              <i className="ti ti-check" style={{ color: 'var(--text-success)' }} aria-hidden="true" />{' '}
              Reviewed {doc.reviewedAt.slice(0, 10)}
            </div>
            <p className="prose" style={{ margin: '6px 0 0' }}>
              {doc.reviewNote}
            </p>
          </div>
        ) : (
          <>
            <textarea
              value={note}
              onChange={(e) => setNote(e.target.value)}
              rows={2}
              aria-label="Dosing review note"
              placeholder="What was reviewed, and by whom. Required — the note is the record."
              disabled={reviewBusy || docState !== 'ready'}
              style={{
                width: '100%',
                maxWidth: 620,
                font: 'inherit',
                fontSize: 'var(--t-small)',
                padding: '6px 8px',
                border: '0.5px solid var(--border-strong)',
                borderRadius: 'var(--r1)',
                resize: 'vertical',
              }}
            />
            <div style={{ marginTop: 'var(--s2)' }}>
              <button
                type="button"
                className="btn"
                disabled={reviewBusy || note.trim().length === 0 || docState !== 'ready'}
                onClick={() => void recordReview()}
                title={docState === 'ready' ? undefined : 'There is no dosing record to review yet'}
              >
                <i className={`ti ${reviewBusy ? 'ti-loader' : 'ti-note'}`} aria-hidden="true" />{' '}
                {reviewBusy ? 'Recording…' : 'Mark review recorded'}
              </button>
            </div>
          </>
        )}

        {reviewError && (
          <div className="banner danger" role="alert" style={{ marginTop: 'var(--s3)' }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>{reviewError}</div>
          </div>
        )}
      </section>

      <RevisionTrail projectId={project.projectId} />
    </>
  );
}

/** One component's dosing: the chart (its answer) above the table (what the operator checks). */
function DosingComponent({
  componentId,
  rows,
  windows,
}: {
  componentId: string;
  rows: ReadRow[];
  windows: PpmWindow[];
}) {
  return (
    <div style={{ marginBottom: 'var(--s5)' }}>
      {/* Per-component tracks are architectural, not cosmetic: there is no product-wide marker, so
          there is no product-wide dose either. */}
      <SectionHeader eyebrow="Component" title={componentId} headingLevel={4} count={rows.length} />

      {/*
        The chart encodes provenance by FORM, never hue — a known end is a solid capped rule, an
        estimated end has no rule and the band dissolves. That conditional is load-bearing: while the
        fade was unconditional it drew a legal migration limit as vaguely as a guess.
      */}
      {windows.length > 0 && <PpmChart windows={windows} />}

      <table className="mx">
        <thead>
          <GroupBand groups={GROUPS} />
          <tr className="mx__cols">
            <th data-rowhead>Material</th>
            <th>State in this phase</th>
            <th>Why</th>
            <th>Confidence</th>
            <th>Amount</th>
            <th>Availability</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={`${row.componentId}|${row.cas}`}>
              <IdentityCell row={row} />
              {row.dosing.kind === 'cells' ? (
                <DosingRow cells={row.dosing.cells} />
              ) : (
                <AbsentCells state={row.dosing} span={DOSING_SPAN} phase="Dosing" />
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function DosingRow({ cells }: { cells: DosingCells }) {
  const recommended = cells.recommendedPpm;
  return (
    <>
      {/*
        THE STATE OF THIS ROW AFTER DOSING IS THE DOSE, so the recommended ppm leads and the window it
        sits inside follows it. Both ends keep their provenance word: a window printed as two bare
        numbers throws away which end somebody measured and which end an agent guessed.
      */}
      <td>
        <div style={{ fontWeight: 600 }}>
          {typeof recommended === 'number' && Number.isFinite(recommended) ? (
            <>
              <Data kind="ppm">{fmtPpm(recommended)}</Data> <span className="tiny muted">ppm</span>
            </>
          ) : (
            <span className="small" style={{ color: 'var(--text-danger)' }}>unreadable</span>
          )}
        </div>
        <div className="small secondary">
          <BoundValue bound={cells.floor} />
          <span className="muted"> – </span>
          <BoundValue bound={cells.upper} />
        </div>
      </td>
      <DosingWhyCell cells={cells} />
      {/* Two bounds, folded worst-wins. A measured floor under an estimated cap is only as good as
          the estimate — see domain/confidence.ts. */}
      <ConfidenceCell values={boundConfidences(cells)} expected={2} />
      <td>
        <AmountValue cells={cells} />
      </td>
      <td>
        <AvailabilityValue cells={cells} />
      </td>
    </>
  );
}

/**
 * A row the record held and this screen refused to draw.
 *
 * Said out loud, and not quietly skipped: an operator counting three codes on a screen that received
 * four is deciding against a record they cannot see.
 */
function Unreadable({ n, what }: { n: number; what: string }) {
  return (
    <div className="banner warn" role="alert" style={{ marginBottom: 'var(--s3)' }}>
      <i className="ti ti-alert-triangle" aria-hidden="true" />
      <div>
        {n} {what}
        {n === 1 ? '' : 's'} on the record could not be read and {n === 1 ? 'is' : 'are'} not shown
        here. Nothing was guessed in {n === 1 ? 'its' : 'their'} place — ask the agent to re-run dosing.
      </div>
    </div>
  );
}

function CodeCard({ code }: { code: MarkerCode }) {
  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
        {/* A code has no name and no kind — its identity IS the ratio, at the 2dp the domain chose. */}
        <Data kind="code">
          <span style={{ fontSize: 'var(--t-lead)', fontWeight: 600 }}>{code.ratioSignature}</span>
        </Data>
        <span className="small muted">{code.markers.length} markers</span>
      </div>

      <table className="mx">
        <thead>
          <tr>
            <th>Element</th>
            <th>ppm</th>
            <th>Loading</th>
            <th data-order="element">Element mass — into the batch</th>
            {/* Tinted and bold because this is the figure a purchase order is written from. */}
            <th data-order="compound" style={{ background: 'var(--surface-1)' }}>
              Compound mass — order this
            </th>
          </tr>
        </thead>
        <tbody>
          {code.markers.map((m) => (
            <tr key={m.cas}>
              <td>
                <Data kind="element">{m.element}</Data>
                <div className="tiny muted">
                  <Data kind="cas">{m.cas}</Data>
                </div>
              </td>
              <td>
                <Data kind="ppm">{fmtPpm(m.ppm)}</Data>
              </td>
              <td>
                <Data kind="num">{fmtLoading(m.metalLoading)}</Data>
              </td>
              {/* What must END UP in the batch. */}
              <td className="muted" data-order="element">
                <Data kind="num">{fmtMass(m.elementMassMg)}</Data> mg
              </td>
              {/* What you BUY. Heavier than the element mass by the compound's non-metal fraction —
                  ordering the element mass under-doses by exactly that. */}
              <td
                data-order="compound"
                style={{ fontWeight: 600, background: 'var(--surface-1)' }}
              >
                <Data kind="num">{fmtMass(m.compoundMassMg)}</Data> mg
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {/* The two mass columns carry the distinction themselves — the compound column is headed
          "order this" and is the tinted one. A paragraph repeating it under the table was the same
          fact told twice. */}
      <p className="prose" style={{ margin: '8px 0 0' }}>
        {code.rationale}
      </p>

      {/* No direct edits (Law 4). The operator never hand-mutates the agent's record: they tell the
          agent what is wrong and why, and the agent applies the change and records the reason as a
          Learned Conclusion. That conversation belongs in the agent column, not in a form in a card. */}
      <AskTheAgent about={`the ${code.ratioSignature} code on ${code.componentId}`} />
    </div>
  );
}

/**
 * Hand this card over to the agent column.
 *
 * The composer lives inside the agent panel, which this screen may not modify and which exposes no
 * handoff API — so the draft is written into it through the DOM, using React's own value setter so a
 * controlled input's state really changes rather than the value being painted on and lost at the next
 * render. It is a bridge, not an architecture.
 *
 * When the composer is not on the page — the agent panel is collapsible — the draft is shown here
 * instead. A button that silently did nothing would be a lying affordance.
 */
function AskTheAgent({ about }: { about: string }) {
  const [draft, setDraft] = useState<string | null>(null);
  const text = `About ${about} — `;

  return (
    <div style={{ marginTop: 'var(--s3)' }}>
      <button
        type="button"
        className="btn"
        onClick={() => {
          setDraft(handOffToChat(text) ? null : text);
        }}
      >
        <i className="ti ti-message-2" aria-hidden="true" /> Ask the agent to change this — say why
      </button>
      {draft && (
        <p className="small secondary" style={{ margin: '6px 0 0' }}>
          The agent column is closed. Open it and start with: <span className="data">{draft}</span>
        </p>
      )}
    </div>
  );
}
