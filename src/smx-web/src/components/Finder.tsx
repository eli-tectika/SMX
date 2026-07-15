import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getLearnedConclusions, getMarkerLibrary, getMsdsRegistry } from '../api/client';
import type { LearnedConclusion, MarkerLibraryEntry, MsdsEntry } from '../api/types';
import { Data } from './ui/Data';

/**
 * The finder — ⌘K / Ctrl-K.
 *
 * A command palette is the most-copied feature in software, and in a generated app it
 * almost always arrives as a *launcher*: a list of the same navigation links that are
 * already in the header, plus "Create new project" and "Toggle dark mode". That version
 * earns nothing. It exists to look like a professional tool rather than to be one.
 *
 * So this is a FINDER, not a launcher. What it searches is chemistry: a CAS number, an
 * element symbol, a marker code, a supplier, a material, an application. It queries the
 * three cross-project knowledge surfaces server-side (KnowledgeEndpoints.cs takes `?search=`
 * on all three) and takes you to the record.
 *
 * That maps onto a real thing the operator does. Spec §6: the Marker Library exists so the
 * Intake agent can "search here first to surface reuse candidates", and the MSDS Registry
 * "gates procurement — an order stays blocked until its MSDS is current and reviewed". When
 * a client names a substance, the first question is *have we already cleared this, and do we
 * have a current sheet for it* — and until now answering that meant leaving the project,
 * opening two screens, and reading two tables.
 *
 * Navigation is deliberately NOT offered here. The four surfaces are one click away in the
 * header; putting them in the finder too would be padding, and padding is how a finder
 * degrades into a launcher.
 */

type Hit =
  | { kind: 'marker'; id: string; title: string; sub: string; to: string; badge: string }
  | { kind: 'msds'; id: string; title: string; sub: string; to: string; badge: string }
  | { kind: 'conclusion'; id: string; title: string; sub: string; to: string; badge: string };

const MIN_QUERY = 2;

export function Finder() {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<Hit[]>([]);
  const [busy, setBusy] = useState(false);
  const [cursor, setCursor] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const navigate = useNavigate();

  const close = useCallback(() => {
    setOpen(false);
    setQuery('');
    setHits([]);
    setCursor(0);
  }, []);

  // ⌘K / Ctrl-K to open, Esc to close.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        setOpen((o) => !o);
        return;
      }
      if (e.key === 'Escape' && open) close();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, close]);

  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  // Query all three surfaces at once. The operator does not know, and should not have to
  // know, which of them holds the answer.
  useEffect(() => {
    if (!open || query.trim().length < MIN_QUERY) {
      setHits([]);
      setBusy(false);
      return;
    }
    let cancelled = false;
    setBusy(true);

    const t = setTimeout(() => {
      const s = query.trim();
      Promise.allSettled([getMarkerLibrary(s), getMsdsRegistry(s), getLearnedConclusions(s)])
        .then(([markers, msds, conclusions]) => {
          if (cancelled) return;
          const out: Hit[] = [];

          if (markers.status === 'fulfilled') {
            for (const m of markers.value as MarkerLibraryEntry[]) {
              out.push({
                kind: 'marker',
                id: m.id,
                title: m.composition.markers.join(' + '),
                sub: `${m.validatedFor.material} · ${m.validatedFor.application} · reused ${m.reuseCount}×`,
                to: '/marker-library',
                badge: m.status,
              });
            }
          }
          if (msds.status === 'fulfilled') {
            for (const m of msds.value as MsdsEntry[]) {
              out.push({
                kind: 'msds',
                id: m.id,
                title: m.cas,
                sub: `${m.supplier} · v${m.version} · ${m.date.slice(0, 10)}`,
                to: '/msds-registry',
                badge: m.reviewStatus,
              });
            }
          }
          if (conclusions.status === 'fulfilled') {
            for (const c of conclusions.value as LearnedConclusion[]) {
              out.push({
                kind: 'conclusion',
                id: c.id,
                title: c.finding,
                sub: `${c.kind} · confidence ${(c.confidence * 100).toFixed(0)}%`,
                to: '/learned-conclusions',
                badge: c.kind,
              });
            }
          }

          setHits(out);
          setCursor(0);
          setBusy(false);
        })
        .catch(() => {
          if (!cancelled) {
            setHits([]);
            setBusy(false);
          }
        });
    }, 180);

    return () => {
      cancelled = true;
      clearTimeout(t);
    };
  }, [open, query]);

  const go = useCallback(
    (hit: Hit) => {
      navigate(hit.to);
      close();
    },
    [navigate, close],
  );

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setCursor((c) => Math.min(hits.length - 1, c + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setCursor((c) => Math.max(0, c - 1));
    } else if (e.key === 'Enter' && hits[cursor]) {
      e.preventDefault();
      go(hits[cursor]);
    }
  };

  const short = query.trim().length > 0 && query.trim().length < MIN_QUERY;
  const empty = useMemo(
    () => !busy && !short && query.trim().length >= MIN_QUERY && hits.length === 0,
    [busy, short, query, hits.length],
  );

  return (
    <>
      <button type="button" className="findbtn" onClick={() => setOpen(true)}>
        <i className="ti ti-search" aria-hidden="true" />
        <span>Find a CAS, marker, supplier…</span>
        <kbd>⌘K</kbd>
      </button>

      {open && (
        <div className="finder__scrim" onMouseDown={close} role="presentation">
          <div
            className="finder"
            role="dialog"
            aria-modal="true"
            aria-label="Find across the knowledge layer"
            onMouseDown={(e) => e.stopPropagation()}
          >
            <div className="finder__field">
              <i className="ti ti-search" aria-hidden="true" />
              <input
                ref={inputRef}
                type="text"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={onKeyDown}
                placeholder="CAS number, element, marker code, supplier, material…"
                aria-label="Search the marker library, MSDS registry and learned conclusions"
              />
              {busy && <i className="ti ti-loader spin" aria-hidden="true" />}
            </div>

            <div className="finder__scope">
              Searches the marker library, the MSDS registry and learned conclusions. Not the
              open web, and not a project's own verdicts.
            </div>

            {hits.length > 0 && (
              <ul className="finder__list">
                {hits.map((h, i) => (
                  <li key={`${h.kind}:${h.id}`}>
                    <button
                      type="button"
                      className="finder__hit"
                      data-on={i === cursor ? '' : undefined}
                      onMouseEnter={() => setCursor(i)}
                      onClick={() => go(h)}
                    >
                      <span className="finder__kind">{LABEL[h.kind]}</span>
                      <span className="finder__hit-main">
                        <span className="finder__hit-title">
                          {h.kind === 'msds' || h.kind === 'marker' ? (
                            <Data kind={h.kind === 'msds' ? 'cas' : 'code'}>{h.title}</Data>
                          ) : (
                            h.title
                          )}
                        </span>
                        <span className="finder__hit-sub">{h.sub}</span>
                      </span>
                      <span className="chip chip--neutral finder__badge">{h.badge}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}

            {short && <div className="finder__note">Keep typing — at least {MIN_QUERY} characters.</div>}

            {empty && (
              <div className="finder__note">
                Nothing matches <b>{query.trim()}</b>. An empty result is a real answer here: the
                library only holds codes that have passed the VP gate.
              </div>
            )}
          </div>
        </div>
      )}
    </>
  );
}

const LABEL: Record<Hit['kind'], string> = {
  marker: 'Marker',
  msds: 'MSDS',
  conclusion: 'Learned',
};
