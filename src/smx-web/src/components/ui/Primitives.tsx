import { Fragment, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import type { Citation } from '../../api/types';
import { Data } from './Data';

/** Card. A hairline border and a surface change on hover. Paper, not a game tile — it does
 *  not float and it does not lift (see the shadow and transform rules in craft.css). */
export function Card({
  children,
  tone,
  className = '',
  style,
}: {
  children: ReactNode;
  tone?: 'warning' | 'danger' | 'accent' | 'muted';
  className?: string;
  style?: React.CSSProperties;
}) {
  return (
    <div className={`card ${className}`} data-tone={tone} style={style}>
      {children}
    </div>
  );
}

/**
 * A stat tile. `absent` renders the tile the spec demands but no endpoint can
 * fill — dashed and empty, naming the missing capability rather than quietly
 * dropping the question.
 */
export function StatCard({
  label,
  value,
  hint,
  tone,
  absent,
}: {
  label: string;
  value?: number | string;
  hint?: string;
  tone?: 'warning' | 'danger' | 'accent';
  absent?: boolean;
}) {
  return (
    <div className={`stat ${absent ? 'stat--absent' : ''}`} data-tone={absent ? undefined : tone}>
      <div className="stat__value">{absent ? '—' : value}</div>
      <div className="stat__label">{label}</div>
      {hint && <div className="stat__hint">{hint}</div>}
    </div>
  );
}

export function SectionHeader({
  eyebrow,
  title,
  count,
  hint,
  actions,
  headingLevel,
}: {
  eyebrow?: string;
  title?: string;
  count?: number;
  hint?: string;
  actions?: ReactNode;
  /**
   * Render the title as a real heading rather than a styled span. Opt-in, because most section
   * headers in this app label a list inside a screen — but where the sections ARE the structure
   * of the screen (Intake's three), a screen-reader user must be able to jump between them, and
   * a `<span>` that merely looks like a heading is not one. Levels are the page's, not the
   * component's: ProjectHeader owns the h1 and NextAction the h2, so a stage screen's sections
   * are h3.
   */
  headingLevel?: 2 | 3 | 4;
}) {
  const Title = (headingLevel ? `h${headingLevel}` : 'span') as 'h3' | 'span';
  return (
    <div className="sec">
      {eyebrow && <span className="sec__eyebrow">{eyebrow}</span>}
      {title && <Title className="sec__title">{title}</Title>}
      {count !== undefined && <span className="sec__count">{count}</span>}
      {hint && <span className="sec__hint">{hint}</span>}
      {actions && <span className="sec__actions">{actions}</span>}
    </div>
  );
}

export function EmptyState({
  icon = 'ti-inbox',
  title,
  body,
  actions,
  children,
}: {
  icon?: string;
  title: string;
  body?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <div className="empty">
      {/* Set on the title's baseline, not floated above it in a grey disc. */}
      <p className="empty__title">
        <i className={`ti ${icon} empty__icon`} aria-hidden="true" />
        {title}
      </p>
      {body && <div className="empty__body prose">{body}</div>}
      {actions && <div className="empty__actions">{actions}</div>}
      {children}
    </div>
  );
}

export function Skeleton({
  variant = 'text',
  width,
  height,
}: {
  variant?: 'text' | 'chip' | 'spine' | 'bar';
  width?: number | string;
  height?: number | string;
}) {
  return <span className={`sk sk--${variant}`} style={{ width, height }} aria-hidden="true" />;
}

/** A horizontal comparison bar. Always neutral — a price is not a verdict. */
export function BarRow({
  label,
  value,
  max,
  display,
  best,
  sub,
}: {
  label: ReactNode;
  value: number;
  max: number;
  display: string;
  best?: boolean;
  sub?: ReactNode;
}) {
  const pct = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
  return (
    <div className="barrow" data-best={best ? 'true' : undefined}>
      <div className="barrow__label">
        {label}
        {sub && <div className="tiny muted">{sub}</div>}
      </div>
      <div className="barrow__track">
        <div className="barrow__fill" style={{ width: `${pct * 100}%` }} />
      </div>
      <div className="barrow__value">
        {display}
        {best && (
          <span className="tiny muted" style={{ marginLeft: 6 }}>
            best
          </span>
        )}
      </div>
    </div>
  );
}

/** The longest reference we will print. Past this the chip stops being read and starts being a wall. */
const REF_MAX = 46;

/**
 * A reference, shortened to what a person can actually use.
 *
 * Measured on the deployed app, the longest chip printed 110 characters at 870px — WIDER than the
 * rationale it was supporting, and in the identical colour, because the client brand direction
 * pulled `--text-muted` to #5c6b7d. So the citation did not out-shout its own sentence by hue; it
 * did it by MASS. Cutting the mass is the whole fix.
 *
 * Three cuts, each of which removes something no reader can use and NONE of which invents a label:
 *
 *   1. the path prefix — `smx-reference/` repeats the `source` word already printed beside it;
 *   2. the trailing content hash — `-1f4f46959a` identifies a chunk to the indexer, nobody else;
 *   3. the `chunk-` / `rule-` scaffolding the chunker prepends to every id it writes.
 *
 * What survives is the slug the corpus itself chose, in words. If that still runs long it is cut at
 * a word boundary with an ellipsis, and `title` always carries the untouched original — so the full
 * identifier is one hover away and is never destroyed. Deriving a *prettier* label than the slug
 * would mean inventing one, which is the same failure as a chip that links to the wrong regulation.
 *
 * `exact` says whether the label IS the reference, character for character. Callers use it to decide
 * whether the original still needs somewhere to live.
 */
export function shortenReference(reference: string): { label: string; exact: boolean } {
  const last = reference.split('/').filter(Boolean).pop() ?? reference;
  const stripped = last
    .replace(/[-_][0-9a-f]{8,}$/i, '') // the trailing content hash
    .replace(/^(?:chunk|entry)[-_](?:rule[-_])?/i, ''); // the chunker's scaffolding

  const done = (label: string) => ({ label, exact: label === reference });

  // Nothing usable survived the cuts — a reference that is a bare hash strips down to the word
  // "chunk", which names our own plumbing and identifies nothing. Print what the record holds.
  if (stripped.length === 0 || /^(?:chunk|entry|rule)$/i.test(stripped)) return done(reference);

  // Short enough to print whole. A short reference is an IDENTIFIER — `reach-17`, `turn0search0` —
  // and its punctuation is part of it. De-hyphenating "reach-17" into "reach 17" would corrupt the
  // name of a regulation to save two pixels.
  if (stripped.length <= REF_MAX) return done(stripped);

  // Long enough that it is a slug meant to be read as words, so read it as words.
  const words = stripped.replace(/[-_]+/g, ' ').trim();
  if (words.length <= REF_MAX) return done(words);
  const cut = words.slice(0, REF_MAX);
  const atSpace = cut.lastIndexOf(' ');
  return done(`${(atSpace > REF_MAX * 0.6 ? cut.slice(0, atSpace) : cut).trimEnd()}…`);
}

/** A citation. Every verdict must trace to one; a dimension without one is a defect. */
export function CitationChip({
  source,
  reference,
  retrievedAt,
  snippet,
  documentId,
  entryId,
}: {
  source: string;
  reference: string;
  retrievedAt: string;
  snippet?: string;
  /**
   * Present ONLY where the citation is real. The fixture-backed screens pass nothing and the
   * chip stays inert — linking a fabricated citation into a real document viewer would let
   * a citation borrow authority it has not earned. `entryId` alone changes nothing: an anchor
   * with no document to anchor into points at the wrong thing.
   */
  documentId?: string;
  entryId?: string;
}) {
  const short = shortenReference(reference);
  // The untouched reference always survives in the tooltip, alongside the snippet if there is
  // one. Shortening may never be the only copy of a value the operator might need to quote.
  const hint = [short.exact ? null : reference, snippet].filter(Boolean).join('\n\n') || undefined;

  const body = (
    <>
      <span className="src__source">{source}</span> <Data kind="code">{short.label}</Data>
      {/* The corpus sync date is the load-bearing half of a citation: a regulation
          entry without the date it was retrieved is not a citation, it is a claim. */}
      <span className="muted">
        {' '}
        · <Data kind="date">{retrievedAt.slice(0, 10)}</Data>
      </span>
    </>
  );

  if (!documentId) {
    return (
      <span className="src" title={hint}>
        {body}
      </span>
    );
  }

  const href = `/docs/${encodeURIComponent(documentId)}${
    entryId ? `?entry=${encodeURIComponent(entryId)}` : ''
  }`;
  return (
    <Link className="src src-link" to={href} title={hint}>
      {body}
    </Link>
  );
}

/** How many chips one source may print before the rest fold into a count. */
const PER_SOURCE_MAX = 2;

/**
 * A set of citations, capped per source.
 *
 * Discovery renders 24 of these. Four of them said `web · turn0search0`, `turn0search8`,
 * `turn0search12`, `turn0search16` — a search-turn index is not a source anybody can check, so
 * four chips carried the information of one plus a number.
 *
 * The rule is uniform and does not special-case `web`: a source shows its first two references and
 * folds the remainder into a `+N more` chip whose tooltip lists every one of them, in full. That is
 * a deliberate trade of stated provenance for legibility, approved 2026-07-30 — and it is bounded,
 * because nothing is discarded. It also deviates from what was proposed: the proposal collapsed the
 * whole group to `web · 3 sources`, and showing the first two keeps the references that *are*
 * legible (a corpus slug) visible while still capping the mass.
 *
 * Grouping preserves first-appearance order, so the agent's own ordering survives.
 */
export function CitationList({
  citations,
  className,
}: {
  citations: readonly Citation[];
  className?: string;
}) {
  // `client.ts` casts every response with `as` and validates nothing, so a drifted payload can put
  // a non-array here. An empty list renders nothing, which is correct — absence of citations is a
  // fact each screen reports itself, not something this component should editorialise.
  const list = Array.isArray(citations) ? citations.filter((c) => c && typeof c.source === 'string') : [];
  if (list.length === 0) return null;

  const bySource = new Map<string, Citation[]>();
  for (const c of list) {
    const group = bySource.get(c.source);
    if (group) group.push(c);
    else bySource.set(c.source, [c]);
  }

  return (
    <span className={className}>
      {[...bySource.entries()].map(([source, group]) => {
        const shown = group.slice(0, PER_SOURCE_MAX);
        const rest = group.slice(PER_SOURCE_MAX);
        return (
          <Fragment key={source}>
            {shown.map((c, i) => (
              <CitationChip key={`${source}-${c.reference}-${i}`} {...c} />
            ))}
            {rest.length > 0 && (
              <span
                className="src"
                title={rest
                  .map((c) => `${c.source} · ${c.reference} · ${c.retrievedAt.slice(0, 10)}`)
                  .join('\n')}
              >
                <span className="src__source">{source}</span> +{rest.length} more
              </span>
            )}
          </Fragment>
        );
      })}
    </span>
  );
}

export function SearchInput({
  value,
  onChange,
  placeholder,
  label,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder: string;
  label: string;
}) {
  return (
    <div className="search" style={{ marginBottom: 14 }}>
      <i className="ti ti-search" aria-hidden="true" />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={label}
      />
    </div>
  );
}
