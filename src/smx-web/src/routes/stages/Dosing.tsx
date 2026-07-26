import { useCallback, useEffect, useState } from 'react';
import { NotFound, getDosing, reviewDosing, ApiError } from '../../api/client';
import type { Bound, DosingDoc, MarkerCode, PpmWindow } from '../../api/types';
import { LoadingEntryForm } from '../../components/LoadingEntryForm';
import { Loading } from '../../components/Loading';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Data } from '../../components/ui/Data';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { byComponent, fmtLoading, fmtMass, fmtPpm } from '../../domain/dosing';
import { axisMax, niceTicks } from '../../domain/ticks';
import type { ScreenProps } from '../ProjectLayout';

const CHART_W = 620;
const LABEL_W = 42;
const ROW_H = 40;
const PAD_TOP = 26;

/**
 * Dosing & codes (spec §4.5) — real.
 *
 * Three things this screen exists to get right:
 *
 *  1. **The floor and the upper bound are not the same kind of claim.** The floor is MEASURED, from the
 *     physicist's XRF data, confidence 1.0. The upper bound is the agent's own `regulatory` or `estimate`
 *     — it may never be "measured", because an agent that could stamp its own guess as a measurement would
 *     launder it into the one field the operator trusts absolutely. They render differently, and each shows
 *     its basis and confidence, because the estimate is known to run low.
 *  2. **The recommended ppm is one scalar**, strictly inside the window. Not a band — a band would invent
 *     a tolerance nobody computed.
 *  3. **What you buy is the compound mass, not the element mass.** They are different numbers, and reading
 *     the wrong one under-doses an oxide by its non-metal fraction.
 */
export function Dosing({ project, refreshProject }: ScreenProps) {
  const stage = project.stages.dosing;
  const status = stage?.status;

  const [doc, setDoc] = useState<DosingDoc | null>(null);
  const [phase, setPhase] = useState<'loading' | 'ready' | 'absent' | 'error'>('loading');
  const [errMsg, setErrMsg] = useState<string>();
  const [reviseNonce, setReviseNonce] = useState(0);
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
    <section className="screen">
      <div className="cap">
        <b>Dosing &amp; codes</b>
        ppm windows + code combinations, per component
      </div>

      <StageStatusCard name="Dosing agent" state={stage} />

      {/* The park the operator can clear themselves. The stage card above already carries the dispatcher's
          verbatim instruction; this is the control that acts on it. */}
      {status === 'awaiting-operator' && (
        <LoadingEntryForm projectId={project.projectId} onEntered={refreshProject} />
      )}

      {status === 'awaiting-physics' && (
        <EmptyState
          icon="ti-player-pause"
          title="Waiting on physics."
          body={
            <>
              Dosing needs a measured XRF background before it can compute a detection floor, and that comes
              from the physics team offline. There is nothing to enter here — the background arrives at
              intake, and dosing resumes on its own once it does.
            </>
          }
        />
      )}

      {phase === 'error' && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not load the dosing record.</b>
            <div style={{ marginTop: 3 }}>{errMsg}</div>
          </div>
        </div>
      )}

      {phase === 'absent' && status !== 'awaiting-operator' && status !== 'awaiting-physics' && (
        <EmptyState
          icon="ti-flask"
          title="No ppm windows yet."
          body={
            <>
              Dosing runs once the regulatory gate is signed — it doses only the substances the operator
              recommended, so it waits for that signature rather than guessing at the compliant set.
            </>
          }
        />
      )}

      {phase === 'ready' && doc && (
        <DosingRecord
          doc={doc}
          projectId={project.projectId}
          onSign={sign}
          signBusy={signBusy}
          signError={signError}
          onRevised={() => setReviseNonce((n) => n + 1)}
        />
      )}

      <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
    </section>
  );
}

