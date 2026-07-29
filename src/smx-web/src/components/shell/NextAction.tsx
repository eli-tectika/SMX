import { useId } from 'react';
import { Link } from 'react-router-dom';
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
 */
export function NextAction({ project }: { project: ProjectSummary }) {
  // Not a hardcoded string. Two of these on one page — or anything else that happened to reuse
  // the name — would put duplicate ids in the document, and an ambiguous `aria-labelledby` names
  // whichever element the browser found first, which is to say the wrong one.
  const titleId = useId();
  const action = nextAction(project);

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
            {action.cta && (
              <Link className="btn primary next__cta" to={action.cta.to}>
                {action.cta.label}
              </Link>
            )}
          </div>
        </>
      )}
    </section>
  );
}
