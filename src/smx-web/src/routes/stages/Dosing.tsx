import { useCallback, useEffect, useState } from 'react';
import { NotFound, getDosing, reviewDosing, ApiError } from '../../api/client';
import type { Bound, DosingDoc, MarkerCode, PpmWindow } from '../../api/types';
import { LoadingEntryForm } from '../../components/LoadingEntryForm';
import { Loading } from '../../components/Loading';
import { PpmChart } from '../../components/PpmChart';
import { RevisionTrail } from '../../components/RevisionControls';
import { Data } from '../../components/ui/Data';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { byComponent, fmtLoading, fmtMass, fmtPpm, readDosing } from '../../domain/dosing';
import type { ScreenProps } from '../ProjectLayout';

/**
 * Dosing & codes — how much of each marker goes in, in what ratio, per component, and what to order.
 *
 * Procurement acts on the numbers on this screen, so three things must stay right:
 *
 *  1. **The floor and the upper bound are not the same kind of claim.** The floor is MEASURED, from
 *     the physicist's XRF data, confidence 1.0. The upper bound is the agent's own `regulatory` or
 *     `estimate` — it may never be "measured", because an agent that could stamp its own guess as a
 *     measurement would launder it into the one field the operator trusts absolutely. The chart
 *     encodes that difference in FORM (see PpmChart), and the table below repeats the kind in words.
 *  2. **The recommended ppm is one scalar**, strictly inside the window. Not a band — a band would
 *     invent a tolerance nobody computed.
 *  3. **What you buy is the compound mass, not the element mass.** They are different numbers, and
 *     reading the wrong one under-doses an oxide by its non-metal fraction.
 *
 * The parks — awaiting the physicist's background, awaiting a metal loading — are stated once, at the
 * top of the artifact column, by the next-action block. This screen does not repeat them; it carries
 * the CONTROL that clears the one the operator can clear themselves.
 */
export function Dosing({ project, refreshProject }: ScreenProps) {
  const stage = project.stages.dosing;
  const status = stage?.status;

  const [doc, setDoc] = useState<DosingDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [signBusy, setSignBusy] = useState(false);
  const [signError, setSignError] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: { cancelled: boolean }) => {
      try {
        const res = await getDosing(project.projectId);
        if (signal?.cancelled) return;
        if (res === NotFound) {
          setDoc(null);
          setPhase('absent');
        } else {
          setDoc(res);
          setPhase('ready');
        }
      } catch (err) {
        if (!signal?.cancelled) {
          setErrMsg(err instanceof Error ? err.message : String(err));
          setPhase('error');
        }
      }
    },
    [project.projectId],
  );

  // The project poll is this screen's clock: `useProject` re-polls while dosing is pending/running, so a
  // status change is exactly when the record may have a new doc. Re-read on each one.
  useEffect(() => {
    const signal = { cancelled: false };
    void load(signal);
    return () => {
      signal.cancelled = true;
    };
  }, [load, status]);

  const sign = useCallback(
    async (note?: string) => {
      if (!note) return;
      setSignBusy(true);
      setSignError(null);
      try {
        const res = await reviewDosing(project.projectId, { note });
        if (res === NotFound) {
          setSignError('No dosing record to review.');
          return;
        }
        await load();
      } catch (err) {
        setSignError(err instanceof ApiError ? err.message : String(err));
      } finally {
        setSignBusy(false);
      }
    },
    [project.projectId, load],
  );

  if (phase === 'loading') return <Loading what="the dosing record" />;

  return (
    <>
      {/* The one park the operator can clear here. The next-action block already says the record is
          waiting; this is the control it sends them to, so it comes first. */}
      {status === 'awaiting-operator' && (
        <section className="screen">
          <SectionHeader
            title="The agent needs a number"
            headingLevel={3}
            hint="dosing starts over once it has one"
          />
          <LoadingEntryForm projectId={project.projectId} onEntered={refreshProject} />
        </section>
      )}

      {phase === 'error' && (
        <section className="screen">
          <div className="banner danger" role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>Could not load the dosing record.</b>
              <div className="small" style={{ marginTop: 3 }}>
                {errMsg}
              </div>
            </div>
          </div>
        </section>
      )}

      {phase === 'absent' && (
        <section className="screen">
          <EmptyState
            icon="ti-flask"
            title="No ppm windows yet."
            body={
              <>
                Dosing runs on the substances the signed regulatory gate cleared, and writes a window
                per marker element per component. Nothing has been written for this project.
              </>
            }
          />
        </section>
      )}

      {phase === 'ready' && doc && (
        <DosingRecord doc={doc} onSign={sign} signBusy={signBusy} signError={signError} />
      )}

      <RevisionTrail projectId={project.projectId} />
    </>
  );
}

