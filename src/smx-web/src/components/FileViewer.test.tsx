import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FileViewer } from './FileViewer';

const detail = {
  summary: {
    id: 'reg_abc',
    kind: 'reg' as const,
    title: 'REACH Annex XVII',
    subtitle: 'ECHA · official 2025-11-20',
    available: true,
    state: 'available' as const,
    contentType: 'text/html',
    officialDate: '2025-11-20',
    ingestedUtc: '20260701T031400Z',
  },
  provenance: [{ label: 'Authority', value: 'ECHA', kind: 'text' as const }],
  unavailableReason: null,
  unavailableDetail: null,
  supersededById: null,
};

const stub = (overrides: Partial<Record<string, unknown>> = {}) => {
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      if (url.includes('/text'))
        return Promise.resolve(
          new Response(
            JSON.stringify([{ ordinal: 0, text: 'chunk body', entryId: '27', section: null }]),
            { headers: { 'Content-Type': 'application/json' } },
          ),
        );
      if (url.includes('/content'))
        return Promise.resolve(
          new Response('<p>original</p>', { headers: { 'Content-Type': 'text/html' } }),
        );
      return Promise.resolve(
        new Response(JSON.stringify({ ...detail, ...overrides }), {
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
};

/** A 503 ProblemDetails — the shape the backend emits when the library is not configured. */
const unavailable = () =>
  new Response(
    JSON.stringify({
      title: 'Document library unavailable',
      detail:
        'The document library is not configured on this deployment: IDocumentContentStore is not registered.',
      status: 503,
    }),
    { status: 503, headers: { 'Content-Type': 'application/problem+json' } },
  );

// Unmount before the URL stub comes off — jsdom implements neither createObjectURL nor
// revokeObjectURL, and Vitest runs afterEach hooks in reverse registration order.
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('FileViewer', () => {
  it('shows the title and provenance, and opens on the original', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText('REACH Annex XVII')).toBeInTheDocument();
    expect(await screen.findByText('ECHA')).toBeInTheDocument();
    expect(await screen.findByTitle('REACH Annex XVII')).toBeInTheDocument();
  });

  it('switches to the chunk view and names the count', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" />);
    const tab = await screen.findByRole('tab', { name: /what the agent read/i });
    await waitFor(() => expect(tab.textContent).toContain('1 chunk'));
    await userEvent.click(tab);
    expect(await screen.findByText('chunk body')).toBeInTheDocument();
  });

  // Arriving from a citation is the anchored case, and it must land on the chunk view —
  // the original has no anchor to land on.
  it('opens directly on the chunk view when anchored', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" anchorEntry="27" />);
    expect(await screen.findByText('chunk body')).toBeInTheDocument();
  });

  it('surfaces a superseded banner', async () => {
    stub({ supersededById: 'sds_newer', summary: { ...detail.summary, state: 'superseded' } });
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText(/superseded/i)).toBeInTheDocument();
  });

  /**
   * REACHABLE IN NORMAL OPERATION, which is why this asserts more than a sentence. A citation chip
   * mints its link from an id recorded at retrieval time, and two ordinary things invalidate one: a
   * `reg` document deleted from the corpus stays in the cached index for up to ten minutes, and an
   * SDS whose registry row was removed leaves an id that resolves to nothing. Landing here must name
   * the identifier (so the operator can say WHICH document is gone) and offer a way onward.
   */
  it('reports a document that is not found, naming the id and offering the library', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('', { status: 404 }))));
    render(<FileViewer documentId="reg_missing" />, { wrapper: MemoryRouter });
    await waitFor(() => expect(screen.getByText(/Document not found/i)).toBeInTheDocument());
    expect(screen.getByText('reg_missing')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /document library/i })).toHaveAttribute('href', '/docs');
  });

  /**
   * A 503 is a claim about the deployment, not about the document — so the client throws it
   * rather than collapsing it into "no file". If the viewer does not catch it, the promise
   * rejects unhandled and the operator watches a spinner forever, which is the one thing
   * spec §8 forbids: a fault that is silent.
   */
  it('says the library is unavailable rather than spinning forever', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string) =>
        url.includes('/content') || url.includes('/text')
          ? Promise.resolve(unavailable())
          : Promise.resolve(
              new Response(JSON.stringify(detail), {
                headers: { 'Content-Type': 'application/json' },
              }),
            ),
      ),
    );
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText(/is not configured on this deployment/i)).toBeInTheDocument();
  });

  /**
   * The content store and the index are separately configured, so one can be unreachable while
   * the other answers. Losing the whole screen to a failure on one tab would hide provenance
   * and chunks that arrived perfectly well.
   */
  it('keeps the chunks when only the content store is unreachable', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string) => {
        if (url.includes('/content')) return Promise.resolve(unavailable());
        if (url.includes('/text'))
          return Promise.resolve(
            new Response(
              JSON.stringify([{ ordinal: 0, text: 'chunk body', entryId: null, section: null }]),
              { headers: { 'Content-Type': 'application/json' } },
            ),
          );
        return Promise.resolve(
          new Response(JSON.stringify(detail), { headers: { 'Content-Type': 'application/json' } }),
        );
      }),
    );
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText(/is not configured on this deployment/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('tab', { name: /what the agent read/i }));
    expect(await screen.findByText('chunk body')).toBeInTheDocument();
  });

  /** A failed detail fetch is fatal — there is no header, no provenance, nothing to show. */
  it('reports a failure to load the document itself', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(unavailable())));
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText(/is not configured on this deployment/i)).toBeInTheDocument();
  });

  /**
   * The viewer is a long-lived mount that swaps documents under itself. Detail resolves before
   * the body does, so without a reset there is a window where the NEW document's title and the
   * OLD document's chunks are on screen together — text attributed to the wrong document, on
   * the one surface built to make attribution checkable.
   */
  it('never shows the previous document’s chunks under the next document’s title', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string) => {
        const two = url.includes('reg_two');
        if (url.includes('/text') || url.includes('/content')) {
          // The second document's body never arrives, which is exactly the window under test.
          if (two) return new Promise<Response>(() => {});
          return Promise.resolve(
            url.includes('/text')
              ? new Response(
                  JSON.stringify([
                    { ordinal: 0, text: 'annex XVII entry 27', entryId: null, section: null },
                  ]),
                  { headers: { 'Content-Type': 'application/json' } },
                )
              : new Response('one', { headers: { 'Content-Type': 'text/plain' } }),
          );
        }
        return Promise.resolve(
          new Response(
            JSON.stringify({
              ...detail,
              summary: {
                ...detail.summary,
                id: two ? 'reg_two' : 'reg_abc',
                title: two ? 'REACH Annex XIV' : 'REACH Annex XVII',
              },
            }),
            { headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }),
    );

    const { rerender } = render(<FileViewer documentId="reg_abc" />);
    await userEvent.click(await screen.findByRole('tab', { name: /what the agent read/i }));
    expect(await screen.findByText('annex XVII entry 27')).toBeInTheDocument();

    rerender(<FileViewer documentId="reg_two" />);

    expect(await screen.findByText('REACH Annex XIV')).toBeInTheDocument();
    expect(screen.queryByText('annex XVII entry 27')).toBeNull();
    // And the tab must not carry the previous document's chunk count either.
    expect(screen.getByRole('tab', { name: /what the agent read/i }).textContent).not.toMatch(
      /chunk/i,
    );
  });

  /**
   * MSAL bearer tokens do not ride on a plain anchor navigation — the same constraint that
   * forced the content itself to be fetched and rendered from memory. A `<a href="/api/…">`
   * would 401 in every deployed environment, so the download is served from the bytes the
   * viewer already holds.
   */
  it('downloads from the bytes already in memory rather than navigating to the API', async () => {
    stub();
    const createObjectURL = vi.fn(() => 'blob:download');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL });
    const clicked: HTMLAnchorElement[] = [];
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(function (this: HTMLAnchorElement) {
        clicked.push(this);
      });
    try {
      render(<FileViewer documentId="reg_abc" />);
      const link = await screen.findByRole('link', { name: /download/i });
      await waitFor(() => expect(link).not.toHaveAttribute('aria-disabled', 'true'));

      // fireEvent returns false when the handler called preventDefault — i.e. when the browser
      // was stopped from navigating to the unauthenticated URL.
      expect(fireEvent.click(link)).toBe(false);

      expect(createObjectURL).toHaveBeenCalledTimes(1);
      expect(clicked).toHaveLength(1);
      expect(clicked[0].download).toBe('REACH-Annex-XVII.html');
      // And the URL is released once the download has started — a leaked object URL pins its
      // blob for the life of the tab.
      await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:download'));
    } finally {
      click.mockRestore();
    }
  });
});
