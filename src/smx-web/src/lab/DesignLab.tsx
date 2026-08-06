import { useCallback, useEffect, useRef, useState } from 'react';
import './lab.css';
import {
  MONO_FONTS,
  SANS_FONTS,
  monoEntry,
  monoStack,
  sansEntry,
  sansStack,
} from './catalog';
import { ensureLoaded, faceReady } from './fontLoader';
import { type FaceMetrics, measureFace } from './measure';
import {
  BASE_STEPS,
  type BaseSize,
  DEFAULTS,
  type Density,
  type LabSettings,
  type MonoScope,
  type SidebarTone,
  applyToDom,
  cssBlock,
  loadSettings,
  saveSettings,
  stepDown,
  stepUp,
} from './settings';

/**
 * The design lab panel. DEV ONLY — see LabMount for the gate, and fontLoader.ts for why
 * none of this (nor any of the twelve font packages) reaches a production build.
 *
 * It is a control surface over the REAL APP, not a mockup. The screens underneath stay
 * mounted, routable and live: change the sans, then navigate to Discovery, Regulatory,
 * Dosing, Full matrix and watch the actual tables re-render. Judging a typeface from a
 * specimen sheet is how you end up with one that reads beautifully in a paragraph and
 * fails on a column of CAS numbers, which is the only place this product reads text.
 *
 * Everything it does, it does by writing custom properties and data attributes onto
 * <html> at runtime. It does not edit a stylesheet; the way a chosen combination becomes
 * real is the "Copy CSS" button and a human pasting it into tokens.css.
 */

/** The strings that actually matter in this product. Not a pangram. */
const SPECIMEN = [
  '1306-38-3  14589-40-3  Ce:Y = 1.00:0.50',
  '0O 1lI 5S 8B',
  '18 estimate – 700 estimate  250 kg  7.2 mg',
];

