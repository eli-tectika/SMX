import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NextAction } from './NextAction';
import type { ProjectSummary, StageState } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  startProject: vi.fn(),
}));
import * as api from '../../api/client';

const project = (stages: Record<string, StageState>): ProjectSummary => ({
  projectId: 'p1',
  client: 'Danone',
  product: 'Alpine Spring 1.5L PET',
  stages,
});

function block(p: ProjectSummary, refreshProject: () => void = vi.fn()) {
  return render(
    <MemoryRouter initialEntries={['/p/p1/regulatory']}>
      <NextAction project={p} refreshProject={refreshProject} />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(api.startProject).mockReset();
  vi.mocked(api.startProject).mockResolvedValue({ status: 'pending' });
});

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
        <NextAction
          project={project({ regulatory: { status: 'awaiting-RE', attempts: 1 } })}
          refreshProject={vi.fn()}
        />
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
        <NextAction
          project={project({ regulatory: { status: 'awaiting-RE', attempts: 1 } })}
          refreshProject={vi.fn()}
        />
        <NextAction
          project={project({ decision: { status: 'awaiting-VP', attempts: 1 } })}
          refreshProject={vi.fn()}
        />
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

/**
 * Start Processing — the one next action that is a WRITE.
 *
 * It was buried three sections down the intake screen, inside the brief panel. It is now this
 * block's own button and exists nowhere else in the app, which means this component performs it:
 * it calls the endpoint, holds the busy state, and says what happened when the call fails.
 */
describe('NextAction — starting the analysis', () => {
  const unstarted = () => project({ intake: { status: 'awaiting-confirmation', attempts: 0 } });

  /**
   * A <button>, not a <Link>. Sending the operator to the intake screen would land them on the
   * screen they are usually already on, with nothing to press when they got there.
   */
  it('renders the start action as a button rather than a link', () => {
    block(unstarted());
    expect(screen.getByRole('button', { name: /start processing/i })).toBeInTheDocument();
    expect(screen.queryByRole('link')).toBeNull();
  });

  it('starts the analysis on the press, then re-reads the record', async () => {
    const refreshProject = vi.fn();
    block(unstarted(), refreshProject);

    await userEvent.click(screen.getByRole('button', { name: /start processing/i }));

    expect(api.startProject).toHaveBeenCalledWith('p1');
    // The spine has to catch up with the record the press just changed.
    await waitFor(() => expect(refreshProject).toHaveBeenCalled());
  });

  /**
   * A 404 means the project is gone from under the operator. Silence would leave them looking at
   * a button they just pressed, believing the analysis is running.
   */
  it('says so when start finds no such project, and does not re-read the record', async () => {
    vi.mocked(api.startProject).mockResolvedValue(api.NotFound);
    const refreshProject = vi.fn();
    block(unstarted(), refreshProject);

    await userEvent.click(screen.getByRole('button', { name: /start processing/i }));

    expect(await screen.findByText(/no project with id p1/i)).toBeInTheDocument();
    expect(refreshProject).not.toHaveBeenCalled();
  });

  it('reports the failure verbatim when the call throws', async () => {
    vi.mocked(api.startProject).mockRejectedValue(new Error('502 Bad Gateway'));
    block(unstarted());

    await userEvent.click(screen.getByRole('button', { name: /start processing/i }));

    expect(await screen.findByText('502 Bad Gateway')).toBeInTheDocument();
  });

  /**
   * The failure text has to live INSIDE the live region, or the one message that says the press
   * did not work is the one thing a screen-reader user is never told.
   */
  it('puts the failure inside the live region', async () => {
    vi.mocked(api.startProject).mockResolvedValue(api.NotFound);
    const { container } = block(unstarted());

    await userEvent.click(screen.getByRole('button', { name: /start processing/i }));

    const live = container.querySelector('[aria-live]')!;
    expect(live).toHaveTextContent(/no project with id p1/i);
  });

  /** Feedback while the request is in flight — a button that looks idle invites a second press. */
  it('disables the button while the start is in flight', async () => {
    let release: (v: { status: string }) => void = () => {};
    vi.mocked(api.startProject).mockReturnValue(
      new Promise((resolve) => {
        release = resolve;
      }),
    );
    block(unstarted());

    await userEvent.click(screen.getByRole('button', { name: /start processing/i }));
    const button = screen.getByRole('button', { name: /starting/i });
    expect(button).toBeDisabled();

    release({ status: 'pending' });
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /start processing/i })).toBeEnabled(),
    );
  });
});
