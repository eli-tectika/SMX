import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { NextAction } from './NextAction';
import type { ProjectSummary, StageState } from '../../api/types';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'Danone',
  product: 'Alpine Spring 1.5L PET',
  stages,
});

function block(p: ProjectSummary) {
  return render(
    <MemoryRouter initialEntries={['/p/p1/regulatory']}>
      <NextAction project={p} />
    </MemoryRouter>,
  );
}

describe('NextAction', () => {
  /**
   * An "all clear" band would be furniture on every screen of a running project, and furniture is
   * what teaches the eye to skip the region — including on the day it does carry the thing that
   * needs a human. So there is no content, no title and no landmark name when nothing is blocked.
   */
  it('shows nothing when nothing needs a human', () => {
    const { container } = block(project({ intake: { status: 'done', attempts: 1 } }));
    expect(container.querySelector('.next')).toBeEmptyDOMElement();
    expect(container.querySelector('.next')).not.toHaveAttribute('aria-labelledby');
    expect(container.querySelector('.next')).not.toHaveAttribute('data-tone');
  });

  /**
   * …and yet the ELEMENT is there, because it is the live region.
   *
   * Most screen readers announce mutations INSIDE a region that was already in the accessibility
   * tree; a region inserted already populated is just more page to them. Returning `null` here
   * would therefore announce nothing on the exact transition that matters — the poll loop finding
   * a park while the operator is thirty rows into a matrix. So the region is mounted persistently
   * and only its contents toggle. `data-empty` is what styles it to nothing; `display: none` is
   * NOT available, because that takes the region back out of the tree and undoes the point.
   */
  it('keeps the live region mounted when nothing is blocked, so it can announce later', () => {
    const { container } = block(project({ intake: { status: 'done', attempts: 1 } }));
    const live = container.querySelector('[aria-live]');
    expect(live).not.toBeNull();
    expect(live).toHaveAttribute('aria-live', 'polite');
    expect(live).toHaveAttribute('data-empty');
  });

  /**
   * The transition itself: the SAME element must survive a settled → parked update, or the
   * announcement is an insertion again and we are back where we started.
   */
  it('fills the region it already mounted when the record parks', () => {
    const { container, rerender } = block(project({ regulatory: { status: 'done', attempts: 1 } }));
    const before = container.querySelector('[aria-live]');
    expect(before).toBeEmptyDOMElement();

    rerender(
      <MemoryRouter initialEntries={['/p/p1/regulatory']}>
        <NextAction project={project({ regulatory: { status: 'awaiting-RE', attempts: 1 } })} />
      </MemoryRouter>,
    );
    expect(container.querySelector('[aria-live]')).toBe(before);
    expect(before).not.toHaveAttribute('data-empty');
    expect(before).toHaveTextContent('Record the R.E. determination');
  });

  it('renders the title, the body and a working link when there is a cta', () => {
    block(project({ regulatory: { status: 'awaiting-RE', attempts: 1 } }));
    expect(screen.getByRole('heading', { name: 'Record the R.E. determination' })).toBeInTheDocument();
    expect(
      screen.getByText(/parked until the Regulatory Expert rules on the elements you sent/i),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Record determination' })).toHaveAttribute(
      'href',
      '/p/p1/regulatory',
    );
  });

  /**
   * The park with no control. The XRF measurement happens on an instrument outside this app and
   * dosing resumes on its own — a button here would claim an action that does not exist. The block
   * still renders: what is being waited on is exactly what the operator needs to read.
   */
  it('renders a park that carries no cta, with no link', () => {
    block(project({ dosing: { status: 'awaiting-physics', attempts: 1 } }));
    expect(screen.getByRole('heading', { name: 'Waiting on the physics team' })).toBeInTheDocument();
    expect(screen.getByText(/needs a measured XRF background/i)).toBeInTheDocument();
    expect(screen.queryByRole('link')).toBeNull();
  });

  /** Verbatim. A paraphrased agent error is a lost one. */
  it('renders the detail exactly as the record wrote it', () => {
    block(
      project({
        discovery: { status: 'failed', attempts: 2, error: 'search_web timed out after 30s' },
      }),
    );
    expect(screen.getByText('search_web timed out after 30s')).toBeInTheDocument();
  });

  /** No detail on the record means no empty line pretending there was one. */
  it('renders no detail line when the record carries none', () => {
    const { container } = block(project({ decision: { status: 'awaiting-VP', attempts: 1 } }));
    expect(container.querySelector('.next__detail')).toBeNull();
  });

  /** The tone drives the whole banner's palette; a halted stage is the danger case. */
  it('carries the tone and the icon the domain chose', () => {
    const { container } = block(
      project({ discovery: { status: 'failed', attempts: 2, error: 'boom' } }),
    );
    expect(container.querySelector('.next')).toHaveAttribute('data-tone', 'danger');
    expect(container.querySelector('.next__icon')).toHaveClass('ti-alert-triangle');
  });

  /**
   * `aria-labelledby` needs an id that is unique in the DOCUMENT, not in the component. A
   * hardcoded one collides the moment two of these are on a page (or anything else reuses the
   * string), and an ambiguous `aria-labelledby` names the wrong region.
   */
  it('labels the region with an id that is unique per instance', () => {
    const { container } = render(
      <MemoryRouter>
        <NextAction project={project({ regulatory: { status: 'awaiting-RE', attempts: 1 } })} />
        <NextAction project={project({ decision: { status: 'awaiting-VP', attempts: 1 } })} />
      </MemoryRouter>,
    );
    const ids = [...container.querySelectorAll('.next__title')].map((el) => el.id);
    expect(ids).toHaveLength(2);
    expect(ids[0]).toBeTruthy();
    expect(new Set(ids).size).toBe(2);
    // And each region points at its OWN title.
    [...container.querySelectorAll('.next')].forEach((region, i) => {
      expect(region.getAttribute('aria-labelledby')).toBe(ids[i]);
    });
  });

  /**
   * The record can change under the operator while they are thirty rows into a matrix, and the
   * change of what needs a human is the one thing that must not go unannounced. The live region
   * is NOT the heading: `role="status"` on an <h2> replaces the heading role rather than adding
   * to it, and a screen reader would lose the heading it uses to navigate here.
   */
  it('announces politely without giving up the heading', () => {
    const { container } = block(project({ regulatory: { status: 'awaiting-RE', attempts: 1 } }));
    const live = container.querySelector('[aria-live]')!;
    expect(live).not.toBeNull();
    expect(live.getAttribute('aria-live')).toBe('polite');
    // The heading is inside the live region, and is still a heading.
    expect(live.contains(screen.getByRole('heading', { level: 2 }))).toBe(true);
    expect(screen.getByRole('heading', { level: 2 })).not.toHaveAttribute('role');
  });
});
