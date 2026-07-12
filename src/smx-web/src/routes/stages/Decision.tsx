import { Fragment, useState } from 'react';
import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { MockBadge } from '../../components/MockBadge';
import { Gate, type Requirement } from '../../components/ui/Gate';
import decision from '../../mocks/fixtures/decision.json';
import msds from '../../mocks/fixtures/msds-registry.json';

interface Clears {
  xrf: boolean;
  compatibility: boolean;
  regulatory: boolean;
  availability: boolean;
}
interface Row {
  component: string;
  code: string;
  ppm: number;
  clears: Clears;
}

const CRITERIA: (keyof Clears)[] = ['xrf', 'compatibility', 'regulatory', 'availability'];

/**
 * Spec §4.7 requires every row be traceable end-to-end. Each criterion is owned by a
 * stage, so "trace" is a link to that stage — not a dead button.
 */
const OWNER: Record<keyof Clears, { stage: string; label: string }> = {
  xrf: { stage: 'background', label: 'Background analysis' },
  compatibility: { stage: 'matrix', label: 'Compatibility matrix' },
  regulatory: { stage: 'regulatory', label: 'Regulatory gate' },
  availability: { stage: 'cost', label: 'Cost & availability' },
};

/**
 * Decision matrix + VP R&D gate (spec §4.7) — the final hard gate.
 *
 * VP approval releases procurement and writes to the Marker Library and Learned
 * Conclusions, so this is the highest-consequence action in the system. It is an
 * operator-signed record with no endpoint, so the control is disabled — and the
 * MSDS-before-order precondition (spec §5) is surfaced here, where the order is
 * actually decided.
 */
export function Decision({ project }: { project: ProjectSummary }) {
  const { rows } = decision as { rows: Row[] };
  const { entries } = msds as { entries: { substance: string; status: string }[] };
  const [expanded, setExpanded] = useState<string | null>(null);

  const blocking = rows.filter((r) => CRITERIA.some((c) => !r.clears[c]));
  const cleared = rows.length - blocking.length;

  // The MSDS-before-order precondition is a hard gate in its own right (spec §5).
  // Both sources are fixtures, so joining them here is honest.
  const staleMsds = entries.filter((e) => e.status !== 'current');

  const requirements: Requirement[] = [
    {
      id: 'components',
      label: 'Every component clears every criterion',
      met: blocking.length === 0,
      detail:
        blocking.length > 0 ? (
          <>
            {cleared} of {rows.length} cleared. Blocking:{' '}
            {blocking
              .map(
                (r) =>
                  `${r.component} (${CRITERIA.filter((c) => !r.clears[c]).join(', ')})`,
              )
              .join(' · ')}
          </>
        ) : (
          <>All {rows.length} components cleared.</>
        ),
    },
    {
      id: 'msds',
      label: 'A current MSDS exists for every substance to be ordered',
      met: staleMsds.length === 0,
      detail:
        staleMsds.length > 0 ? (
          <>
            {staleMsds.map((e) => `${e.substance} (${e.status})`).join(' · ')} — procurement is
            blocked until these are current.
          </>
        ) : undefined,
    },
    {
      id: 'vp',
      // Never met, and it must never look met.
      label: 'VP R&D determination recorded',
      met: false,
      detail: <>No endpoint exists to record one. This gate cannot arm.</>,
    },
  ];

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Decision matrix</b> &nbsp;·&nbsp; spec §4.7 — final code + ppm per component, then the VP
        R&amp;D gate
      </div>

      <Gate
        kind="hard"
        title="VP R&D gate"
        records="releases procurement · writes the Marker Library + Learned Conclusions"
        requirements={requirements}
        signLabel="Approve & close project"
        rejectLabel="Reject (requires a reason)"
      />

      <MockBadge note="No decision agent has run. These codes, ppm values and clearances are illustrative." />

      <table className="mx">
        <thead>
          <tr>
            <th>Component</th>
            <th>Final code</th>
            <th>ppm</th>
            {CRITERIA.map((c) => (
              <th key={c} style={{ textAlign: 'center', textTransform: 'capitalize' }}>
                {c}
              </th>
            ))}
            <th style={{ width: 60 }}>Trace</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const isOpen = expanded === r.component;
            const fails = CRITERIA.filter((c) => !r.clears[c]);
            return (
              <Fragment key={r.component}>
                <tr style={isOpen ? { background: 'var(--surface-2)' } : undefined}>
                  <td style={{ fontWeight: 500 }}>{r.component}</td>
                  <td>
                    {/* A code is not a Conditional verdict — purple here would say so. */}
                    <span className="chip chip--neutral chip--mono">{r.code}</span>
                  </td>
                  <td className="secondary" style={{ fontVariantNumeric: 'tabular-nums' }}>
                    {r.ppm}
                  </td>
                  {CRITERIA.map((c) => (
                    <td key={c} style={{ textAlign: 'center' }}>
                      <span
                        className={`chip ${r.clears[c] ? 'v' : 'x'}`}
                        title={`${c} — ${r.clears[c] ? 'clear' : 'blocking'} (owned by ${OWNER[c].label})`}
                      >
                        {r.clears[c] ? '✓' : '✕'}
                      </span>
                    </td>
                  ))}
                  <td>
                    <button
                      className="btn"
                      onClick={() => setExpanded(isOpen ? null : r.component)}
                      aria-expanded={isOpen}
                    >
                      {isOpen ? 'Hide' : 'View'}
                    </button>
                  </td>
                </tr>
                {isOpen && (
                  <tr>
                    <td colSpan={7} style={{ padding: 0, background: 'var(--surface-2)' }}>
                      <div
                        style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}
                      >
                        <div className="tiny muted" style={{ marginBottom: 6 }}>
                          Each criterion is owned by the stage that produced it — follow it to its
                          source.
                        </div>
                        {CRITERIA.map((c) => (
                          <div className="step" key={c}>
                            <i
                              className={`ti ${r.clears[c] ? 'ti-check' : 'ti-x'}`}
                              aria-hidden="true"
                              style={{
                                color: r.clears[c] ? 'var(--text-success)' : 'var(--text-danger)',
                                marginTop: 2,
                              }}
                            />
                            <div>
                              <span style={{ textTransform: 'capitalize' }}>{c}</span> —{' '}
                              {r.clears[c] ? 'clear' : <b>blocking</b>}{' '}
                              <Link to={`/p/${project.projectId}/${OWNER[c].stage}`}>
                                {OWNER[c].label} <i className="ti ti-arrow-right" aria-hidden="true" />
                              </Link>
                            </div>
                          </div>
                        ))}
                        {fails.length === 0 && (
                          <div className="tiny muted" style={{ marginTop: 6 }}>
                            Cleared on all four criteria. Cleared is not approved — the VP gate is
                            still the only thing that releases procurement.
                          </div>
                        )}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
        <tfoot>
          <tr>
            <td className="tiny muted" colSpan={3}>
              per criterion
            </td>
            {CRITERIA.map((c) => {
              const n = rows.filter((r) => r.clears[c]).length;
              const all = n === rows.length;
              return (
                <td
                  key={c}
                  className="tiny"
                  style={{
                    textAlign: 'center',
                    color: all ? 'var(--text-muted)' : 'var(--text-danger)',
                    fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {n} of {rows.length}
                </td>
              );
            })}
            <td />
          </tr>
        </tfoot>
      </table>
    </section>
  );
}
