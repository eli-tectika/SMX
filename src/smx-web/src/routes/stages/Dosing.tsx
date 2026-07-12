import { MockBadge } from '../../components/MockBadge';
import { Gate, type Requirement } from '../../components/ui/Gate';
import { ParkSlot } from '../../components/ui/Primitives';
import { axisMax, niceTicks } from '../../domain/ticks';
import dosing from '../../mocks/fixtures/dosing.json';

interface Win {
  element: string;
  floor: number;
  recommendedLow: number;
  recommendedHigh: number;
  ceiling: number;
}
interface Code {
  code: string;
  kind: string;
  markers: string[];
  ratio: string;
  orderAmountKg: number;
  note: string;
}

const CHART_W = 620;
const LABEL_W = 42;
const ROW_H = 40;
const PAD_TOP = 26;

/**
 * Dosing & codes (spec §4.5) — the ppm window chart and the code cards.
 *
 * The chart is hand-drawn SVG rather than a chart library: the annotated bands
 * (floor, recommended, ceiling) are the whole point and a generic bar chart would
 * lose them.
 *
 * The axis ticks now come from niceTicks(), which fixes a real bug: this screen
 * used to scale to max(ceiling) * 1.1 (= 38.5) while hardcoding its gridlines to
 * [0, 10, 20, 30], so the labels understated the chart and would have drifted
 * further wrong the moment a ceiling changed. On a screen whose only job is to
 * show a dosing window, a mislabelled axis is a correctness bug.
 */