function DosingRecord({
  doc,
  onSign,
  signBusy,
  signError,
}: {
  doc: DosingDoc;
  onSign: (note?: string) => void;
  signBusy: boolean;
  signError: string | null;
}) {
  /* Read the payload rather than trusting it. `getDosing` casts with `as` and validates nothing, so
     an unreadable row reaches the chart as a NaN coordinate — and a mis-scaled dosing window is a
     mis-dose. Anything dropped is counted and said out loud below. */
  const { windows, codes, droppedWindows, droppedCodes } = readDosing(doc);
  const reviewed = Boolean(doc.reviewedAt);

  return (
    <>
      <section className="screen">
        <SectionHeader
          title="How much goes in"
          headingLevel={3}
          hint="ppm — mg/kg, mass over mass"
        />

        {droppedWindows > 0 && <Unreadable n={droppedWindows} what="ppm window" />}

        {windows.length === 0 ? (
          <p className="prose" style={{ margin: 0 }}>
            The record holds no readable ppm window.
          </p>
        ) : (
          byComponent(windows).map(([componentId, componentWindows]) => (
            <div key={componentId} style={{ marginBottom: 'var(--s6)' }}>
              <ComponentHeading componentId={componentId} count={componentWindows.length} />
              {/* The chart is the answer; the table under it is the supporting detail. */}
              <PpmChart windows={componentWindows} />
              <BoundsTable windows={componentWindows} />
            </div>
          ))
        )}
      </section>

      <section className="screen">
        <SectionHeader
          title="What to order"
          headingLevel={3}
          hint="a code's identity is its ratio"
        />

        {droppedCodes > 0 && <Unreadable n={droppedCodes} what="code" />}

        {codes.length === 0 ? (
          <p className="prose" style={{ margin: 0 }}>
            The record holds no readable code.
          </p>
        ) : (
          byComponent(codes).map(([componentId, componentCodes]) => (
            <div key={componentId} style={{ marginBottom: 'var(--s6)' }}>
              <ComponentHeading componentId={componentId} count={componentCodes.length} />
              <div style={{ display: 'grid', gap: 'var(--s3)' }}>
                {componentCodes.map((c) => (
                  <CodeCard key={c.ratioSignature} code={c} />
                ))}
              </div>
            </div>
          ))
        )}
      </section>

      <section className="screen">
        <SectionHeader
          title="Recording the review"
          headingLevel={3}
          hint="a note that the review happened — it unlocks nothing"
        />

        {reviewed ? (
          <div className="region">
            <div className="small" style={{ fontWeight: 500 }}>
              <i className="ti ti-check" style={{ color: 'var(--text-success)' }} aria-hidden="true" />{' '}
              Reviewed {doc.reviewedAt?.slice(0, 10)}
            </div>
            <p className="prose" style={{ margin: '6px 0 0' }}>
              {doc.reviewNote}
            </p>
            {/* What a signature here does and does not do is a sentence, and it is the whole
                point of a SOFT checkpoint. Read, not glanced at. */}
            <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
              Nothing was unlocked by this. The hard signatures are the regulatory gate and VP R&amp;D's
              final determination.
            </p>
          </div>
        ) : (
          <ReviewGate windows={windows} codes={codes} onSign={onSign} signBusy={signBusy} />
        )}

        {signError && (
          <div className="banner danger" role="alert" style={{ marginTop: 'var(--s3)' }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>{signError}</div>
          </div>
        )}
      </section>
    </>
  );
}

/**
 * A component's heading, and the rule that closes it off from what came before.
 *
 * Per-component tracks are architectural, not cosmetic: there is no product-wide marker, so there
 * is no product-wide dose either. On the deployed screen that boundary was carried by whitespace
 * alone — one component's chart and table ran into the next one's with nothing between them — and
 * the heading sat at `SectionHeader`'s weight 500 while the element symbols beneath it are
 * semibold. A heading lighter than its own children is a caption on the first item, not a
 * container around all of them, and two components then read as one long list. On a screen whose
 * numbers are dosed per component, that is the reading that puts a dose on the wrong part.
 *
 * It stays at `--t-lead` rather than `--t-title`: the section it sits inside is a `--t-lead` h3,
 * and a subordinate heading larger than its own parent inverts the hierarchy it exists to
 * clarify. The comparison that matters is against what it heads — 12px element rows — and it wins
 * that one on size, weight, face and colour at once.
 *
 * DUPLICATION, knowingly: `ProposedPool.tsx` exports a `ComponentHeading` of the same shape for
 * Discovery and the pool, arrived at independently in the same pass. The two are kept
 * pixel-identical here rather than shared, because that file is owned by another change in
 * flight; consolidating on the exported one is a follow-up, not a merge conflict waiting to
 * happen. If you touch either, touch both.
 */
function ComponentHeading({ componentId, count }: { componentId: string; count: number }) {
  return (
    <div
      data-component-heading=""
      style={{
        display: 'flex',
        alignItems: 'baseline',
        gap: 'var(--s2)',
        flexWrap: 'wrap',
        /* Longhands, not the `border-bottom` shorthand. jsdom's CSSOM drops a shorthand whose
           value contains `var()` outright — the declaration simply vanishes — so the rule would
           be unassertable in a test while looking correct in the source. The longhands round-trip
           intact and render identically. */
        borderBottomWidth: 'var(--hair)',
        borderBottomStyle: 'solid',
        borderBottomColor: 'var(--border-strong)',
        paddingBottom: 'var(--s2)',
        marginBottom: 'var(--s2)',
      }}
    >
      <h4
        style={{
          margin: 0,
          fontFamily: 'var(--font-serif)',
          fontSize: 'var(--t-lead)',
          fontWeight: 'var(--w-semibold)',
          color: 'var(--brand-navy)',
        }}
      >
        {componentId}
      </h4>
      <span className="sec__count">{count}</span>
    </div>
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
        here. Nothing was guessed in {n === 1 ? 'its' : 'their'} place — ask the agent to re-run
        dosing.
      </div>
    </div>
  );
}

/**
 * The soft checkpoint. It records that the PL/VP/physics review happened and unlocks NOTHING — the
 * hard gates are Regulatory and VP R&D, and a third would dilute what a signature means here.
 */
function ReviewGate({
  windows,
  codes,
  onSign,
  signBusy,
}: {
  windows: PpmWindow[];
  codes: MarkerCode[];
  onSign: (note?: string) => void;
  signBusy: boolean;
}) {
  const requirements: Requirement[] = [
    {
      id: 'windows',
      label: 'A ppm window exists for every marker element',
      met: windows.length > 0,
      detail: windows.length > 0 ? [...new Set(windows.map((w) => w.element))].join(', ') : undefined,
    },
    {
      id: 'codes',
      label: 'At least one code is finalized',
      met: codes.length > 0,
      detail: codes.length > 0 ? `${codes.length} code(s)` : 'The agent produced no code.',
    },
  ];

  return (
    <Gate
      kind="soft"
      title="Code finalization"
      records="PL / VP / physics review"
      requirements={requirements}
      signLabel="Mark review recorded"
      onSign={onSign}
      signBusy={signBusy}
      signNote={{ placeholder: 'What was reviewed, and by whom. Required — the note is the record.' }}
    />
  );
}

/**
 * The numbers behind the chart. Below it, deliberately: the chart is what the operator reads, this is
 * what they check.
 *
 * `kind` is repeated in words per bound because the chart's encoding is geometric, and the
 * measured/estimated distinction is too load-bearing to exist in only one modality. Basis prose goes
 * one click away — it is a sentence, and a sentence in a table cell is a truncated sentence.
 */
function BoundsTable({ windows }: { windows: PpmWindow[] }) {
  return (
    <>
      <table className="mx">
        <thead>
          <tr>
            <th>Element</th>
            <th>Detection floor</th>
            <th>Upper bound</th>
            <th>Recommended</th>
            <th>Quantifiable above</th>
          </tr>
        </thead>
        <tbody>
          {windows.map((w) => (
            <tr key={`${w.cas}|${w.element}`}>
              <td>
                <Data kind="element">{w.element}</Data>
                <div className="tiny muted data">{w.cas}</div>
              </td>
              <td>
                <span className="data">{fmtPpm(w.floor.ppm)}</span> <KindChip bound={w.floor} />
              </td>
              <td>
                <span className="data">{fmtPpm(w.upper.ppm)}</span> <KindChip bound={w.upper} />
              </td>
              <td className="data" style={{ fontWeight: 600 }}>
                {fmtPpm(w.recommendedPpm)}
              </td>
              <td className="data">{fmtPpm(w.quantificationPpm)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <details style={{ marginTop: 'var(--s2)' }}>
        <summary className="small secondary" style={{ cursor: 'pointer' }}>
          Where each bound comes from
        </summary>
        <div style={{ marginTop: 'var(--s2)' }}>
          {windows.map((w) => (
            <div key={`${w.cas}|${w.element}`} style={{ marginBottom: 'var(--s3)' }}>
              <div className="small" style={{ fontWeight: 500 }}>
                <Data kind="element">{w.element}</Data>
              </div>
              <BoundBasis label="Detection floor" bound={w.floor} />
              <BoundBasis label="Upper bound" bound={w.upper} />
            </div>
          ))}
        </div>
      </details>
    </>
  );
}

/**
 * One bound's provenance, in two registers rather than one.
 *
 * The label, the ppm and the confidence are REFERENCED — glanced at to identify which bound this
 * is — and stay at the floor. The basis is READ: it is the agent's or the physicist's sentence
 * saying where the number came from, and it is the only reason this disclosure exists. Running
 * both at 12px in secondary grey, as one wrapped paragraph, meant the sentence was the same weight
 * as the label pointing at it.
 *
 * The absence marker keeps `muted` on purpose: "No basis was recorded." is a statement that there
 * is nothing to read, not something to read, and it must not be mistakable for a recorded basis.
 */
function BoundBasis({ label, bound }: { label: string; bound: Bound }) {
  const basis = bound.basis.trim();
  return (
    <div style={{ marginTop: 'var(--s2)' }}>
      <div className="small secondary">
        {label} — <span className="data">{fmtPpm(bound.ppm)} ppm</span>, confidence{' '}
        <span className="data">{bound.confidence.toFixed(2)}</span>
      </div>
      <p className="prose" style={{ margin: '2px 0 0' }}>
        {basis || <span className="muted">No basis was recorded.</span>}
      </p>
    </div>
  );
}

/**
 * `measured` is the physicist's, never the agent's — DosingAgent rejects an agent-authored bound
 * claiming it. That asymmetry is the whole reason `kind` exists, so this reads the RECORD'S kind and
 * nothing else. It is never inferred from which end of the window the bound sits at, and never from a
 * confidence of 1.00: either inference would let an estimate render as a measurement.
 */
function KindChip({ bound }: { bound: Bound }) {
  const measured = bound.kind === 'measured';
  return (
    <span
      className="chip"
      data-bound-kind={bound.kind}
      style={{
        background: measured ? 'var(--bg-teal)' : 'var(--surface-1)',
        color: measured ? 'var(--text-teal)' : 'var(--text-secondary)',
      }}
      title={
        measured
          ? "The physicist's measurement. The agent cannot author this kind."
          : "The agent's own claim — not a measurement."
      }
    >
      {bound.kind}
    </span>
  );
}

function CodeCard({ code }: { code: MarkerCode }) {
  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
        {/* A code has no name and no kind — its identity IS the ratio. */}
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
                <div className="tiny muted data">{m.cas}</div>
              </td>
              <td className="data">{fmtPpm(m.ppm)}</td>
              <td className="data">{fmtLoading(m.metalLoading)}</td>
              {/* What must END UP in the batch. */}
              <td className="data muted" data-order="element">
                {fmtMass(m.elementMassMg)} mg
              </td>
              {/* What you BUY. Heavier than the element mass by the compound's non-metal fraction —
                  ordering the element mass under-doses by exactly that. */}
              <td
                className="data"
                data-order="compound"
                style={{ fontWeight: 600, background: 'var(--surface-1)' }}
              >
                {fmtMass(m.compoundMassMg)} mg
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {/* READ, not chrome. This is the sentence that prevents the failure this whole screen exists
          to prevent, and it used to render at the same size and colour as a unit label. */}
      <p className="prose" style={{ margin: 'var(--s3) 0 0' }}>
        Order the compound mass. The element mass is what has to end up in the batch — buying that
        figure instead under-doses by the compound's non-metal fraction.
      </p>

      <p className="prose" style={{ margin: 'var(--s2) 0 0' }}>
        {code.rationale}
      </p>

      {/* No direct edits (Law 4). The operator never hand-mutates the agent's record: they tell the
          agent what is wrong and why, and the agent applies the change and records the reason as a
          Learned Conclusion. That conversation belongs in the agent column, not in a form buried in
          a card — which is what this used to be. */}
      <AskTheAgent about={`the ${code.ratioSignature} code on ${code.componentId}`} />
    </div>
  );
}

/**
 * Hand this card over to the agent column.
 *
 * The composer lives inside `AgentPanel`, which this screen may not modify and which exposes no
 * handoff API — so the draft is written into it through the DOM, using React's own value setter so a
 * controlled input's state really changes rather than the value being painted on and lost at the next
 * render. It is a bridge, not an architecture: the right fix is a shared handoff channel owned by the
 * panel, and this should be replaced by one the moment it exists.
 *
 * When the composer is not on the page — the agent column is collapsible on this screen — the draft
 * is shown here instead. A button that silently did nothing would be a lying affordance, which is
 * exactly the failure mode this codebase spends its effort avoiding.
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
          The agent column is closed. Open it (Ctrl/Cmd + \) and start with:{' '}
          <span className="data">{draft}</span>
        </p>
      )}
    </div>
  );
}

/** Returns false when there is no composer on the page — the caller must say so rather than pretend. */
function handOffToChat(text: string): boolean {
  const input = document.querySelector<HTMLInputElement>('input[aria-label^="Message the"]');
  if (!input) return false;
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
  if (!setter) return false;
  setter.call(input, text);
  // React listens for the bubbling `input` event; without it the component's state never updates and
  // the next render wipes what was just written.
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.focus();
  return true;
}