export function DesignLab() {
  const [settings, setSettings] = useState<LabSettings>(() =>
    loadSettings(window.localStorage),
  );
  const [open, setOpen] = useState(true);
  const [showCss, setShowCss] = useState(false);
  const [copied, setCopied] = useState(false);
  const [sansMetrics, setSansMetrics] = useState<FaceMetrics | null>(null);
  const [monoMetrics, setMonoMetrics] = useState<FaceMetrics | null>(null);
  const [sansOk, setSansOk] = useState<boolean | null>(null);
  const [monoOk, setMonoOk] = useState<boolean | null>(null);
  const outRef = useRef<HTMLTextAreaElement>(null);

  const sansFace = sansEntry(settings.sans);
  const monoFace = monoEntry(settings.mono);

  // Push the settings at the document and remember them. Runs on every change, including
  // the first, so a reload lands on the stored combination rather than on the defaults.
  useEffect(() => {
    applyToDom(settings, document.documentElement);
    saveSettings(settings, window.localStorage);
  }, [settings]);

  // Reserve the panel's width so it never sits on top of a wide matrix — the artifact it
  // is most often used to judge is also the widest thing on the screen.
  useEffect(() => {
    document.documentElement.style.setProperty('--lab-panel-w', open ? '340px' : '0px');
  }, [open]);

  // Load the chosen faces, wait until they are genuinely usable, then measure.
  // The await is load-bearing: injecting an @font-face rule does not fetch anything, so
  // measuring immediately would report the FALLBACK's x-height under the candidate's name.
  useEffect(() => {
    let live = true;
    void (async () => {
      await ensureLoaded('sans', settings.sans);
      const ok = await faceReady(sansFace.family, sansFace.weights);
      if (!live) return;
      setSansOk(ok);
      setSansMetrics(measureFace(sansStack(settings.sans)));
    })();
    return () => {
      live = false;
    };
  }, [settings.sans, sansFace.family, sansFace.weights]);

  useEffect(() => {
    let live = true;
    void (async () => {
      if (settings.mono === 'none') {
        // "none" is not a face and has nothing to load. It measures as the SANS, because
        // that is literally what the mono slot renders as — reporting a system monospace
        // here would describe a font the app would never actually use.
        await ensureLoaded('sans', settings.sans);
        if (!live) return;
        setMonoOk(null);
        setMonoMetrics(measureFace(sansStack(settings.sans)));
        return;
      }
      await ensureLoaded('mono', settings.mono);
      const ok = await faceReady(monoFace.family, monoFace.weights);
      if (!live) return;
      setMonoOk(ok);
      setMonoMetrics(measureFace(monoStack(settings.mono)));
    })();
    return () => {
      live = false;
    };
  }, [settings.mono, settings.sans, monoFace.family, monoFace.weights]);

  const set = useCallback(<K extends keyof LabSettings>(key: K, value: LabSettings[K]) => {
    setSettings((prev) => ({ ...prev, [key]: value }));
    setCopied(false);
  }, []);

  const block = cssBlock(settings);

  const copy = useCallback(() => {
    void (async () => {
      try {
        await navigator.clipboard.writeText(block);
        setCopied(true);
      } catch {
        // Clipboard permission is not guaranteed on an http origin. Falling back to
        // selecting the text keeps the button honest: it still gets the CSS in front of
        // the operator instead of silently doing nothing.
        setShowCss(true);
        queueMicrotask(() => outRef.current?.select());
      }
    })();
  }, [block]);

  if (!open) {
    return (
      <button className="dlab-open" onClick={() => setOpen(true)}>
        Design lab
      </button>
    );
  }

  const down = stepDown(settings.base);
  const up = stepUp(settings.base);

  return (
    <aside className="dlab" aria-label="Design lab">
      <div className="dlab__head">
        <h2 className="dlab__title">Design lab</h2>
        <span className="dlab__dev">DEV</span>
        <button className="dlab__x" onClick={() => setOpen(false)} aria-label="Hide panel">
          Hide
        </button>
      </div>

      <div className="dlab__body">
        {/* ---- SANS ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Sans</span>
          <select
            className="dlab__select"
            value={settings.sans}
            onChange={(e) => set('sans', e.target.value as LabSettings['sans'])}
          >
            {SANS_FONTS.map((f) => (
              <option key={f.id} value={f.id}>
                {f.label}
                {f.id === DEFAULTS.sans ? ' — current' : ''}
              </option>
            ))}
          </select>
          {sansFace.caveat ? <p className="dlab__warn">{sansFace.caveat}</p> : null}
        </section>

        {/* ---- MONO ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Mono</span>
          <select
            className="dlab__select"
            value={settings.mono}
            onChange={(e) => set('mono', e.target.value as LabSettings['mono'])}
          >
            {MONO_FONTS.map((f) => (
              <option key={f.id} value={f.id}>
                {f.label}
                {f.id === DEFAULTS.mono ? ' — current' : ''}
              </option>
            ))}
          </select>
          {settings.mono === 'none' ? (
            <p className="dlab__note">
              The mono slot now aliases the sans — not a system monospace. Figures are held
              in column by <code>tabular-nums</code> instead of by a fixed advance.
            </p>
          ) : null}
        </section>

        {/* ---- MONO SCOPE ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Mono scope</span>
          <div className="dlab__seg">
            {(
              [
                ['all', 'All values'],
                ['identifiers', 'IDs only'],
                ['none', 'None'],
              ] as const
            ).map(([id, label]) => (
              <button
                key={id}
                aria-pressed={settings.monoScope === id}
                onClick={() => set('monoScope', id as MonoScope)}
              >
                {label}
              </button>
            ))}
          </div>
          <p className="dlab__note">
            {settings.monoScope === 'all'
              ? "Today's rule: every machine-readable value is mono."
              : settings.monoScope === 'identifiers'
                ? 'Mono kept on CAS, marker codes and ids — the values transcribed character by character. Quantities go sans + tabular.'
                : 'Nothing is mono. One face for the whole product.'}
          </p>
        </section>

        {/* ---- DENSITY ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Table density</span>
          <div className="dlab__seg">
            {(
              [
                ['compact', 'Compact'],
                ['default', 'Default'],
                ['roomy', 'Roomy'],
              ] as const
            ).map(([id, label]) => (
              <button
                key={id}
                aria-pressed={settings.density === id}
                onClick={() => set('density', id as Density)}
              >
                {label}
              </button>
            ))}
          </div>
        </section>

        {/* ---- BASE SIZE ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Base size — {settings.base}px</span>
          <div className="dlab__seg">
            {BASE_STEPS.map((b) => (
              <button
                key={b}
                aria-pressed={settings.base === b}
                onClick={() => set('base', b as BaseSize)}
              >
                {b}px
              </button>
            ))}
          </div>
          <div className="dlab__seg">
            <button disabled={!down.ok} onClick={() => down.ok && set('base', down.next)}>
              &minus; Smaller
            </button>
            <button disabled={!up.ok} onClick={() => up.ok && set('base', up.next)}>
              + Larger
            </button>
          </div>
          {/* The floor is REFUSED, not clamped: the control says why it will not go lower
              rather than accepting the press and quietly doing nothing. */}
          {!down.ok ? <p className="dlab__warn">{down.reason}</p> : null}
        </section>

        {/* ---- SIDEBAR TONE ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Sidebar tone</span>
          <div className="dlab__seg">
            {(
              [
                ['light', 'Light'],
                ['navy', 'Brand navy'],
              ] as const
            ).map(([id, label]) => (
              <button
                key={id}
                aria-pressed={settings.sidebar === id}
                onClick={() => set('sidebar', id as SidebarTone)}
              >
                {label}
              </button>
            ))}
          </div>
          {settings.sidebar === 'navy' ? (
            <p className="dlab__note">
              Status glyphs have been re-mixed toward white to survive the dark surface —
              so they no longer match the verdict colours used everywhere else. That
              divergence is the real cost of this choice, not a rendering bug.
            </p>
          ) : null}
        </section>

        {/* ---- SPECIMEN ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Specimen — at {settings.base}px</span>
          <div className="dlab__spec">
            <div className="dlab__specrow">
              <span className="dlab__speclabel">Sans — {sansFace.family}</span>
              {SPECIMEN.map((line) => (
                <span key={line} className="dlab__specline dlab__specline--sans">
                  {line}
                </span>
              ))}
            </div>
            <div className="dlab__specrow">
              <span className="dlab__speclabel">
                {settings.mono === 'none'
                  ? `Mono — none (${sansFace.family} + tabular)`
                  : `Mono — ${monoFace.family}`}
              </span>
              {SPECIMEN.map((line) => (
                <span key={line} className="dlab__specline dlab__specline--mono">
                  {line}
                </span>
              ))}
            </div>
          </div>
        </section>

        {/* ---- MEASUREMENTS ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Why one reads bigger</span>
          <div className="dlab__metrics">
            <MetricRow label="x-height" a={sansMetrics} b={monoMetrics} metric="x" />
            <MetricRow label="10 digits" a={sansMetrics} b={monoMetrics} metric="w" />
          </div>
          <p className="dlab__note">
            x-height is what the eye reads as &ldquo;size&rdquo; — two faces at the same
            {' '}
            {settings.base}px can differ by a sixth of it. The digit run is the cost:
            wider figures mean fewer matrix columns on a laptop.
          </p>
        </section>

        {/* ---- RESOLVED ---- */}
        <section className="dlab__group">
          <span className="dlab__legend">Resolved</span>
          <div className="dlab__resolved">
            <div>--font-sans: {sansStack(settings.sans)}</div>
            <div>
              --font-mono:{' '}
              {settings.monoScope === 'all' ? monoStack(settings.mono) : sansStack(settings.sans)}
            </div>
            <div>
              loaded: {sansFace.family}{' '}
              {sansOk === null ? '…' : sansOk ? 'yes' : <span className="dlab__missing">NO — showing fallback</span>}
              {settings.mono !== 'none' ? (
                <>
                  {' · '}
                  {monoFace.family}{' '}
                  {monoOk === null ? '…' : monoOk ? 'yes' : <span className="dlab__missing">NO — showing fallback</span>}
                </>
              ) : null}
            </div>
          </div>
        </section>

        {/* ---- ACTIONS ---- */}
        <section className="dlab__group">
          <div className="dlab__actions">
            <button className="dlab__btn dlab__btn--primary" onClick={copy}>
              {copied ? 'Copied' : 'Copy CSS'}
            </button>
            <button className="dlab__btn" onClick={() => setShowCss((v) => !v)}>
              {showCss ? 'Hide' : 'Show'}
            </button>
            <button className="dlab__btn" onClick={() => setSettings({ ...DEFAULTS })}>
              Reset
            </button>
          </div>
          {showCss ? (
            <textarea className="dlab__out" ref={outRef} readOnly value={block} />
          ) : null}
          <p className="dlab__note">
            Paste into <code>src/styles/tokens.css</code>. Choices that are not tokens —
            mono scope, density, sidebar tone — are emitted as comments naming the file and
            rule that has to change, rather than as CSS that looks complete and is not.
          </p>
        </section>
      </div>
    </aside>
  );
}

