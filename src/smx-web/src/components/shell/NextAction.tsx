import { useCallback, useId, useState } from 'react';
import { Link } from 'react-router-dom';
import { NotFound, startProject } from '../../api/client';
import type { ProjectSummary } from '../../api/types';
import { nextAction } from '../../domain/nextAction';

/**
 * The one thing that needs a human, at the top of the artifact column.
 *
 * This is the answer to "what do I do now", which the app previously never gave: the blocking
 * reason was a grey sentence in a status bar, at the same size and weight as everything around
 * it, with the control that acts on it somewhere further down the page.
 *
 * **The element is always mounted; only its contents come and go.** That is not an accident of
 * structure, it is the whole announcement mechanism. Most screen readers announce MUTATIONS
 * INSIDE a live region that was already in the accessibility tree when the mutation happened;
 * a region that is inserted already populated is, to them, just more page. So a component that
 * returned `null` when nothing was blocked would announce nothing on the one transition that
 * matters — the poll loop finding a park while the operator is thirty rows into a matrix, which
 * is precisely when they are not looking at the top of the screen.
 *
 * `display: none` is not available as the empty state for the same reason: it takes the element
 * out of the accessibility tree, which is the situation this is built to avoid. So the empty
 * region is styled to nothing instead — no border, no padding, no margin, no background
 * (`.next[data-empty]`, styles/shell.css). An "all clear" band on every screen of a running
 * project would be furniture, and furniture is what teaches the eye to skip a region.
 *
 * **One action here is a write, not a link, and so this component performs it.** Start Processing
 * is the most consequential press in the app and it used to sit three sections down the intake
 * screen, inside the brief. It is now here and nowhere else — which means the block that states
 * the action also owns calling it, the busy state while it is in flight, and what to say when it
 * fails. Handing that back up to `ProjectLayout` as a prop would only move the same three pieces
 * of state one level away from the button they belong to.
 */
export function NextAction({
  project,
  refreshProject,
}: {
  project: ProjectSummary;
  /** Re-read the record after a write — the server decides what the stage status now is. */
  refreshProject: () => void;
}) {
  // Not a hardcoded string. Two of these on one page — or anything else that happened to reuse
  // the name — would put duplicate ids in the document, and an ambiguous `aria-labelledby` names
  // whichever element the browser found first, which is to say the wrong one.
  const titleId = useId();
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const action = nextAction(project);

  const start = useCallback(async () => {
    setBusy(true);
    setFailure(null);
    try {
      const result = await startProject(project.projectId);
      // A 404 here means the project is gone from under the operator. Silence would leave them
      // looking at a button they just pressed, believing the analysis is running.
      if (result === NotFound) {
        setFailure(`Could not start: no project with id ${project.projectId}.`);
        return;
      }
      // Re-read rather than patching local state: the server decides what the stage status now
      // is, and a second press is idempotent (it replies with the CURRENT status, it does not
      // re-dispatch).
      refreshProject();
    } catch (e) {
      setFailure(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [project.projectId, refreshProject]);

  return (
    /*
     * The live region is this box, not the heading. `role="status"` on an <h2> would REPLACE the
     * heading role rather than add to it — an element has one computed role — so the navigation
     * landmark a screen-reader user actually uses to find this would disappear. `aria-live` is a
     * property, so it layers on a container without touching any role.
     *
     * It wraps the title AND the sentence because they change together: half an announcement
     * ("Record the VP determination") without its body is the half that leaves out what it is for.
     *
     * `aria-labelledby` is conditional along with the content. An empty <section> with a dangling
     * `aria-labelledby` would be a landmark named by an element that is not in the document; with
     * neither, it is a plain generic box, which is what an empty one should be.
     */
    <section
      className="next"
      data-tone={action?.tone}
      data-empty={action ? undefined : ''}
      aria-live="polite"
      aria-labelledby={action ? titleId : undefined}
    >
      {action && (
        <>
          <i className={`ti ${action.icon} next__icon`} aria-hidden="true" />
          <div className="next__body">
            <h2 className="next__title" id={titleId}>
              {action.title}
            </h2>
            <p className="next__text">{action.body}</p>
            {/* Verbatim, in mono — a paraphrased agent error is a lost one. */}
            {action.detail && <p className="next__detail data">{action.detail}</p>}
            {action.cta &&
              (action.cta.perform ? (
                <button
                  className="btn primary next__cta"
                  type="button"
                  disabled={busy}
                  onClick={() => void start()}
                >
                  {busy ? 'Starting…' : action.cta.label}
                </button>
              ) : (
                <Link className="btn primary next__cta" to={action.cta.to}>
                  {action.cta.label}
                </Link>
              ))}
            {/* No `role="alert"` on this: the whole block is already an `aria-live` region, and a
                nested assertive region inside a polite one is announced twice or not at all
                depending on the reader. The failure appearing here IS the mutation the region
                exists to announce. */}
            {failure && <p className="next__detail">{failure}</p>}
          </div>
        </>
      )}
    </section>
  );
}
