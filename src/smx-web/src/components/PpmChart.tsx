import { Fragment, useId } from 'react';
import type { Bound, BoundKind, PpmWindow } from '../api/types';
import { fmtPpm } from '../domain/dosing';
import { axisMax, niceTicks } from '../domain/ticks';

/**
 * The dosable ppm window, per marker element — the Dosing screen's ANSWER, not its decoration.
 *
 * Three decisions hold this drawing together, and none of them is cosmetic.
 *
 * **1. Measured vs. estimated is encoded by FORM, never by hue.** The obvious encoding —
 * teal for the measured floor, grey for the estimated ceiling — was checked with the palette
 * validator and fails: `#5c6b7d` against `#0f6b62` separates by ΔE 4.3 under protanopia and only
 * 8.8 with normal colour vision. That is not enough separation to carry the most load-bearing
 * distinction on the screen (one end is the physicist's measurement, the other is the agent's own
 * claim, and an agent may never author `measured`). So the chart is achromatic and the shapes speak:
 *
 *   - a KNOWN end is a solid, capped rule — the detection floor always, and the upper bound when the
 *     record gives it a measured or regulatory basis;
 *   - an ESTIMATED end has no rule at all: the band DISSOLVES into the page, because nobody knows
 *     where the window really ends;
 *   - the recommended dose is the largest mark, in ink, and carries the only direct label.
 *
 * The dissolve is conditional on `upper.kind` and must stay that way. Fading every band alike drew a
 * legal migration limit — a hard number with a citation — as exactly as vague as a figure the agent
 * extrapolated from a neighbouring salt, while the key underneath claimed the fade meant "estimated".
 * That is the one direction this drawing must not fail in: it would understate a real ceiling and
 * launder a guess into something that looks equally considered.
 *
 * Ink and not green for the recommendation: green means *Pass* everywhere else in this app, and the
 * recommended dose is an answer, not a verdict. And the floor is NOT drawn in `--text-danger`, which
 * it used to be — red means *Fail*, and the floor is the opposite of a failure: it is the one number
 * on this screen that was actually measured.
 *
 * **2. The region below the floor is DRAWN.** The old chart started the plot at the floor, so the
 * band where XRF physically cannot see the marker did not exist on screen and the window's left edge
 * looked arbitrary. It is hatched from zero.
 *
 * **3. It is responsive by construction.** Every horizontal coordinate is a PERCENTAGE of the
 * viewport, so the plot fills whatever width the artifact column has — no fixed 620px viewBox, no
 * measurement, no ResizeObserver. The element gutter is an HTML grid column, which is also what
 * keeps the gridlines of every row in register.
 *
 * Provenance — basis and confidence — lives in the hover title of each mark. That is what took
 * `conf 0.62` off every row of the table below.
 */

/** Row geometry, in user units (= px, since there is no viewBox). */
const ROW_H = 46;
const BAND_Y = 20;
const BAND_H = 12;
/** The floor rule runs past the band top and bottom; its caps sit just outside that. */
const RULE_TOP = 15;
const RULE_BOT = 37;
const CAP_W = 9;
const CAP_H = 2;
const AXIS_H = 18;
/** The element gutter. Symbols are 1–2 glyphs; this is a label column, not a legend. */
const GUTTER = 52;

const FLOOR_INK = 'var(--text-secondary)';
const RECOMMENDED_INK = 'var(--ink)';

/** What each kind of bound actually is, in one clause. Shown on hover, where the evidence lives. */
const KIND_NOTE: Record<BoundKind, string> = {
  measured: "the physicist's measurement — the agent cannot author this kind",
  regulatory: 'a regulatory cap the agent read from the corpus',
  estimate: "the agent's own estimate, known to run low",
};

const clamp01 = (n: number) => Math.max(0, Math.min(1, n));

/**
 * Does this end of the window dissolve rather than stop?
 *
 * Only an `estimate` does. `measured` is the physicist's and `regulatory` is a cap read from the
 * corpus with a citation behind it — both are edges somebody can point at, and drawing them as fog
 * would say the record is vaguer than it is.
 */
const isSoftEnd = (b: Bound) => b.kind === 'estimate';

function boundTitle(label: string, b: Bound): string {
  const basis = b.basis.trim();
  return [
    `${label} ${fmtPpm(b.ppm)} ppm`,
    `${b.kind} — ${KIND_NOTE[b.kind]}`,
    `confidence ${b.confidence.toFixed(2)}`,
    basis,
  ]
    .filter(Boolean)
    .join(' · ');
}

