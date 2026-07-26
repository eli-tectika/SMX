import { useLayoutEffect, useRef } from 'react';
import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { whatsBlocking } from '../domain/blocking';
import { StageSpine } from './StageSpine';
import { Data } from './ui/Data';

/**
 * The project context bar — sticky, pinned directly beneath the masthead.
 *
 * The masthead is a compact brand/utility top bar (logo, finder, corpus stamp); this is
 * the per-project status board. Thirty rows into a compatibility matrix you need to know
 * which project you are in and what it is waiting on — so this pins where it stays useful.
 *
 * z-index must clear the matrix's own sticky `thead` (craft.css puts it at 2, and its
 * corner cell at 3). They do not compete for scroll — the table has its own container —
 * but they do compete for paint order.
 */
export function ContextBar({ project }: { project: ProjectSummary }) {
  /**
   * The needle.
   *
   * A project runs in bursts across days, parking in an explicit `awaiting <X>` state each time
   * it needs a human. `whatsBlocking` folds the record into one prioritised sentence naming the
   * wait and whom it is on — and it used to render only on the dashboard card, so the operator
   * lost it the moment they opened the project. The dashboard calls the SAME function, so the two
   * surfaces cannot drift apart.
   *
   * No matrix summary is passed: this bar is on every stage screen, and fetching the matrix to
   * render a status line would make the whole workspace wait on it. The matrix-derived rules
   * (inconsistent, uncited, unopened-flagged) stay the dashboard's job, where the summary is
   * already loaded.
   */
  const blocking = whatsBlocking(project, undefined, 0, 'project');

  /**
   * "Settled" is a claim, not a default. `whatsBlocking` returning `null` means only "no rule
   * matched" — for a well-formed record that happens to coincide with every stage being done,
   * but `stages` is an untyped `Record<string, StageState>` with no runtime validation. An empty
   * record, or a tenth status a future backend change introduces, would also fail to match any
   * rule and must NOT be allowed to paint a green "All stages settled" over a record we do not
   * actually understand. So settled is asserted positively — every stage present and `done` — and
   * the fallback for the genuinely unclassifiable case is a muted "status unknown", never success.
   * (The dashboard card has the easier version of this problem: it renders nothing at all when
   * `whatsBlocking` returns null, because a card can just omit the line — a status bar cannot.)
   */
  const stageList = Object.values(project.stages);
  const settled = stageList.length > 0 && stageList.every((s) => s.status === 'done');
  const tone = blocking ? blocking.tone : settled ? 'success' : 'muted';
  const nextText = blocking ? blocking.text : settled ? 'All stages settled' : 'Status unknown';
  const nextIcon = blocking ? blocking.icon : settled ? 'ti-check' : 'ti-help-circle';

  const ref = useRef<HTMLDivElement>(null);

  /**
   * The bar's height is now a function of the RECORD — a settled project gets one short line, a
   * halted one gets a sentence plus a verbatim error — so the sticky stack can no longer agree on
   * a constant. `--ctxbar-h` was 74px against a real 101–132px, and everything that pins beneath
   * it (the agent dock) was being painted over by an opaque z-index:15 bar, taking its collapse
   * control with it. Measuring and publishing is the only version of this that stays true.
   *
   * `borderBoxSize`, not `contentRect`: the dock's `top` has to clear the bar's border-box
   * bottom edge (content + padding + border), and `contentRect` excludes the padding and
   * border — 25px of them here (12 top + 12 bottom + 1 border), which is exactly the constant
   * overlap a first pass at this left behind. `getBoundingClientRect()` is the fallback for the
   * browsers where `borderBoxSize` isn't reported.
   */
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    // jsdom (our test environment) has no ResizeObserver; the constant fallback in tokens.css
    // covers that case, and a real browser is where this actually needs to be right.
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(([entry]) =>
      document.documentElement.style.setProperty(
        '--ctxbar-h',
        `${entry.borderBoxSize?.[0]?.blockSize ?? el.getBoundingClientRect().height}px`,
      ),
    );
    ro.observe(el);
    return () => {
      ro.disconnect();
      // Otherwise <html> keeps the last mounted bar's height after this one unmounts, and the
      // NEXT ContextBar (a different project, a different stage) paints its first frame — before
      // its own observer fires — against a stale value. Going from a tall (failed) bar to a short
      // (settled) one is merely wrong for a frame; going the other way is a one-frame occlusion
      // of the dock again, the exact bug this effect exists to prevent.
      document.documentElement.style.removeProperty('--ctxbar-h');
    };
  }, []);

  return (
    <div className="ctxbar" ref={ref}>
      <div className="ctxbar__row">
        <Link to="/" className="ctxbar__back" title="All projects">
          <i className="ti ti-chevron-left" aria-hidden="true" />
          Projects
        </Link>

        <span className="ctxbar__sep" aria-hidden="true" />

        <span className="ctxbar__product">{project.product}</span>
        <span className="ctxbar__meta">
          client {project.client} · <Data kind="id">{project.projectId}</Data>
        </span>

        {/* Never a celebration — a settled project is quiet (see the motion policy in craft.css). */}
        <span className="ctxbar__next" data-tone={tone}>
          <i
            className={`ti ${nextIcon}`}
            aria-hidden="true"
            data-running={blocking?.icon === 'ti-loader' ? '' : undefined}
          />
          {/* The one thing on screen that changes when the poll loop finds a transition, and the
              operator may be thirty rows into a matrix when it does — so it announces itself. */}
          <span role="status" aria-live="polite">
            {nextText}
            {blocking?.detail && <span className="ctxbar__detail data">{blocking.detail}</span>}
          </span>
        </span>
      </div>

      <StageSpine project={project} />
    </div>
  );
}
