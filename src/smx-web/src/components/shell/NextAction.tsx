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
 * It renders nothing when nothing is blocked. An empty "all clear" band would be furniture on
 * every screen of a running project, and furniture is what teaches the eye to skip a region.
 */
export function NextAction({ project }: { project: ProjectSummary }) {
  // Not a hardcoded string. Two of these on one page — or anything else that happened to reuse
  // the name — would put duplicate ids in the document, and an ambiguous `aria-labelledby` names
  // whichever element the browser found first, which is to say the wrong one.
  const titleId = useId();
  const action = nextAction(project);
  if (!action) return null;

  return (
    <section className="next" data-tone={action.tone} aria-labelledby={titleId}>
      <i className={`ti ${action.icon} next__icon`} aria-hidden="true" />
      {/*
       * The live region is this box, not the heading. `role="status"` on an <h2> would REPLACE
       * the heading role rather than add to it — an element has one computed role — so the
       * navigation landmark a screen-reader user actually uses to find this would disappear.
       * `aria-live` is a property, so it layers on a container without touching any role.
       *
       * It wraps the title AND the sentence because they change together: the poll loop can
       * swap the whole block while the operator is thirty rows into a matrix, and half an
       * announcement ("Record the VP determination") without its body is the half that leaves
       * out what it is for.
       */}
      <div className="next__body" aria-live="polite">
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
    </section>
  );
}
