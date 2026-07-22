import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DocumentContent } from './DocumentContent';

/**
 * Unmount BEFORE the URL stub comes off. jsdom implements neither createObjectURL nor
 * revokeObjectURL, and Vitest runs afterEach hooks in reverse registration order — so
 * RTL's auto-cleanup would otherwise fire the revoke against the real (function-less)
 * URL and every object-URL test would die in teardown rather than on its assertions.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const bytes = (body: string, contentType: string) => ({
  blob: new Blob([body], { type: contentType }),
  contentType,
});

describe('DocumentContent — what may be rendered, and how', () => {
  /**
   * THE invariant of this component (design D7).
   *
   * A blob: URL inherits the origin of the document that created it. Regulatory HTML is
   * fetched from the open web; rendering it in an origin-inheriting frame would be stored
   * XSS against the operator's session — with access to their MSAL tokens.
   *
   * srcdoc + sandbox="" grants neither allow-scripts nor allow-same-origin. If a later
   * change "optimizes" this into an object URL for consistency with the PDF path, this
   * test is what stops it.
   */
  it('renders HTML in a fully sandboxed srcdoc frame and never an object URL', async () => {
    const createObjectURL = vi.fn(() => 'blob:should-never-be-called');
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL: vi.fn() });

    render(<DocumentContent content={bytes('<h1>REACH</h1>', 'text/html')} title="REACH" />);

    const frame = await screen.findByTitle('REACH');
    expect(frame).toHaveAttribute('sandbox', '');
    expect(frame.getAttribute('srcdoc')).toContain('<h1>REACH</h1>');
    expect(frame).not.toHaveAttribute('src');
    expect(createObjectURL).not.toHaveBeenCalled();
  });

  /**
   * The same invariant stated as a capability rather than a mechanism, so that relaxing
   * `sandbox=""` into a token list fails here too. Either dangerous token alone re-arms the
   * attack: allow-scripts runs the document's code, allow-same-origin hands it our cookies
   * and MSAL token cache.
   *
   * Read off the attribute rather than the `sandbox` DOMTokenList property — jsdom does not
   * reflect that property, so asserting on it would pass vacuously here.
   */
  it('grants the HTML frame no sandbox token at all', async () => {
    render(<DocumentContent content={bytes('<h1>REACH</h1>', 'text/html')} title="REACH" />);
    const frame = await screen.findByTitle('REACH');
    const sandbox = frame.getAttribute('sandbox');
    expect(sandbox).not.toBeNull();
    expect(sandbox!.split(/\s+/).filter(Boolean)).toEqual([]);
  });

  it('renders a PDF through an object URL', async () => {
    const createObjectURL = vi.fn(() => 'blob:pdf-url');
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL: vi.fn() });

    render(<DocumentContent content={bytes('%PDF-1.4', 'application/pdf')} title="Silver nitrate" />);

    const frame = await screen.findByTitle('Silver nitrate');
    expect(frame).toHaveAttribute('src', 'blob:pdf-url');
    expect(createObjectURL).toHaveBeenCalledTimes(1);
  });

  // Leaking object URLs pin their blobs in memory for the life of the document.
  it('revokes the object URL on unmount', async () => {
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { ...URL, createObjectURL: vi.fn(() => 'blob:pdf-url'), revokeObjectURL });

    const { unmount } = render(
      <DocumentContent content={bytes('%PDF', 'application/pdf')} title="x" />,
    );
    await screen.findByTitle('x');
    unmount();

    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:pdf-url'));
  });

  /**
   * Unmount is the easy half. The viewer is a long-lived mount that swaps documents under
   * itself (the overlay, and /docs/:id when a superseded banner is followed), so the leak
   * that actually accumulates is the one on change — and the frame must point at the new
   * document, never at the revoked URL of the old one.
   */
  it('revokes the previous object URL when the document changes while mounted', async () => {
    const revokeObjectURL = vi.fn();
    let n = 0;
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => `blob:pdf-${++n}`),
      revokeObjectURL,
    });

    const { rerender } = render(
      <DocumentContent content={bytes('%PDF one', 'application/pdf')} title="x" />,
    );
    expect(await screen.findByTitle('x')).toHaveAttribute('src', 'blob:pdf-1');

    rerender(<DocumentContent content={bytes('%PDF two', 'application/pdf')} title="x" />);

    await waitFor(() => expect(screen.getByTitle('x')).toHaveAttribute('src', 'blob:pdf-2'));
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:pdf-1');
  });

  /**
   * The text path has the same staleness hazard without the leak: the body arrives
   * asynchronously, so a swapped document would otherwise show the PREVIOUS document's text
   * under the new document's title until the read resolved. On a provenance surface, bytes
   * attributed to the wrong document is the failure this feature exists to prevent.
   */
  it('never shows the previous document’s text after a change', async () => {
    const { rerender } = render(
      <DocumentContent content={bytes('annex XVII entry 27', 'text/plain')} title="x" />,
    );
    expect(await screen.findByText('annex XVII entry 27')).toBeInTheDocument();

    rerender(<DocumentContent content={bytes('annex XIV entry 3', 'text/plain')} title="x" />);

    expect(screen.queryByText('annex XVII entry 27')).toBeNull();
    expect(await screen.findByText('annex XIV entry 3')).toBeInTheDocument();
  });

  it.each([
    ['text/plain', 'plain text body'],
    ['text/csv', 'a,b,c'],
    ['application/json', '{"a":1}'],
    ['application/xml', '<root/>'],
  ])('renders %s as escaped text', async (contentType, body) => {
    render(<DocumentContent content={bytes(body, contentType)} title="x" />);
    expect(await screen.findByText(body)).toBeInTheDocument();
  });

  // An unknown type must not be handed to a renderer that might interpret it.
  it('offers download instead of rendering an unknown type', async () => {
    render(
      <DocumentContent
        content={bytes('', 'application/octet-stream')}
        title="x"
        downloadHref="/api/documents/x/content?download=1"
      />,
    );
    expect(await screen.findByText(/cannot be displayed/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /download/i })).toBeInTheDocument();
  });

  // Spec §8: over the cap, offer the file rather than melting the tab.
  it('refuses to render inline above the size cap', async () => {
    const big = {
      blob: new Blob([new Uint8Array(26 * 1024 * 1024)], { type: 'application/pdf' }),
      contentType: 'application/pdf',
    };
    render(<DocumentContent content={big} title="x" downloadHref="/dl" />);
    expect(await screen.findByText(/25 MB/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /download/i })).toBeInTheDocument();
  });

  it('states the reason when there are no bytes at all', async () => {
    render(<DocumentContent content={null} title="x" unavailableDetail="3 fetch attempts failed" />);
    expect(await screen.findByText(/3 fetch attempts failed/)).toBeInTheDocument();
  });
});
