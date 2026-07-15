import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  NotFound,
  approveRegulatory,
  createProject,
  getChatThread,
  getMatrix,
  getProject,
  getRegulatoryGate,
  getRevisions,
  matrixXlsxUrl,
  recordDetermination,
  reviewEvidence,
  reviseStage,
  sendChatMessage,
  setAccessTokenProvider,
} from './client';
import type { CreateProjectRequest } from './types';

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const stubFetch = (impl: (url: string, init?: RequestInit) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => Promise.resolve(impl(url, init))),
  );

afterEach(() => vi.unstubAllGlobals());
afterEach(() => setAccessTokenProvider(async () => null));

const request: CreateProjectRequest = {
  client: 'LVMH',
  product: 'MUFE clear bottle',
  components: [
    { id: 'bottle', material: 'PET', application: 'leave-on', markets: ['EU'], objective: 'brand' },
  ],
  elementPools: [{ component: 'bottle', element: 'Zr', line: 'Kα', status: 'V' }],
};

describe('createProject', () => {
  it('POSTs to /api/projects and returns the 202 body', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ projectId: 'proj-abc' }, 202);
    });

    await expect(createProject(request)).resolves.toEqual({ projectId: 'proj-abc' });
    expect(seen?.url).toBe('/api/projects');
    expect(seen?.init?.method).toBe('POST');
    expect(JSON.parse(String(seen?.init?.body))).toEqual(request);
  });

  it("surfaces the server's `{error}` body from a 400 rather than a generic message", async () => {
    stubFetch(() => json({ error: 'every element pool must reference a declared component' }, 400));
    await expect(createProject(request)).rejects.toThrow(
      'every element pool must reference a declared component',
    );
    await expect(createProject(request)).rejects.toBeInstanceOf(ApiError);
  });

  it('attaches an Authorization header when a token provider is set', async () => {
    setAccessTokenProvider(async () => 'tok123');
    let seen: RequestInit | undefined;
    stubFetch((_url, init) => {
      seen = init;
      return json({ projectId: 'p' }, 202);
    });
    await createProject(request);
    expect(new Headers(seen?.headers).get('Authorization')).toBe('Bearer tok123');
  });
});

describe('getProject', () => {
  it('returns the NotFound sentinel on 404 instead of throwing', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getProject('nope')).resolves.toBe(NotFound);
  });

  it('url-encodes the project id', async () => {
    let seen = '';
    stubFetch((url) => {
      seen = url;
      return json({});
    });
    await getProject('a/b');
    expect(seen).toBe('/api/projects/a%2Fb');
  });

  it('throws on a 500', async () => {
    stubFetch(() => new Response('boom', { status: 500 }));
    await expect(getProject('p1')).rejects.toBeInstanceOf(ApiError);
  });
});

describe('getMatrix', () => {
  // A missing matrix is the normal pre-assembly state; the Matrix screen renders an
  // explanatory empty state for it, so it must never arrive as a thrown error.
  it('returns the NotFound sentinel when the matrix is not yet assembled', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getMatrix('p1')).resolves.toBe(NotFound);
  });

  it('returns the parsed MatrixDoc on 200', async () => {
    const doc = { id: 'p1|matrix', projectId: 'p1', type: 'matrix', rows: [], columns: [], cells: [], generatedAt: '' };
    stubFetch(() => json(doc));
    await expect(getMatrix('p1')).resolves.toEqual(doc);
  });
});

describe('matrixXlsxUrl', () => {
  it('points at the xlsx format of the matrix endpoint', () => {
    expect(matrixXlsxUrl('p1')).toBe('/api/projects/p1/matrix?format=xlsx');
  });
});

/* ---- the write side -------------------------------------------------------- */

describe('recordDetermination', () => {
  it('POSTs the cell, determination and reason to the determination endpoint', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ determination: 'recommended' });
    });
    await recordDetermination('p1', {
      cas: '39049-04-2',
      componentId: 'bottle',
      determination: 'recommended',
      reason: 'clean on every axis',
    });
    expect(seen?.url).toBe('/api/projects/p1/regulatory/determination');
    expect(seen?.init?.method).toBe('POST');
    expect(JSON.parse(String(seen?.init?.body))).toEqual({
      cas: '39049-04-2',
      componentId: 'bottle',
      determination: 'recommended',
      reason: 'clean on every axis',
    });
  });

  it('returns NotFound when the verdict no longer exists (404)', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(
      recordDetermination('p1', { cas: 'x', componentId: 'c', determination: 'rejected', reason: 'r' }),
    ).resolves.toBe(NotFound);
  });

  it("surfaces the server's `{error}` on a 422 (e.g. a blank reason)", async () => {
    stubFetch(() => json({ error: 'every determination requires a reason' }, 422));
    await expect(
      recordDetermination('p1', { cas: 'x', componentId: 'c', determination: 'recommended', reason: '' }),
    ).rejects.toThrow('every determination requires a reason');
  });
});

