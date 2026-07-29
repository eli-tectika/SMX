import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';
import { Finder } from '../Finder';

/**
 * One line: where you came from, what you are looking at, who it is for.
 *
 * This replaces the identity half of ContextBar. What is deliberately NOT here: the project id
 * (it identifies a record, not a product, and the operator does not read it to know where they
 * are), the corpus stamp (a property of the instrument, not this project), and the poll ticker
 * (the next-action block already changes when the record does).
 *
 * UNRESOLVED, for whoever wires this in: `AppShell`'s masthead already mounts a `<Finder />` on
 * every route, this one included. Two mounted Finders means two global ⌘K listeners, so the
 * shortcut opens two stacked dialogs — one of them has to go. Either the masthead drops it while
 * a project is open, or this header does; the design (`2026-07-29-webapp-ux-redesign-design.md`,
 * "The shell") draws it here, but it draws no masthead at all, so it does not settle the question.
 */
export function ProjectHeader({ project }: { project: ProjectSummary }) {
  return (
    <header className="phead">
      <Link to="/" className="phead__back">
        <i className="ti ti-chevron-left" aria-hidden="true" />
        Projects
      </Link>
      {/* The app's only <h1>. Nothing above this in the tree sets one — the masthead is a brand
          lockup, not a heading — so the product name is the page's title, which is what it is. */}
      <h1 className="phead__product">{project.product}</h1>
      <span className="phead__client">{project.client}</span>
      <div className="phead__end">
        <Finder />
      </div>
    </header>
  );
}
