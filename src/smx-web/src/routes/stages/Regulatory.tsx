import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  NotFound,
  approveRegulatory,
  getMatrix,
  getRegulatoryGate,
} from '../../api/client';
import type { MatrixDoc, ProjectSummary, RegulatoryGate } from '../../api/types';
import { EvidencePanel } from '../../components/EvidencePanel';
import { ReviseForm, RevisionTrail } from '../../components/RevisionControls';
import { Loading } from '../../components/Loading';
import { StageStatusCard } from '../../components/StageStatusCard';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { EmptyState, SectionHeader } from '../../components/ui/Primitives';
import { verdictClass } from '../../domain/matrix';
import { cellBlockerKey, parseBlocker, type CellBlocker } from '../../domain/gate';
import { operatorRuling, reviewStance } from '../../domain/proposal';

const cellKey = (cas: string, componentId: string) => `${cas}|${componentId}`;

/**
 * Regulatory gate (spec §4.4) — a HARD gate, now real.
 *
 * This screen used to render a fixture behind a MockBadge with a hard-coded "no endpoint" requirement.
 * Every part of it is now a real endpoint: `GET /gate/regulatory` computes armability server-side and
 * lists the exact blockers, `GET /matrix` carries each cell's verdict + the operator's determination, and
 * the sign button POSTs `/regulatory/approve`.
 *
 * The gate arms on SERVER truth: the button is enabled only when the server says `armable`, never on a
 * browser-side tally, and the backend re-checks on approve — so a concurrent revise can still refuse it,
 * in which case we re-read the gate to show the fresh blockers.
 */
export function Regulatory({ project }: { project: ProjectSummary }) {
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
    } catch (err) {
      // The server re-checks armability; a concurrent revise can refuse a button that looked live.
      setSignError(err instanceof ApiError ? err.message : String(err));
      await reload(); // refresh the blockers so the operator sees why
    } finally {
      setSignBusy(false);
    }
  }, [project.projectId, reload]);

  if (phase === 'loading') return <Loading what="the regulatory gate" />;

  if (phase === 'error') {
    return (
      <section className="screen">
        <Head />
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>Could not load the regulatory gate.</b>
            <div style={{ marginTop: 3 }}>{loadError}</div>
          </div>
        </div>
      </section>
    );
  }

  if (phase === 'unassembled' || !doc) {
    return (
      <section className="screen">
        <Head />
        <StageStatusCard name="Regulatory agent" state={project.stages.regulatory} />
        <EmptyState
          icon="ti-gavel"
          title="No verdicts to rule on yet."
          body={
            <>
              The screening agents have not produced a compatibility matrix, so there is nothing to sign
              off. Regulatory sign-off opens once discovery and screening complete.
            </>
          }
        />
      </section>
    );
  }

  const g = gate!;
  const approved = g.status === 'approved';
  const parsed = g.blockers.map(parseBlocker);
  const cellBlockers = parsed.filter((b): b is CellBlocker => b.kind === 'cell');
  const incomplete = parsed.some((b) => b.kind === 'message');

  const requirements: Requirement[] = [
    {
      id: 'complete',
      label: 'Every candidate has a verdict',
      met: !incomplete,
      detail: incomplete ? 'Screening is still running — not every cell has been screened.' : undefined,
    },
    {
      id: 'reviewed',
      label: 'Every flagged verdict reviewed',
      met: cellBlockers.length === 0,
      detail:
        cellBlockers.length > 0
          ? `${cellBlockers.length} unreviewed: ${cellBlockers
              .map((b) => `${b.cas}|${b.componentId} (${b.overall})`)
              .join(', ')}`
          : 'All flagged verdicts have been reviewed.',
      action:
        cellBlockers.length > 0
          ? { label: 'Open first', onClick: () => openCell(cellBlockerKey(cellBlockers[0])) }
          : undefined,
    },
  ];

  const substanceOf = (cas: string) => doc.rows.find((r) => r.cas === cas);

  return (
    <section className="screen">
      <Head synced={g.approvedAt} />

      {approved ? (
        <div className="banner" style={{ background: 'var(--bg-teal)', borderColor: 'var(--border-teal)', color: 'var(--text-teal)' }}>
          <i className="ti ti-writing-sign" aria-hidden="true" />
          <div>
            <b>Regulatory gate signed.</b>
            {g.approvedAt && <> Recorded {g.approvedAt.slice(0, 10)}.</>} Procurement is unblocked for the
            recommended substances.
          </div>
        </div>
      ) : (
        <Gate
          kind="hard"
          title="Regulatory gate"
          records="records the R.E.'s offline determination"
          requirements={requirements}
          signLabel="Sign the R.E. determination"
          onSign={sign}
          signBusy={signBusy}
        />
      )}

      {signError && (
        <div className="banner danger">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>
            <b>The gate was not signed.</b>
            <div style={{ marginTop: 3 }}>{signError}</div>
          </div>
        </div>
      )}

      <SectionHeader
        eyebrow="Verdicts"
        count={doc.cells.length}
        hint="each substance × component — rule on it, then the gate can arm"
      />

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
          {doc.cells.map((cell) => {
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
                  <td>
                    <span
                      className="tiny"
                      style={{ color: cell.evidenceReviewed ? 'var(--text-success)' : 'var(--text-warning)' }}
                    >
                      <i
                        className={`ti ${cell.evidenceReviewed ? 'ti-eye-check' : 'ti-eye-exclamation'}`}
                        aria-hidden="true"
                      />{' '}
                      {cell.evidenceReviewed ? 'reviewed' : 'not reviewed'}
                    </span>
                  </td>
                  <td>
                    {signedRuling ? (
                      <span
                        className="tiny"
                        style={{ color: 'var(--text-teal)', fontWeight: 600, fontFamily: 'var(--font-mono)' }}
                      >
                        {signedRuling.determination}
                        {stance === 'overridden' && (
                          <span className="muted"> (overrode agent)</span>
                        )}
                      </span>
                    ) : (
                      <span className="tiny muted">unsigned</span>
                    )}
                  </td>
                  <td>
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
                      <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
                        <EvidencePanel
                          projectId={project.projectId}
                          cell={cell}
                          substance={sub}
                          onClose={() => setOpenKey(null)}
                          onWrote={() => reload()}
                        />
                        {/* No direct edits: to change a verdict, tell the agent why (spec §1.5). */}
                        <div style={{ marginTop: 'var(--s3)' }}>
                          <ReviseForm
                            projectId={project.projectId}
                            stage="regulatory"
                            fixedTarget={`${sub ? `${sub.element} ${sub.form}` : cell.cas} on ${cell.componentId}`}
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

      <RevisionTrail projectId={project.projectId} refreshKey={reviseNonce} />
    </section>
  );
}

function Head({ synced }: { synced?: string }) {
  return (
    <div className="cap">
      <b>Regulatory gate</b>
      Hard gate, the R.E.&rsquo;s sign-off
      {synced && <span className="muted"> · signed {synced.slice(0, 10)}</span>}
    </div>
  );
}