function DosingRecord({
  doc,
  projectId,
  onSign,
  signBusy,
  signError,
  onRevised,
}: {
  doc: DosingDoc;
  projectId: string;
  onSign: (note?: string) => void;
  signBusy: boolean;
  signError: string | null;
  onRevised: () => void;
}) {
  const reviewed = Boolean(doc.reviewedAt);

  // A soft checkpoint: it records that the PL/VP/physics review happened and unlocks NOTHING. The hard
  // gates are Regulatory and VP; a third would dilute what a signature means.
  const requirements: Requirement[] = [
    {
      id: 'windows',
      label: 'A ppm window exists for every marker element',
      met: doc.windows.length > 0,
      detail: doc.windows.length > 0 ? [...new Set(doc.windows.map((w) => w.element))].join(', ') : undefined,
    },
    {
      id: 'codes',
      label: 'At least one code is finalized',
      met: doc.codes.length > 0,
      detail: doc.codes.length > 0 ? `${doc.codes.length} code(s)` : 'The agent produced no code.',
    },
  ];

  return (
    <>
      {reviewed ? (
        <div className="region" style={{ marginBottom: 'var(--s4)' }}>
          <div style={{ fontSize: 13, fontWeight: 500 }}>
            <i className="ti ti-check" style={{ color: 'var(--text-success)' }} aria-hidden="true" /> Code
            finalization reviewed
          </div>
          <div className="tiny muted" style={{ marginTop: 3 }}>
            {doc.reviewedAt?.slice(0, 10)} — a record that the review happened. It gates nothing.
          </div>
          <p className="small" style={{ margin: '6px 0 0' }}>
            {doc.reviewNote}
          </p>
        </div>
      ) : (
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
      )}

      {signError && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>{signError}</div>
        </div>
      )}

      {byComponent(doc.windows).map(([componentId, windows]) => (
        <div key={componentId} style={{ marginBottom: 'var(--s5)' }}>
          <SectionHeader
            eyebrow="Component"
            title={componentId}
            count={windows.length}
            hint="ppm windows — mg/kg, mass over mass"
          />
          <PpmChart windows={windows} />
          {windows.map((w) => (
            <BoundsDetail key={`${w.cas}|${w.element}`} window={w} />
          ))}
        </div>
      ))}

      {byComponent(doc.codes).map(([componentId, codes]) => (
        <div key={componentId} style={{ marginBottom: 'var(--s5)' }}>
          <SectionHeader
            eyebrow="Codes"
            title={componentId}
            count={codes.length}
            hint="a code's identity is its ratio"
          />
          <div style={{ display: 'grid', gap: 'var(--s3)' }}>
            {codes.map((c) => (
              <CodeCard key={c.ratioSignature} code={c} projectId={projectId} onRevised={onRevised} />
            ))}
          </div>
        </div>
      ))}
    </>
  );
}

/**
 * The window geometry. The chart shows SHAPE; provenance is prose and lives in BoundsDetail below it —
 * a basis is a sentence, and cramming it into an SVG label would just truncate it.
 */
function PpmChart({ windows }: { windows: PpmWindow[] }) {
  const rawMax = Math.max(...windows.map((w) => w.upper.ppm));
  const max = axisMax(rawMax * 1.05);
  const ticks = niceTicks(rawMax * 1.05);
  const scale = (v: number) => LABEL_W + (v / max) * (CHART_W - LABEL_W - 12);
  const height = PAD_TOP + windows.length * ROW_H + 20;

  return (
    <svg
      viewBox={`0 0 ${CHART_W} ${height}`}
      width="100%"
      role="img"
      aria-label="Dosable ppm window per marker element"
      style={{ marginBottom: 10 }}
    >
      <title>Dosable ppm window per marker element</title>

      {ticks.map((t) => (
        <g key={t}>
          <line
            x1={scale(t)}
            y1={PAD_TOP - 6}
            x2={scale(t)}
            y2={PAD_TOP + windows.length * ROW_H}
            stroke="var(--border)"
            strokeWidth="0.5"
          />
          <text
            x={scale(t)}
            y={PAD_TOP - 11}
            fontSize="9"
            fill="var(--text-muted)"
            textAnchor="middle"
            fontFamily="var(--font-mono)"
          >
            {t}
          </text>
        </g>
      ))}
      <text x={CHART_W - 4} y={height - 4} fontSize="9" fill="var(--text-muted)" textAnchor="end">
        ppm
      </text>

      {windows.map((w, i) => {
        const y = PAD_TOP + i * ROW_H + 6;
        return (
          <g key={`${w.cas}|${w.element}`}>
            <text
              x={0}
              y={y + 10}
              fontSize="11"
              fill="var(--text-primary)"
              fontWeight="500"
              fontFamily="var(--font-mono)"
            >
              {w.element}
            </text>

            {/* The dosable range: floor -> upper. */}
            <rect
              x={scale(w.floor.ppm)}
              y={y}
              width={Math.max(0, scale(w.upper.ppm) - scale(w.floor.ppm))}
              height={14}
              rx={3}
              fill="var(--surface-1)"
            />

            {/* Detection floor — MEASURED. Solid and hard, because it is the one bound that is not a claim. */}
            <line
              x1={scale(w.floor.ppm)}
              y1={y - 3}
              x2={scale(w.floor.ppm)}
              y2={y + 17}
              stroke="var(--text-danger)"
              strokeWidth="1.5"
            />

            {/* Quantification — detectable below this, but not measurable. */}
            <line
              x1={scale(w.quantificationPpm)}
              y1={y}
              x2={scale(w.quantificationPpm)}
              y2={y + 14}
              stroke="var(--text-warning)"
              strokeWidth="1"
              strokeDasharray="2 2"
            />

            {/* Upper bound — the AGENT's claim. Dashed: it is asserted, not measured. */}
            <line
              x1={scale(w.upper.ppm)}
              y1={y - 3}
              x2={scale(w.upper.ppm)}
              y2={y + 17}
              stroke="var(--text-muted)"
              strokeWidth="1.5"
              strokeDasharray="3 2"
            />

            {/* The recommendation is ONE value, so it is one mark. */}
            <circle cx={scale(w.recommendedPpm)} cy={y + 7} r={4} fill="var(--text-success)" />
            <text
              x={scale(w.recommendedPpm)}
              y={y - 1}
              fontSize="9"
              fill="var(--text-success)"
              textAnchor="middle"
              fontFamily="var(--font-mono)"
            >
              {fmtPpm(w.recommendedPpm)}
            </text>

            <text
              x={scale(w.floor.ppm)}
              y={y + 27}
              fontSize="8"
              fill="var(--text-danger)"
              textAnchor="middle"
              fontFamily="var(--font-mono)"
            >
              {fmtPpm(w.floor.ppm)}
            </text>
            <text
              x={scale(w.upper.ppm)}
              y={y + 27}
              fontSize="8"
              fill="var(--text-muted)"
              textAnchor="middle"
              fontFamily="var(--font-mono)"
            >
              {fmtPpm(w.upper.ppm)}
            </text>
          </g>
        );
      })}
    </svg>
  );
}

