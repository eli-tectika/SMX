import { Fragment, useMemo, useRef, useState } from 'react';
import { MockBadge } from '../../components/MockBadge';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { CitationChip } from '../../components/ui/Primitives';
import regulatory from '../../mocks/fixtures/regulatory.json';

interface Check {
  check: string;
  result: string;
  source: string;
  reference: string;
  retrievedAt: string;
}
interface Substance {
  element: string;
  form: string;
  cas: string;
  elementGate: string;
  application: string;
  conclusion: string;
  reviewed: boolean;
  evidence: { elementGate: Check[]; application: Check[]; hazard: Check[] };
}

const CLASS: Record<string, string> = { Pass: 'v', Conditional: 'l', NeedsReview: 'n', Fail: 'x' };

/** A corpus older than this is stale enough to say so. Silent when fresh — noise otherwise. */
const CORPUS_MAX_AGE_DAYS = 90;

const daysBetween = (a: string, b: string) =>
  Math.round((Date.parse(a) - Date.parse(b)) / 86_400_000);

/**
 * Regulatory gate (spec §4.4) — a HARD gate.
 *
 * The gate is the subject of this screen, at the top, not a banner at the bottom.
 * Spec §1.8 says it must not arm until the agent's flagged items have been opened,
 * so it enumerates exactly which substances are unopened and links to each one.
 * Making the remaining work concrete and reachable is what makes rubber-stamping hard.
 *
 * The sign control stays disabled regardless of arming: a gate is an operator-signed
 * record and no endpoint exists to sign one.
 */