/**
 * One comparison row. Bars are scaled against the larger of the two, so the row shows the
 * DIFFERENCE at a glance rather than two absolute lengths that need reading.
 */
function MetricRow({
  label,
  a,
  b,
  metric,
}: {
  label: string;
  a: FaceMetrics | null;
  b: FaceMetrics | null;
  metric: 'x' | 'w';
}) {
  const pick = (m: FaceMetrics | null) => (m ? (metric === 'x' ? m.xRatio : m.digitRun / m.atPx) : 0);
  const av = pick(a);
  const bv = pick(b);
  const max = Math.max(av, bv);
  if (max <= 0) {
    return (
      <>
        <span className="dlab__mname">{label}</span>
        <span className="dlab__mval">not measurable here</span>
        <span />
      </>
    );
  }
  const fmt = (v: number) => (metric === 'x' ? `${(v * 100).toFixed(0)}%` : `${v.toFixed(2)}em`);
  return (
    <>
      <span className="dlab__mname">{label} · sans</span>
      <span
        className={metric === 'x' ? 'dlab__mbar' : 'dlab__mbar dlab__mbar--w'}
        style={{ width: `${(av / max) * 100}%` }}
      />
      <span className="dlab__mval">{fmt(av)}</span>
      <span className="dlab__mname">{label} · mono</span>
      <span
        className={metric === 'x' ? 'dlab__mbar' : 'dlab__mbar dlab__mbar--w'}
        style={{ width: `${(bv / max) * 100}%` }}
      />
      <span className="dlab__mval">{fmt(bv)}</span>
    </>
  );
}