/**
 * The row, as a sentence. The chart's marks are unreachable to a screen reader, and the bounds
 * table below carries the numbers but not the shape — so each row states its own reading, including
 * which end is measured and which is claimed.
 */
function rowLabel(w: PpmWindow): string {
  return (
    `${w.element}: recommended ${fmtPpm(w.recommendedPpm)} ppm, ` +
    `inside a window of ${fmtPpm(w.floor.ppm)} to ${fmtPpm(w.upper.ppm)} ppm. ` +
    `Detection floor ${w.floor.kind}, upper bound ${w.upper.kind}. ` +
    `Quantifiable above ${fmtPpm(w.quantificationPpm)} ppm.`
  );
}

export function PpmChart({ windows }: { windows: readonly PpmWindow[] }) {
  /* Ids must be unique per mount — two components on one screen sharing a pattern id would each
     paint with whichever definition the document found first. `useId` gives colons, which are legal
     in an id but treacherous inside `url(#…)`; strip to word characters. */
  const uid = useId().replace(/\W/g, '');
  const hatchId = `ppmhatch${uid}`;
  const fadeId = `ppmfade${uid}`;

  /* The axis must cover every mark, not just the upper bounds: a recommended dose or a
     quantification threshold outside the plotted range would be silently clipped to the edge, and a
     dose that renders at the wrong place is the whole failure mode of this drawing. */
  const top = windows.reduce(
    (m, w) => Math.max(m, w.upper.ppm, w.floor.ppm, w.recommendedPpm, w.quantificationPpm),
    0,
  );
  const headroom = top * 1.05;
  const max = axisMax(headroom);
  const ticks = niceTicks(headroom);

  if (windows.length === 0) return null;
  if (!Number.isFinite(max) || max <= 0) {
    /* Everything is zero or nothing is finite. There is no scale to draw against, and a chart drawn
       against a made-up one would be a picture of numbers nobody recorded. */
    return (
      <p className="small" role="alert" style={{ margin: '0 0 var(--s3)' }}>
        These windows carry no positive ppm value, so there is no scale to plot them against. The
        figures are in the table below.
      </p>
    );
  }

  const pct = (v: number) => `${clamp01(v / max) * 100}%`;
  /** A mark's label runs off the plot at the extremes; anchor it inward instead of clipping it. */
  const anchor = (v: number) => {
    const f = clamp01(v / max);
    return f > 0.93 ? 'end' : f < 0.05 ? 'start' : 'middle';
  };

  return (
    <div style={{ marginBottom: 'var(--s3)' }}>
      {/* One definition per mount, referenced by every row and by the legend below. */}
      <svg width="0" height="0" aria-hidden="true" focusable="false" style={{ position: 'absolute' }}>
        <defs>
          <pattern
            id={hatchId}
            width={6}
            height={6}
            patternUnits="userSpaceOnUse"
            patternTransform="rotate(45)"
          >
            <line x1={0} y1={0} x2={0} y2={6} stroke="var(--border-strong)" strokeWidth={1} />
          </pattern>
          {/* The dissolve. Solid where the window is known to be usable, gone by the upper bound —
              the gradient is in bounding-box units, so it stretches to each row's own band. */}
          <linearGradient id={fadeId} x1="0" y1="0" x2="1" y2="0">
            <stop offset="0" stopColor="var(--surface-3)" stopOpacity={1} />
            <stop offset="0.45" stopColor="var(--surface-3)" stopOpacity={1} />
            <stop offset="1" stopColor="var(--surface-3)" stopOpacity={0} />
          </linearGradient>
        </defs>
      </svg>

      <div
        style={{
          display: 'grid',
          gridTemplateColumns: `${GUTTER}px minmax(0, 1fr)`,
          alignItems: 'center',
          rowGap: 0,
        }}
      >
        <span />
        {/* The scale. aria-hidden: each row states its own numbers in words. */}
        <svg
          width="100%"
          height={AXIS_H}
          aria-hidden="true"
          focusable="false"
          style={{ display: 'block', overflow: 'visible' }}
        >
          {ticks.map((t, i) => (
            <text
              key={t}
              x={pct(t)}
              y={AXIS_H - 5}
              fontSize={12}
              fontFamily="var(--font-mono)"
              fill="var(--text-muted)"
              textAnchor={i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle'}
            >
              {t}
            </text>
          ))}
        </svg>

        {windows.map((w) => (
          <Fragment key={`${w.componentId}|${w.cas}|${w.element}`}>
            <span
              className="data"
              style={{ fontSize: 'var(--t-body)', fontWeight: 'var(--w-medium)' }}
            >
              {w.element}
            </span>
            <svg
              width="100%"
              height={ROW_H}
              role="img"
              aria-label={rowLabel(w)}
              style={{ display: 'block', overflow: 'visible' }}
            >
              {ticks.map((t) => (
                <line
                  key={t}
                  x1={pct(t)}
                  x2={pct(t)}
                  y1={0}
                  y2={ROW_H}
                  stroke="var(--border)"
                  strokeWidth={1}
                />
              ))}

              {/* Below the floor. Drawn, because "XRF cannot see the marker here" is a fact about
                  this window, and a plot that began at the floor hid it. */}
              <g data-mark="below-floor">
                <title>
                  {`Below the detection floor — 0 to ${fmtPpm(w.floor.ppm)} ppm. XRF cannot see the marker at these levels.`}
                </title>
                <rect
                  x={0}
                  y={BAND_Y}
                  width={pct(w.floor.ppm)}
                  height={BAND_H}
                  fill={`url(#${hatchId})`}
                />
              </g>

              {/* The dosable window. Its right end dissolves ONLY when the upper bound is an
                  estimate; a measured or regulatory ceiling is a real edge and the band runs to it
                  at full opacity, with a capped rule below. */}
              <g data-mark="window" data-upper={isSoftEnd(w.upper) ? 'soft' : 'hard'}>
                <title>
                  {isSoftEnd(w.upper)
                    ? `Dosable window ${fmtPpm(w.floor.ppm)} to ${fmtPpm(w.upper.ppm)} ppm. Its upper end is an estimate, not a hard edge — the band fades because where the window really ends is not known.`
                    : `Dosable window ${fmtPpm(w.floor.ppm)} to ${fmtPpm(w.upper.ppm)} ppm. Both ends are known: the upper bound is ${w.upper.kind}, so the band stops where it stops.`}
                </title>
                <rect
                  x={pct(w.floor.ppm)}
                  y={BAND_Y}
                  width={`${Math.max(0, clamp01(w.upper.ppm / max) - clamp01(w.floor.ppm / max)) * 100}%`}
                  height={BAND_H}
                  fill={isSoftEnd(w.upper) ? `url(#${fadeId})` : 'var(--surface-3)'}
                />
              </g>

              {/* Quantification — detectable below this, but not measurable. A hairline notch, and
                  deliberately uncapped: it is a threshold inside the window, not one of its ends. */}
              <g data-mark="quantification">
                <title>
                  {`Quantification threshold ${fmtPpm(w.quantificationPpm)} ppm — detectable below this, but not measurable.`}
                </title>
                <line
                  x1={pct(w.quantificationPpm)}
                  x2={pct(w.quantificationPpm)}
                  y1={BAND_Y}
                  y2={BAND_Y + BAND_H}
                  stroke="var(--text-muted)"
                  strokeWidth={1}
                />
                <rect
                  x={pct(w.quantificationPpm)}
                  y={BAND_Y}
                  width={10}
                  height={BAND_H}
                  transform="translate(-5,0)"
                  fill="transparent"
                />
              </g>

              {/* An ESTIMATED upper bound gets no rule: the absence of an edge is the encoding, and
                  the hit area below is the only thing there. A measured or regulatory one gets the
                  same capped rule as the floor, because it is the same kind of fact. */}
              <g data-mark="upper" data-edge={isSoftEnd(w.upper) ? 'soft' : 'hard'}>
                <title>{boundTitle('Upper bound', w.upper)}</title>
                {!isSoftEnd(w.upper) && (
                  <>
                    <rect
                      x={pct(w.upper.ppm)}
                      y={RULE_TOP - CAP_H}
                      width={CAP_W}
                      height={CAP_H}
                      transform={`translate(${-CAP_W / 2},0)`}
                      fill={FLOOR_INK}
                    />
                    <line
                      x1={pct(w.upper.ppm)}
                      x2={pct(w.upper.ppm)}
                      y1={RULE_TOP}
                      y2={RULE_BOT}
                      stroke={FLOOR_INK}
                      strokeWidth={2}
                    />
                    <rect
                      x={pct(w.upper.ppm)}
                      y={RULE_BOT}
                      width={CAP_W}
                      height={CAP_H}
                      transform={`translate(${-CAP_W / 2},0)`}
                      fill={FLOOR_INK}
                    />
                  </>
                )}
                <rect
                  x={pct(w.upper.ppm)}
                  y={BAND_Y - 4}
                  width={18}
                  height={BAND_H + 8}
                  transform="translate(-9,0)"
                  fill="transparent"
                />
              </g>

              {/* The detection floor. Solid and capped — the hard edge of a known value. */}
              <g data-mark="floor">
                <title>{boundTitle('Detection floor', w.floor)}</title>
                <rect
                  x={pct(w.floor.ppm)}
                  y={RULE_TOP - CAP_H}
                  width={CAP_W}
                  height={CAP_H}
                  transform={`translate(${-CAP_W / 2},0)`}
                  fill={FLOOR_INK}
                />
                <line
                  x1={pct(w.floor.ppm)}
                  x2={pct(w.floor.ppm)}
                  y1={RULE_TOP}
                  y2={RULE_BOT}
                  stroke={FLOOR_INK}
                  strokeWidth={2}
                />
                <rect
                  x={pct(w.floor.ppm)}
                  y={RULE_BOT}
                  width={CAP_W}
                  height={CAP_H}
                  transform={`translate(${-CAP_W / 2},0)`}
                  fill={FLOOR_INK}
                />
                <rect
                  x={pct(w.floor.ppm)}
                  y={0}
                  width={16}
                  height={ROW_H}
                  transform="translate(-8,0)"
                  fill="transparent"
                />
              </g>

              {/* The recommendation is ONE value, so it is one mark — the largest one, drawn last so
                  nothing overlaps it, and the only thing on the row carrying a direct label. */}
              <g data-mark="recommended">
                <title>
                  {`Recommended dose ${fmtPpm(w.recommendedPpm)} ppm — a single value the agent chose inside the window, not a tolerance band.`}
                </title>
                <rect
                  x={pct(w.recommendedPpm)}
                  y={RULE_TOP - 3}
                  width={3}
                  height={RULE_BOT - RULE_TOP + 6}
                  transform="translate(-1.5,0)"
                  fill={RECOMMENDED_INK}
                />
                <text
                  x={pct(w.recommendedPpm)}
                  y={11}
                  fontSize={12}
                  fontFamily="var(--font-mono)"
                  fontWeight={600}
                  fill={RECOMMENDED_INK}
                  textAnchor={anchor(w.recommendedPpm)}
                >
                  {fmtPpm(w.recommendedPpm)}
                </text>
                <rect
                  x={pct(w.recommendedPpm)}
                  y={0}
                  width={18}
                  height={ROW_H}
                  transform="translate(-9,0)"
                  fill="transparent"
                />
              </g>
            </svg>
          </Fragment>
        ))}
      </div>

      <ChartKey hatchId={hatchId} fadeId={fadeId} />
    </div>
  );
}

/**
 * The key. Not optional decoration: with colour deliberately carrying no meaning here, the shapes
 * are the entire encoding, and a shape nobody can decode is worse than a hue nobody can separate.
 */
function ChartKey({ hatchId, fadeId }: { hatchId: string; fadeId: string }) {
  const items: [string, JSX.Element][] = [
    [
      'A known end — measured, or a cited regulatory cap',
      <>
        <rect x={11} y={2} width={9} height={2} transform="translate(-4.5,0)" fill={FLOOR_INK} />
        <line x1={11} x2={11} y1={4} y2={12} stroke={FLOOR_INK} strokeWidth={2} />
        <rect x={11} y={12} width={9} height={2} transform="translate(-4.5,0)" fill={FLOOR_INK} />
      </>,
    ],
    [
      'Dosable window',
      <rect x={0} y={5} width={26} height={8} fill="var(--surface-3)" />,
    ],
    [
      'Fading edge — that end is only an estimate',
      <rect x={0} y={5} width={26} height={8} fill={`url(#${fadeId})`} />,
    ],
    [
      'Recommended dose',
      <rect x={11} y={2} width={3} height={14} fill={RECOMMENDED_INK} />,
    ],
    [
      'Below the floor — XRF cannot see the marker',
      <rect x={0} y={5} width={26} height={8} fill={`url(#${hatchId})`} />,
    ],
    [
      'Quantification threshold',
      <line x1={13} x2={13} y1={5} y2={13} stroke="var(--text-muted)" strokeWidth={1} />,
    ],
  ];

  return (
    <ul
      style={{
        listStyle: 'none',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 'var(--s1) var(--s4)',
        margin: 'var(--s2) 0 0',
        padding: 0,
        fontSize: 'var(--t-small)',
        color: 'var(--text-secondary)',
      }}
    >
      {items.map(([label, glyph]) => (
        <li key={label} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <svg width={26} height={18} aria-hidden="true" focusable="false" style={{ flex: 'none' }}>
            {glyph}
          </svg>
          {label}
        </li>
      ))}
    </ul>
  );
}
