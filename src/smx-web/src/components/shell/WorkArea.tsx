import { useCallback, useEffect, useState, type ReactNode } from 'react';

const KEY = 'smx.chatCollapsed';

/**
 * Chat left at a fixed width; the artifact takes everything else.
 *
 * The agent is a primary working surface, not an accessory — so it comes first in the eye's path
 * and is always mounted. But primary means POSITION, not area: comfortable reading tops out
 * around 65 characters, and every pixel past that is taken from the compatibility matrix, which
 * is the thing being decided. Hence a fixed 390px (~62 characters) that never grows. The old
 * 230px right dock gave ~32 characters, at which a cited regulation name does not fit on a line.
 *
 * `collapsible` is per screen, not global: it is for Matrix and Dosing, where the artifact is
 * genuinely width-starved. Everywhere else the control would only offer a way to hide the one
 * surface the operator may instruct — and, because the stored preference is one shared key, a
 * screen that did not ask to be collapsible also IGNORES a collapse another screen persisted.
 * Honouring it there would hide the agent on a screen carrying no control to bring it back.
 *
 * `chat={null}` drops the column entirely (`data-chat='none'`). No screen passes it today —
 * Background was the one case, and it now fills the column with the XRF entry form instead of
 * leaving it empty. The path stays because it is the honest answer for a stage that has neither
 * an agent nor an operator input: an empty 390px panel apologising for not existing is worse
 * than no panel.
 */
export function WorkArea({
  chat,
  children,
  collapsible = false,
}: {
  chat: ReactNode;
  children: ReactNode;
  collapsible?: boolean;
}) {
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(KEY) === '1';
    } catch {
      return false;
    }
  });

  const toggle = useCallback(() => {
    setCollapsed((c) => {
      const next = !c;
      try {
        localStorage.setItem(KEY, next ? '1' : '0');
      } catch {
        /* a private-mode browser is not a reason to break the layout */
      }
      return next;
    });
  }, []);

  // Cmd/Ctrl + \ — the conventional "toggle the side panel" binding in professional tools.
  // Bound only where the control exists: a shortcut that hid the agent on a screen with no way
  // to restore it would be a trap, and an operator who hit it by accident would have no clue.
  useEffect(() => {
    if (!collapsible) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === '\\' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        toggle();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [collapsible, toggle]);

  const state = chat === null ? 'none' : collapsible && collapsed ? 'collapsed' : 'open';

  return (
    <div className="work" data-chat={state}>
      {state === 'collapsed' && (
        /* `aria-label`, even though the rail carries a visible word: the name computed from its
           contents is just "Agent", which is a noun, not the thing the button does. The label
           names the action; the visible word stays the label. */
        <button
          type="button"
          className="work__rail"
          onClick={toggle}
          title="Show the agent (Ctrl/Cmd + \)"
          aria-label="Show the agent"
        >
          <i className="ti ti-layout-sidebar-left-expand" aria-hidden="true" />
          <span className="work__rail-label">Agent</span>
        </button>
      )}
      {state === 'open' && (
        /*
         * A plain box, deliberately not an <aside>. What goes in here is `AgentPanel`, which is
         * already an <aside> carrying its own stage-specific label ("Regulatory agent") — so a
         * landmark here would nest a second `complementary` region around it, named more vaguely
         * than the one inside it. Two nested landmarks where the outer one adds nothing is noise
         * a screen reader has to walk past.
         */
        <div className="work__chat">
          {collapsible && (
            <button
              type="button"
              className="work__collapse"
              onClick={toggle}
              title="Collapse the agent (Ctrl/Cmd + \)"
              aria-label="Collapse the agent"
            >
              <i className="ti ti-layout-sidebar-left-collapse" aria-hidden="true" />
            </button>
          )}
          {chat}
        </div>
      )}
      <div className="work__artifact">{children}</div>
    </div>
  );
}
