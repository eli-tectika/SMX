import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
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
  const body = (
    <>
      {source} · <Data kind="code">{reference}</Data>
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
      <span className="src" title={snippet ?? undefined}>
        {body}
      </span>
    );
  }

  const href = `/docs/${encodeURIComponent(documentId)}${
    entryId ? `?entry=${encodeURIComponent(entryId)}` : ''
  }`;
  return (
    <Link className="src src-link" to={href} title={snippet ?? undefined}>
      {body}
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
