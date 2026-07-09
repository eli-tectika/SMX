import { MockBadge } from '../../components/MockBadge';
import dosing from '../../mocks/fixtures/dosing.json';

interface Window {
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

const CHART_W = 560;
const LABEL_W = 46;
const ROW_H = 34;
const PAD_TOP = 22;

/**
 * Dosing & codes (spec §4.5) — the ppm window chart and the code cards.
 *
 * The chart is hand-drawn SVG rather than a chart library: it encodes a floor, a
 * recommended band, and a dashed upper bound, and the annotated bands are the point.
 * Axis is ppm, shared across the rows so bars are comparable.
 */
export function Dosing() {
  const { windows, codes } = dosing as { windows: Window[]; codes: Code[] };
  const max = Math.max(...windows.map((w) => w.ceiling)) * 1.1;
  const scale = (v: number) => LABEL_W + (v / max) * (CHART_W - LABEL_W - 10);
  const height = PAD_TOP + windows.length * ROW_H + 24;

  return (
    <section className="screen">
      <div className="cap">
        <b>Dosing &amp; codes</b> &nbsp;·&nbsp; spec §4.5 — ppm windows + code combinations, per
        component
      </div>

      <MockBadge note="No ppm model has run. These windows and ratio signatures are illustrative." />

      <svg
        viewBox={`0 0 ${CHART_W} ${height}`}
        width="100%"
        role="img"
        aria-label="Recommended ppm window per marker element"
        style={{ marginBottom: 18 }}
      >
        <title>Recommended ppm window per marker element</title>
        {[0, 10, 20, 30].map((t) => (
          <g key={t}>
            <line
              x1={scale(t)}
              y1={PAD_TOP - 6}
              x2={scale(t)}
              y2={PAD_TOP + windows.length * ROW_H}
              stroke="var(--border)"
              strokeWidth="0.5"
            />
            <text x={scale(t)} y={PAD_TOP - 10} fontSize="9" fill="var(--text-muted)" textAnchor="middle">
              {t}
            </text>
          </g>
        ))}
        <text x={CHART_W - 4} y={height - 6} fontSize="9" fill="var(--text-muted)" textAnchor="end">
          ppm
        </text>

        {windows.map((w, i) => {
          const y = PAD_TOP + i * ROW_H + 6;
          return (
            <g key={w.element}>
              <text x={0} y={y + 9} fontSize="11" fill="var(--text-primary)" fontWeight="500">
                {w.element}
              </text>
              {/* full usable range: floor -> ceiling */}
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
              {/* detection floor */}
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
            </g>
          );
        })}
      </svg>

      <div className="tiny muted" style={{ display: 'flex', gap: 14, marginBottom: 18 }}>
        <span>
          <span
            style={{ display: 'inline-block', width: 10, height: 3, background: 'var(--text-danger)' }}
          />{' '}
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
          <span
            style={{
              display: 'inline-block',
              width: 10,
              borderTop: '2px dashed var(--text-muted)',
            }}
          />{' '}
          upper bound
        </span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 10 }}>
        {codes.map((c) => (
          <div className="region" key={c.code}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
              <span style={{ fontSize: 14, fontWeight: 600 }}>{c.code}</span>
              <span className="tiny muted">{c.kind}</span>
              <span className="tiny muted" style={{ marginLeft: 'auto' }}>
                {c.orderAmountKg} kg
              </span>
            </div>
            <div style={{ marginBottom: 6 }}>
              {c.markers.map((m) => (
                <span className="chip v" key={m} style={{ marginRight: 3 }}>
                  {m}
                </span>
              ))}
            </div>
            <div className="small secondary">
              ratio <b>{c.ratio}</b>
            </div>
            <p className="tiny muted" style={{ margin: '5px 0 0' }}>
              {c.note}
            </p>
          </div>
        ))}
      </div>

      <div className="banner warn" style={{ marginTop: 14 }}>
        <i className="ti ti-eye-exclamation" aria-hidden="true" />
        <div>
          Code finalization is a <b>soft review</b> gate (PL / VP / physics). Recording that review
          has no endpoint yet.
        </div>
      </div>
    </section>
  );
}
