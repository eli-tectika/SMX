import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { documentName } from '../../domain/documentName';
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

/**
 * A citation. Every verdict must trace to one; a dimension without one is a defect.
 *
 * TWO KINDS OF THING, not one thing in two moods (spec §16.5):
 *
 *   WITH a decodable `documentId` — `search_regulatory` or `search_sds` retrieved the chunk from a
 *   document we hold, so the chip shows that document's FILE NAME and opens it. Not the id
 *   (base64url, unreadable, unbounded in a table cell) and not `reference`, which is free text.
 *
 *   WITHOUT one — a plain label. `.cite` carries no border, no hover and no cursor change, because
 *   this state is PERMANENT for most citations rather than a gap someone will close: only those two
 *   tools can mint an id, so every Discovery and pool citation — reference spreadsheets, learned
 *   conclusions, Cosmos lookups, the open web — will never have one, and a Regulatory cell holds
 *   both kinds side by side. A chip that looks pressable and is not teaches the operator to stop
 *   pressing the ones that work.
 *
 * NO ID IS EVER DERIVED FROM `reference`. It would be right often enough to be trusted and wrong
 * often enough to open the wrong regulation, and nothing on screen would say which they got. An id
 * this build cannot decode falls to the label branch for the same reason: a link that looks live and
 * 404s is worse than no link, because the operator only finds out after following it.
 */
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
  documentId?: string | null;
  /** An anchor alone changes nothing: there is nothing to anchor into without a document. */
  entryId?: string | null;
}) {
  /* The corpus sync date is the load-bearing half of a citation: a regulation entry without the date
     it was retrieved is not a citation, it is a claim. It survives in both forms. */
  const when = (
    <span className="muted">
      {' '}
      · <Data kind="date">{retrievedAt.slice(0, 10)}</Data>
    </span>
  );

  const name = documentName(documentId);

  if (!name) {
    return (
      <span className="cite" data-cite="label" title={snippet ?? undefined}>
        {source} · <Data kind="code">{reference}</Data>
        {when}
      </span>
    );
  }

  const href = `/docs/${encodeURIComponent(documentId!)}${
    entryId ? `?entry=${encodeURIComponent(entryId)}` : ''
  }`;
  return (
    /* `reference` moves into the title rather than being dropped: it is the agent's own pointer into
       the document (an article, an annex), and losing it would cost the operator the passage. */
    <Link className="cite-open" data-cite="open" to={href} title={snippet ?? reference}>
      <i className="ti ti-file-text" aria-hidden="true" />
      <Data kind="code">{name}</Data>
      {when}
    </Link>
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
