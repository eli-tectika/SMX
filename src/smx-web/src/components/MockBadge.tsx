/**
 * Marks a screen whose content is fixture data, not an agent verdict.
 *
 * This is load-bearing, not decoration. SMX exists because a wrong marker
 * recommendation causes real-world harm, and every real verdict traces to a cited
 * source. A fabricated verdict that renders identically to a real one is the exact
 * failure this badge prevents. Do not remove it from a screen until that screen is
 * reading from a real endpoint.
 */
export function MockBadge({ note }: { note?: string }) {
  return (
    <div className="banner warn" role="note">
      <i className="ti ti-alert-triangle" aria-hidden="true" style={{ marginTop: 1 }} />
      <div>
        <b>Mock data</b> — not produced by an agent, not traceable to a source. This screen has no
        backend endpoint yet.
        {note ? <div style={{ marginTop: 3, opacity: 0.85 }}>{note}</div> : null}
      </div>
    </div>
  );
}
