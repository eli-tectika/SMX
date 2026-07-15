import { useCallback, useEffect, useState, type ReactNode } from 'react';

const KEY = 'smx.dockCollapsed';

/**
 * The agent dock.
 *
 * Spec §2 calls the agent panel "right, docked, always present" — and it was none of those
 * things. It sat in an ordinary grid column inside the 1100px page wrapper, so it scrolled
 * away with the content, and it was sized `minmax(280px, 1fr)`, which meant that on a wide
 * monitor it *grew*, taking the space away from the compatibility matrix. That is exactly
 * backwards: the matrix is the subject, the dock is the tool.
 *
 * So the dock now has a width, and the canvas gets everything else.
 *
 * **It collapses to a rail, never to zero.** "Always present" is doctrine, not decoration:
 * the agent is the only thing the operator may instruct, and an interface where the command
 * surface can be dismissed entirely quietly invites the operator to work around it — which,
 * in an app whose central rule is "no direct edits to agent output, instruct with a reason",
 * is the one habit that must not form. Collapsed, it is a 44px rail. It is still there.
 */
export function Dock({ children, panel }: { children: ReactNode; panel: ReactNode }) {
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
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === '\\' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        toggle();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [toggle]);

  return (
    <div className="workspace" data-dock={collapsed ? 'collapsed' : 'open'}>
      <div className="workspace__canvas">{children}</div>

      <aside className="dock" aria-label="Agent dock">
        {collapsed ? (
          <button
            type="button"
            className="dock__rail"
            onClick={toggle}
            title="Show the agent dock (Ctrl/Cmd + \)"
          >
            <i className="ti ti-layout-sidebar-right-expand" aria-hidden="true" />
            <span className="dock__rail-label">Agent</span>
          </button>
        ) : (
          <>
            <button
              type="button"
              className="dock__collapse"
              onClick={toggle}
              title="Collapse the agent dock (Ctrl/Cmd + \)"
              aria-label="Collapse the agent dock"
            >
              <i className="ti ti-layout-sidebar-right-collapse" aria-hidden="true" />
            </button>
            {panel}
          </>
        )}
      </aside>
    </div>
  );
}