export function Regulatory() {
  const { corpusSyncedAt, substances } = regulatory as {
    corpusSyncedAt: string;
    substances: Substance[];
  };

  const [open, setOpen] = useState<string | null>(null);
  /** Opened here, in this browser, in this session. Nothing self-marks. */
  const [opened, setOpened] = useState<Set<string>>(new Set());
  const rowRefs = useRef<Record<string, HTMLTableRowElement | null>>({});

  const openRow = (cas: string) => {
    setOpen(cas);
    setOpened((prev) => new Set(prev).add(cas));
    requestAnimationFrame(() =>
      rowRefs.current[cas]?.scrollIntoView({ block: 'center', behavior: 'smooth' }),
    );
  };

  const unopened = substances.filter((s) => !opened.has(s.cas));
  const flaggedUnopened = substances.filter(
    (s) => !opened.has(s.cas) && (s.elementGate !== 'Pass' || s.application !== 'Pass'),
  );

  // The fixture has no clock, so age is measured against the newest citation in it
  // rather than "today" — inventing a live date would make this look like real
  // freshness telemetry, which it is not.
  const newestCitation = useMemo(
    () =>
      substances
        .flatMap((s) => [...s.evidence.elementGate, ...s.evidence.application, ...s.evidence.hazard])
        .map((c) => c.retrievedAt)
        .sort()
        .at(-1) ?? corpusSyncedAt,
    [substances, corpusSyncedAt],
  );
  const corpusAge = Math.abs(daysBetween(newestCitation, corpusSyncedAt));

  const requirements: Requirement[] = [
    {
      id: 'opened',
      label: `Every substance's evidence opened`,
      met: unopened.length === 0,
      detail:
        unopened.length > 0 ? (
          <>
            {unopened.length} not yet opened — {unopened.map((s) => `${s.element} ${s.form}`).join(', ')}
          </>
        ) : (
          <>All {substances.length} opened.</>
        ),
      action:
        unopened.length > 0
          ? { label: 'Open next', onClick: () => openRow(unopened[0].cas) }
          : undefined,
    },
    {
      id: 'flagged',
      label: 'No flagged verdict left unreviewed',
      met: flaggedUnopened.length === 0,
      detail:
        flaggedUnopened.length > 0 ? (
          <>{flaggedUnopened.map((s) => `${s.element} (${s.conclusion})`).join(' · ')}</>
        ) : undefined,
      action:
        flaggedUnopened.length > 0
          ? { label: 'Open', onClick: () => openRow(flaggedUnopened[0].cas) }
          : undefined,
    },
    {
      id: 'corpus',
      label: `Corpus synced within ${CORPUS_MAX_AGE_DAYS} days`,
      met: corpusAge <= CORPUS_MAX_AGE_DAYS,
      detail: <>synced {corpusSyncedAt}</>,
    },
    {
      id: 'determination',
      // Never met. There is no endpoint, and pretending otherwise would be the
      // single most dangerous lie this screen could tell.
      label: 'R.E. determination recorded',
      met: false,
      detail: <>No endpoint exists to record one. This gate cannot arm.</>,
    },
  ];

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Regulatory gate</b>
        spec §4.4 — hard gate, R.E. sign-off · corpus synced{' '}
        {corpusSyncedAt}
      </div>

      <Gate
        kind="hard"
        title="Regulatory gate"
        records="records the R.E.'s offline determination"
        requirements={requirements}
        signLabel="Record R.E. determination"
        rejectLabel="Reject (requires a reason)"
        ledgerNote
      />

      <MockBadge note="These screening results were not produced by the compliance agent. The real per-substance verdicts live on the compatibility matrix." />

      <table className="mx">
        <thead>
          <tr>
            <th>Substance</th>
            <th>Element gate</th>
            <th>Application</th>
            <th>Conclusion</th>
            <th style={{ width: 90 }} />
          </tr>
        </thead>
        <tbody>
          {substances.map((s) => {
            const isOpen = open === s.cas;
            return (
              <Fragment key={s.cas}>
                <tr
                  ref={(el) => {
                    rowRefs.current[s.cas] = el;
                  }}
                  style={isOpen ? { background: 'var(--surface-2)' } : undefined}
                >
                  <td>
                    <span style={{ fontWeight: 500 }}>{s.element}</span>{' '}
                    <span className="secondary">{s.form}</span>
                    <div className="tiny muted data">
                      {s.cas}
                    </div>
                  </td>
                  <td>
                    <span className={`chip ${CLASS[s.elementGate]}`}>{s.elementGate}</span>
                    <div className="tiny muted">product-wide</div>
                  </td>
                  <td>
                    <span className={`chip ${CLASS[s.application]}`}>{s.application}</span>
                    <div className="tiny muted">per component</div>
                  </td>
                  <td className="small">
                    {s.conclusion}
                    {!opened.has(s.cas) && (
                      <div className="tiny" style={{ color: 'var(--text-warning)' }}>
                        <i className="ti ti-eye-exclamation" aria-hidden="true" /> not yet opened
                      </div>
                    )}
                    {opened.has(s.cas) && (
                      <div className="tiny" style={{ color: 'var(--text-success)' }}>
                        <i className="ti ti-check" aria-hidden="true" /> opened
                      </div>
                    )}
                  </td>
                  <td>
                    <button
                      className="btn"
                      onClick={() => (isOpen ? setOpen(null) : openRow(s.cas))}
                      aria-expanded={isOpen}
                    >
                      {isOpen ? 'Hide' : 'Evidence'}
                    </button>
                  </td>
                </tr>
                {/* Master-detail in place: the evidence belongs to its row, not to a
                    floating panel below the whole table. */}
                {isOpen && (
                  <tr>
                    <td colSpan={5} style={{ padding: 0, background: 'var(--surface-2)' }}>
                      <div style={{ borderLeft: '2px solid var(--text-accent)', padding: 'var(--s3)' }}>
                        <Evidence substance={s} />
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
        </tbody>
      </table>
    </section>
  );
}

function Evidence({ substance }: { substance: Substance }) {
  const groups: [string, string, Check[]][] = [
    // The hybrid model made legible from the layout (spec §1.2): the element gate is
    // product-wide, so it gets a full-width band; the application check is per
    // component. Today they read as two identical columns.
    ['Element gate', 'product-wide — a failing element is out for every component', substance.evidence.elementGate],
    ['Application check', 'per component — application × target markets', substance.evidence.application],
    ['Hazard layer', 'CLP / SDS', substance.evidence.hazard],
  ];

  return (
    <div>
      {groups.map(([title, sub, checks]) => (
        <div key={title} style={{ marginBottom: 12 }}>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, marginBottom: 4 }}>
            <span style={{ fontSize: 'var(--t-small)', fontWeight: 600 }}>{title}</span>
            <span className="tiny muted">{sub}</span>
          </div>
          {checks.map((c) => (
            <div className="step" key={c.check}>
              <i className="ti ti-file-search" aria-hidden="true" style={{ marginTop: 2 }} />
              <div>
                <div>
                  <b>{c.check}</b> — {c.result}
                </div>
                <CitationChip
                  source={c.source}
                  reference={c.reference}
                  retrievedAt={c.retrievedAt}
                />
              </div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}