export function Dosing() {
  const { windows, codes } = dosing as { windows: Win[]; codes: Code[] };

  const rawMax = Math.max(...windows.map((w) => w.ceiling));
  const max = axisMax(rawMax * 1.05);
  const ticks = niceTicks(rawMax * 1.05);
  const scale = (v: number) => LABEL_W + (v / max) * (CHART_W - LABEL_W - 12);
  const height = PAD_TOP + windows.length * ROW_H + 20;

  const requirements: Requirement[] = [
    {
      id: 'windows',
      label: 'A ppm window exists for every marker element',
      met: windows.length > 0,
      detail: <>{windows.map((w) => w.element).join(', ')}</>,
    },
    {
      id: 'review',
      label: 'Code finalization reviewed (PL / VP / physics)',
      met: false,
      detail: <>No endpoint records a soft review. This gate cannot be marked reviewed.</>,
    },
  ];

  return (
    <section className="screen" data-provenance="mock">
      <div className="cap">
        <b>Dosing &amp; codes</b> &nbsp;·&nbsp; spec §4.5 — ppm windows + code combinations, per
        component
      </div>

      <Gate
        kind="soft"
        title="Code finalization"
        records="PL / VP / physics review"
        requirements={requirements}
        signLabel="Mark review recorded"
      />

      <MockBadge note="No ppm model has run. These windows and ratio signatures are illustrative." />

      <svg
        viewBox={`0 0 ${CHART_W} ${height}`}
        width="100%"
        role="img"
        aria-label="Recommended ppm window per marker element"
        style={{ marginBottom: 10 }}
      >
        <title>Recommended ppm window per marker element</title>

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
            <g key={w.element}>
              <text x={0} y={y + 10} fontSize="11" fill="var(--text-primary)" fontWeight="500">
                {w.element}
              </text>

              {/* usable range: floor -> ceiling */}
              <rect
                x={scale(w.floor)}
                y={y}
                width={scale(w.ceiling) - scale(w.floor)}
                height={14}
                rx={3}
                fill="var(--surface-1)"
              />
              {/* recommended band */}
              <rect
                x={scale(w.recommendedLow)}
                y={y}
                width={scale(w.recommendedHigh) - scale(w.recommendedLow)}
                height={14}
                rx={3}
                fill="var(--bg-success)"
                stroke="var(--border-success)"
                strokeWidth="0.5"
              />
              {/* detection floor — below this the marker cannot be read back */}
              <line
                x1={scale(w.floor)}
                y1={y - 3}
                x2={scale(w.floor)}
                y2={y + 17}
                stroke="var(--text-danger)"
                strokeWidth="1.5"
              />
              {/* upper bound */}
              <line
                x1={scale(w.ceiling)}
                y1={y - 3}
                x2={scale(w.ceiling)}
                y2={y + 17}
                stroke="var(--text-muted)"
                strokeWidth="1.5"
                strokeDasharray="3 2"
              />

              {/* Value labels — the numbers were previously unreadable from the chart. */}
              <text
                x={scale(w.recommendedLow) + (scale(w.recommendedHigh) - scale(w.recommendedLow)) / 2}
                y={y + 10}
                fontSize="9"
                fill="var(--text-success)"
                textAnchor="middle"
                style={{ fontVariantNumeric: 'tabular-nums' }}
              >
                {w.recommendedLow}–{w.recommendedHigh}
              </text>
              <text x={scale(w.floor)} y={y + 27} fontSize="8" fill="var(--text-danger)" textAnchor="middle">
                {w.floor}
              </text>
              <text x={scale(w.ceiling)} y={y + 27} fontSize="8" fill="var(--text-muted)" textAnchor="middle">
                {w.ceiling}
              </text>
            </g>
          );
        })}
      </svg>

      <div className="tiny muted" style={{ display: 'flex', gap: 14, marginBottom: 8, flexWrap: 'wrap' }}>
        <span>
          <span style={{ display: 'inline-block', width: 10, height: 3, background: 'var(--text-danger)' }} />{' '}
          detection floor
        </span>
        <span>
          <span
            style={{
              display: 'inline-block',
              width: 12,
              height: 8,
              background: 'var(--bg-success)',
              border: '0.5px solid var(--border-success)',
            }}
          />{' '}
          recommended band
        </span>
        <span>
          <span style={{ display: 'inline-block', width: 10, borderTop: '2px dashed var(--text-muted)' }} />{' '}
          upper bound
        </span>
      </div>

      {/* Spec §4.5: "the estimate tends to underestimate, so basis and confidence are
          shown per bound." The fixture carries no basis, and inventing one would be a
          fabricated claim about how a bound was derived. Say so instead. */}
      <div className="banner warn" style={{ marginBottom: 18 }}>
        <i className="ti ti-alert-triangle" aria-hidden="true" />
        <div>
          Spec §4.5 requires a <b>basis and confidence per bound</b> — a regulatory ceiling and a
          formulation estimate are not the same claim, and the estimate tends to underestimate. The
          fixture carries neither, so none is shown. These bounds are unattributed.
        </div>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(230px, 1fr))', gap: 10 }}>
        {codes.map((c) => (
          <div className="card" key={c.code}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
              <span style={{ fontSize: 15, fontWeight: 600, fontFamily: 'var(--font-mono)' }}>
                {c.code}
              </span>
              <span className="tiny muted">{c.kind}</span>
              <span
                className="tiny muted"
                style={{ marginLeft: 'auto', fontVariantNumeric: 'tabular-nums' }}
              >
                {c.orderAmountKg} kg
              </span>
            </div>
            <div style={{ marginBottom: 8 }}>
              {/* A marker element is not a verdict — green here would read as "Pass". */}
              {c.markers.map((m) => (
                <span className="chip chip--neutral" key={m} style={{ marginRight: 3 }}>
                  {m}
                </span>
              ))}
            </div>
            <div className="small secondary" style={{ marginBottom: 4 }}>
              ratio <b style={{ fontFamily: 'var(--font-mono)' }}>{c.ratio}</b>
            </div>
            <p className="tiny muted" style={{ margin: 0 }}>
              {c.note}
            </p>
          </div>
        ))}
      </div>

      <div style={{ marginTop: 14 }}>
        <ParkSlot awaiting="code-finalization review" specRef="spec §4.5" />
      </div>
    </section>
  );
}
