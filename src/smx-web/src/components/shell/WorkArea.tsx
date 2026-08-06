import { useCallback, useEffect, useState, type ReactNode } from 'react';

const KEY = 'smx.agentCollapsed.';

const read = (slug: string, fallback: boolean) => {
  try {
    const stored = localStorage.getItem(KEY + slug);
    return stored === null ? fallback : stored === '1';
  } catch {
    // a private-mode browser is not a reason to break the layout
    return fallback;
  }
};

/**
 * Whether the agent panel is collapsed on this screen, remembered PER SCREEN.
 *
 * One shared key was the old design and it had a real failure mode: a collapse the operator chose
 * on the matrix also governed screens that had never offered the control, so the agent could end up
 * hidden on a screen with no way to bring it back. Now the default comes from the screen (spec
 * §11.2 — open on the three phases, collapsed on Full matrix, where the table is genuinely
 * width-starved) and the operator's own choice overrides it for that screen only.
 *
 * `slug` is the SCREEN's slug, never the agent's — Full matrix borrows the Regulatory agent, and
 * keying on that would hand two screens one shared preference, recreating the leak this fixes.
 *
 * `slug === null` means the screen has no panel at all. The shortcut is not bound in that case:
 * a binding with nothing to toggle would silently swallow Ctrl/Cmd + \ from whatever else wants it.
 */
export function useAgentPanel(
  slug: string | null,
  defaultCollapsed: boolean,
): { collapsed: boolean; toggle: () => void } {
  const [collapsed, setCollapsed] = useState(() => (slug ? read(slug, defaultCollapsed) : false));

  // Re-read on a screen change. This hook lives in `ProjectLayout`, which React keeps mounted
  // across stage navigations, so without this the first screen's state would follow the operator
  // onto every subsequent one — including onto Full matrix, whose whole point is a different default.
  useEffect(() => {
    setCollapsed(slug ? read(slug, defaultCollapsed) : false);
  }, [slug, defaultCollapsed]);

  const toggle = useCallback(() => {
    if (!slug) return;
    setCollapsed((current) => {
      const next = !current;
      try {
        localStorage.setItem(KEY + slug, next ? '1' : '0');
      } catch {
        /* see above — the preference is lost, the layout is not */
      }
      return next;
    });
  }, [slug]);

  // Cmd/Ctrl + \ — the conventional "toggle the side panel" binding in professional tools, and the
  // only way back to the agent without a mouse.
  useEffect(() => {
    if (!slug) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === '\\' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        toggle();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [slug, toggle]);

  return { collapsed, toggle };
}

/**
 * The work area: the artifact, and the agent panel to its RIGHT.
 *
 * The agent used to hold a fixed 390px LEFT column, on the reasoning that a cited regulation name
 * must fit on one line. The measure was right and is unchanged — 390px open, ~62 characters. What
 * changed is the artifact beside it: against an 18-column matrix (spec §5.2) a permanent third of
 * the viewport spent on the conversation ABOUT the decision, rather than the decision, is the wrong
 * trade. So the panel moved right and became collapsible.
 *
 * Collapsed it is a RAIL, never an absence. The agent is the only surface the operator may instruct
 * — the app's central rule is "no direct edits, instruct with a reason" — and an interface where it
 * can be dismissed outright invites working around it. The rail also carries the unread mark, so
 * activity that arrived while it was shut is visible rather than lost.
 *
 * `panel={null}` drops the column entirely (`data-chat='none'`). Overview passes it: the intake
 * agent's composer lives inside that screen, beside the brief it amends (spec §11.4), and an empty
 * 390px panel apologising for not existing is worse than no panel.
 *
 * The class names are the ones the old left-hand column used (`work__chat`, `work__rail`). They are
 * the same surface in a different place, and `styles/print.css` — which is not this stream's file —
 * hides both by name.
 */
export function WorkArea({
  panel,
  collapsed,
  onToggle,
  unread = false,
  children,
}: {
  panel: ReactNode | null;
  collapsed: boolean;
  onToggle: () => void;
  /** Agent activity arrived while the panel was collapsed. Only ever shown on the rail. */
  unread?: boolean;
  children: ReactNode;
}) {
  const state = panel === null ? 'none' : collapsed ? 'collapsed' : 'open';

  return (
    <div className="work" data-chat={state}>
      {/* The artifact comes first in the DOM as well as on screen: it is what the operator came to
          read, and DOM order is tab order. */}
      <div className="work__artifact">{children}</div>

      {state === 'collapsed' && (
        /* `aria-label`, even though the rail carries a visible word: the name computed from its
           contents is "Agent", a noun, not what the button does — and the unread mark is a dot,
           which says nothing at all out loud. */
        <button
          type="button"
          className="work__rail"
          onClick={onToggle}
          title={
            unread ? 'Show the agent — new activity (Ctrl/Cmd + \\)' : 'Show the agent (Ctrl/Cmd + \\)'
          }
          aria-label={unread ? 'Show the agent — new activity' : 'Show the agent'}
        >
          <i className="ti ti-layout-sidebar-right-expand" aria-hidden="true" />
          <span className="work__rail-label">Agent</span>
          {unread && <span className="work__rail-dot" aria-hidden="true" />}
        </button>
      )}

      {state === 'open' && (
        /*
         * A plain box, deliberately not an <aside>. What goes in here is `AgentPanel`, already an
         * <aside> carrying its own stage-specific label ("Regulatory agent"), so a landmark here
         * would nest a second `complementary` region around it, named more vaguely than the one
         * inside. Two nested landmarks where the outer adds nothing is noise to walk past.
         */
        <div className="work__chat">
          <button
            type="button"
            className="work__collapse"
            onClick={onToggle}
            title="Collapse the agent (Ctrl/Cmd + \)"
            aria-label="Collapse the agent"
          >
            <i className="ti ti-layout-sidebar-right-collapse" aria-hidden="true" />
          </button>
          {panel}
        </div>
      )}
    </div>
  );
}
