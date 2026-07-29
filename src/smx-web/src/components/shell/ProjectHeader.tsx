import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../../api/types';

/**
 * One line: where you came from, what you are looking at, who it is for.
 *
 * This replaces the identity half of ContextBar. What is deliberately NOT here: the project id
 * (it identifies a record, not a product, and the operator does not read it to know where they
 * are), the corpus stamp (a property of the instrument, not this project), and the poll ticker
 * (the next-action block already changes when the record does).
 *
 * NO FINDER, though the design sketch draws one on this line. `AppShell`'s masthead already
 * mounts a global `<Finder />` and it renders on `/p/*` like everywhere else, so a second one
 * here would buy one duplicate button at the cost of a second global ⌘K listener — the shortcut
 * would open two stacked `aria-modal` dialogs. The sketch draws no masthead at all, which is why
 * it appears to be missing something. Do not add it back.
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
    </header>
  );
}