describe('reviewEvidence', () => {
  it('POSTs the cell to the review endpoint', async () => {
    let seen = '';
    stubFetch((url) => {
      seen = url;
      return json({ reviewed: true });
    });
    await expect(reviewEvidence('p1', { cas: 'x', componentId: 'c' })).resolves.toEqual({
      reviewed: true,
    });
    expect(seen).toBe('/api/projects/p1/regulatory/review');
  });

  it('returns NotFound on 404', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(reviewEvidence('p1', { cas: 'x', componentId: 'c' })).resolves.toBe(NotFound);
  });
});

describe('getRegulatoryGate', () => {
  it('returns the projected gate state', async () => {
    const gate = { status: 'locked', armable: false, blockers: ['unreviewed: x|c (Fail)'] };
    stubFetch(() => json(gate));
    await expect(getRegulatoryGate('p1')).resolves.toEqual(gate);
  });
});

describe('approveRegulatory', () => {
  it('POSTs to the approve endpoint and returns the approved status', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ status: 'approved' });
    });
    await expect(approveRegulatory('p1')).resolves.toEqual({ status: 'approved' });
    expect(seen?.url).toBe('/api/projects/p1/regulatory/approve');
    expect(seen?.init?.method).toBe('POST');
  });

  it('throws an ApiError carrying the server message when the gate is not armable (422)', async () => {
    stubFetch(() => json({ error: 'gate not armable — open the flagged items first', blockers: [] }, 422));
    await expect(approveRegulatory('p1')).rejects.toThrow('gate not armable');
    await expect(approveRegulatory('p1')).rejects.toBeInstanceOf(ApiError);
  });
});

describe('sendChatMessage', () => {
  it('POSTs the text to the stage chat endpoint and returns the 202 body', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ messageId: 'm1', status: 'pending' }, 202);
    });
    await expect(sendChatMessage('p1', 'regulatory', 'why is Pb failing?')).resolves.toEqual({
      messageId: 'm1',
      status: 'pending',
    });
    expect(seen?.url).toBe('/api/projects/p1/stages/regulatory/chat');
    expect(JSON.parse(String(seen?.init?.body))).toEqual({ text: 'why is Pb failing?' });
  });

  it('surfaces a 422 for an unknown stage', async () => {
    stubFetch(() => json({ error: "unknown stage 'dosing'" }, 422));
    await expect(sendChatMessage('p1', 'dosing', 'hi')).rejects.toThrow("unknown stage 'dosing'");
  });
});

describe('getChatThread', () => {
  it('returns the turn array', async () => {
    const turns = [
      { id: 't1', role: 'operator', text: 'hi', createdAt: '', toolCalls: [], status: 'answered' },
    ];
    stubFetch(() => json(turns));
    await expect(getChatThread('p1', 'regulatory')).resolves.toEqual(turns);
  });
});

describe('reviseStage', () => {
  it('POSTs target + reason (+ cas/componentId) and returns the 202 body', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ revisionId: 'r1', status: 'pending' }, 202);
    });
    await reviseStage('p1', 'regulatory', {
      target: 'Pb on bottle',
      reason: 'the R.E. cleared it under the new solubility class',
      cas: '61790-14-5',
      componentId: 'bottle',
    });
    expect(seen?.url).toBe('/api/projects/p1/stages/regulatory/revise');
    expect(JSON.parse(String(seen?.init?.body))).toMatchObject({
      target: 'Pb on bottle',
      cas: '61790-14-5',
      componentId: 'bottle',
    });
  });

  it('returns NotFound on 404', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(reviseStage('p1', 'discovery', { target: 't', reason: 'r' })).resolves.toBe(
      NotFound,
    );
  });
});

describe('getRevisions', () => {
  it('returns the revision array', async () => {
    const revs = [{ id: 'r1', projectId: 'p1', stage: 'discovery', target: 't', reason: 'r', status: 'applied', createdAt: '' }];
    stubFetch(() => json(revs));
    await expect(getRevisions('p1')).resolves.toEqual(revs);
  });
});
