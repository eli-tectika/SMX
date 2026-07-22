import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DocumentView } from './DocumentView';

const CHUNKS = [
  { ordinal: 147, text: 'nickel release limit', entryId: '27', section: null },
  { ordinal: 148, text: 'prolonged skin contact', entryId: '27', section: null },
];

const stub = () =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      if (url.includes('/text'))
        return Promise.resolve(
          new Response(JSON.stringify(CHUNKS), { headers: { 'Content-Type': 'application/json' } }),
        );
      if (url.includes('/content'))
        return Promise.resolve(new Response('original', { headers: { 'Content-Type': 'text/plain' } }));
      return Promise.resolve(
        new Response(
          JSON.stringify({
            summary: {
              id: 'reg_abc',
              kind: 'reg',
              title: 'REACH Annex XVII',
              subtitle: 'ECHA',
              available: true,
              state: 'available',
              contentType: 'text/plain',
              officialDate: null,
              ingestedUtc: null,
            },
            provenance: [],
            unavailableReason: null,
            unavailableDetail: null,
            supersededById: null,
          }),
          { headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }),
  );

const view = (path: string) =>
  render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/docs/:documentId" element={<DocumentView />} />
      </Routes>
    </MemoryRouter>,
  );

afterEach(() => vi.unstubAllGlobals());

describe('DocumentView — the citable mount', () => {
  it('anchors on the entry a citation carried', async () => {
    stub();
    view('/docs/reg_abc?entry=27');
    expect(await screen.findByText(/anchored to 2 chunks citing entry 27/i)).toBeInTheDocument();
    expect(screen.getAllByTestId('chunk-cited')).toHaveLength(2);
  });

  it('anchors on an explicit chunk ordinal', async () => {
    stub();
    view('/docs/reg_abc?chunk=148');
    expect(await screen.findByText(/anchored to 1 chunk at ordinal 148/i)).toBeInTheDocument();
  });

  /**
   * `?chunk=abc` and `?chunk=-1` are not chunk numbers, and parseInt would turn the first into
   * NaN and accept the second as an ordinal no document has. Either way nothing matches — and
   * an anchor that matches nothing must SAY so, because the alternative is showing the top of
   * a document as though it were the passage a verdict cited.
   */
  it.each(['abc', '-1', '3.7', '99999999999999999999'])(
    'says so rather than silently dropping the anchor for ?chunk=%s',
    async (raw) => {
      stub();
      view(`/docs/reg_abc?chunk=${raw}`);
      expect(await screen.findByText(/is not a chunk number|no chunk at ordinal/i)).toBeInTheDocument();
      expect(screen.queryAllByTestId('chunk-cited')).toHaveLength(0);
      expect(screen.queryByText(/NaN/)).toBeNull();
    },
  );

  /**
   * The worst case is not NaN, it is truncation: Number.parseInt reads `147.9` as chunk 147,
   * which EXISTS — so a malformed link would mark a real passage as the one the verdict cited.
   * A wrong highlight on a safety data sheet is worse than no highlight.
   */
  it('never truncates a malformed ordinal onto a chunk that happens to exist', async () => {
    stub();
    view('/docs/reg_abc?chunk=147.9');
    expect(await screen.findByText(/is not a chunk number/i)).toBeInTheDocument();
    expect(screen.queryAllByTestId('chunk-cited')).toHaveLength(0);
    expect(screen.getByText('nickel release limit')).toBeInTheDocument(); // chunk 147, unmarked
  });

  /**
   * No anchor at all is not a failed anchor. An empty parameter is a link-building artifact,
   * not a claim about a passage, so there is nothing to report a miss on.
   */
  it.each(['?entry=', '?chunk=', ''])(
    'says nothing about anchoring when the link carries none (%s)',
    async (qs) => {
      stub();
      view(`/docs/reg_abc${qs}`);
      expect(await screen.findByText('REACH Annex XVII')).toBeInTheDocument();
      expect(screen.queryByText(/anchored to|no chunk|not a chunk number/i)).toBeNull();
    },
  );
});
