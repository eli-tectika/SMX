import { Link, useLocation, useNavigate } from 'react-router-dom';

interface Props {
  /** Where to go when there is no history to step back through. */
  fallback: string;
  /** What that fallback is called, in the operator's words. */
  fallbackLabel: string;
}

/** What a caller puts in `state` so the way back can name itself. */
export interface BackFrom {
  from: { label: string };
}

/**
 * The way back out of a detail screen.
 *
 * A document is reached from several places — the library, an MSDS registry row that blocks an
 * order, the finder — so a fixed breadcrumb sends the operator somewhere they were not. This steps
 * back through history instead, which returns them to the exact URL they left: the filters those
 * screens keep in the query string come back with it, and the browser restores the scroll.
 *
 * The label is only as specific as the caller made it. A link that passed `state={{ from: {...} }}`
 * proves where it came from and gets named; anything else says plain "Back", because a confident
 * label on a guess is how you promise the MSDS registry and deliver the finder.
 */
export function BackLink({ fallback, fallbackLabel }: Props) {
  const navigate = useNavigate();
  const { key, state } = useLocation();
  const from = (state as Partial<BackFrom> | null)?.from;

  // React Router stamps the session's first entry `default`. A deep link, a refresh or a pasted
  // URL lands there, and stepping back from it leaves the app entirely — so that case gets a real
  // destination instead.
  const canGoBack = key !== 'default';
  const label = from?.label ?? (canGoBack ? null : fallbackLabel);
  const text = label ? `Back to ${label}` : 'Back';

  if (!canGoBack) {
    return (
      <Link className="backlink" to={fallback}>
        <i className="ti ti-chevron-left" aria-hidden="true" />
        {text}
      </Link>
    );
  }

  return (
    <button className="backlink" type="button" onClick={() => navigate(-1)}>
      <i className="ti ti-chevron-left" aria-hidden="true" />
      {text}
    </button>
  );
}