/** Spec §4.5's requirement, finally satisfiable: basis + confidence per bound, and they read differently. */
function BoundsDetail({ window: w }: { window: PpmWindow }) {
  return (
    <div className="region" style={{ marginBottom: 'var(--s2)' }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 6 }}>
        <Data kind="element">
          <b>{w.element}</b>
        </Data>
        <span className="tiny muted data">{w.cas}</span>
        <span className="tiny muted" style={{ marginLeft: 'auto' }}>
          recommended <b className="data">{fmtPpm(w.recommendedPpm)}</b> ppm · quantifiable above{' '}
          <b className="data">{fmtPpm(w.quantificationPpm)}</b>
        </span>
      </div>
      <BoundRow label="Detection floor" bound={w.floor} />
      <BoundRow label="Upper bound" bound={w.upper} />
    </div>
  );
}

function BoundRow({ label, bound }: { label: string; bound: Bound }) {
  // "measured" is the physicist's, never the agent's. That distinction is the whole reason `kind` exists,
  // so it is the most prominent thing about the row after the number itself.
  const measured = bound.kind === 'measured';
  return (
    <div style={{ display: 'flex', gap: 8, padding: '4px 0', alignItems: 'baseline' }}>
      <span className="tiny muted" style={{ minWidth: 88 }}>
        {label}
      </span>
      <span className="data" style={{ minWidth: 62, fontWeight: 500 }}>
        {fmtPpm(bound.ppm)} ppm
      </span>
      <span
        className="chip"
        style={{
          fontSize: 10,
          background: measured ? 'var(--bg-teal)' : 'var(--bg-warning)',
          color: measured ? 'var(--text-teal)' : 'var(--text-warning)',
        }}
        title={
          measured
            ? "The physicist's measurement. The agent cannot author this kind."
            : "The agent's own claim — not a measurement."
        }
      >
        {bound.kind}
      </span>
      <span className="tiny muted data">conf {bound.confidence.toFixed(2)}</span>
      <span className="tiny muted" style={{ flex: 1 }}>
        {bound.basis}
      </span>
    </div>
  );
}

function CodeCard({
  code,
  projectId,
  onRevised,
}: {
  code: MarkerCode;
  projectId: string;
  onRevised: () => void;
}) {
  return (
    <div className="card">
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8, flexWrap: 'wrap' }}>
        {/* A code has no name and no kind — its identity IS the ratio. */}
        <Data kind="code">
          <span style={{ fontSize: 15, fontWeight: 600 }}>{code.ratioSignature}</span>
        </Data>
        <span className="tiny muted">{code.markers.length} markers</span>
      </div>

      <table className="mx" style={{ marginBottom: 8 }}>
        <thead>
          <tr>
            <th>Element</th>
            <th>ppm</th>
            <th>Loading</th>
            <th>Element mass</th>
            <th>Compound mass — order this</th>
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
              <td className="data muted">{fmtMass(m.elementMassMg)} mg</td>
              {/* What you BUY. Heavier than the element mass by the compound's non-metal fraction —
                  ordering the element mass under-doses by exactly that. */}
              <td className="data" style={{ fontWeight: 600 }}>
                {fmtMass(m.compoundMassMg)} mg
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <p className="tiny muted" style={{ margin: '0 0 8px' }}>
        {code.rationale}
      </p>

      {/* No direct edits (Law 4). A dosing revision does NOT void the regulatory gate — dosing is
          downstream of the signature and consumes the already-signed compliant set. */}
      <ReviseForm
        projectId={projectId}
        stage="dosing"
        fixedTarget={`the ${code.ratioSignature} code on ${code.componentId}`}
        onRequested={onRevised}
      />
    </div>
  );
}
