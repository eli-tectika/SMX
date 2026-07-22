import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  NotFound,
  approveRegulatory,
  createProject,
  getChatThread,
  getCost,
  getDosing,
  getMatrix,
  getProject,
  getRegulatoryGate,
  getRevisions,
  listProjects,
  matrixXlsxUrl,
  recordDetermination,
  recordLoading,
  reviewDosing,
  reviewEvidence,
  reviseStage,
  sendChatMessage,
  setAccessTokenProvider,
} from './client';
import type { CostDoc, CreateProjectRequest } from './types';

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
    {
      id: 'bottle',
      material: 'PET',
      application: 'leave-on',
      markets: ['EU'],
      objective: 'brand',
      physicalState: 'solid',
    },
  ],
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

describe('listProjects', () => {
  it('GETs /api/projects and returns the items', async () => {
    let seen = '';
    const items = [
      { projectId: 'proj-b', client: 'LVMH', product: 'Bottle', stages: {}, createdAt: '2026-07-01T00:00:00Z' },
      { projectId: 'proj-a', client: 'Acme', product: 'Lid', stages: {}, createdAt: '2026-01-01T00:00:00Z' },
    ];
    stubFetch((url) => {
      seen = url;
      return json(items);
    });
    await expect(listProjects()).resolves.toEqual(items);
    expect(seen).toBe('/api/projects');
  });

  // An empty record is a legitimate answer, not a failure — a fresh subscription has no projects. The
  // dashboard renders that as an empty state, and it must never arrive as a thrown error or a sentinel.
  it('returns an empty array on an empty record rather than a NotFound sentinel', async () => {
    stubFetch(() => json([]));
    await expect(listProjects()).resolves.toEqual([]);
  });

  it('throws an ApiError on a 500 — an unreachable list is not an empty one', async () => {
    stubFetch(() => new Response('boom', { status: 500 }));
    await expect(listProjects()).rejects.toBeInstanceOf(ApiError);
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

  // Was written against "unknown stage 'dosing'" — but dosing is one of the six stages chat accepts now.
  // The response is stubbed, so that version kept passing while asserting something the backend no longer
  // does. Pointed at a stage that is genuinely unknown.
  it('surfaces a 422 for an unknown stage', async () => {
    stubFetch(() => json({ error: "unknown stage 'decision'" }, 422));
    await expect(sendChatMessage('p1', 'decision', 'hi')).rejects.toThrow("unknown stage 'decision'");
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

/* ---- Plan 4: dosing & cost -------------------------------------------------- */

describe('getDosing', () => {
  // 404 is the normal pre-run state — dosing waits for a signed regulatory gate. The screen renders an
  // empty state for it, so it must never arrive as a thrown error.
  it('returns the NotFound sentinel before the stage has run', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getDosing('p1')).resolves.toBe(NotFound);
  });

  it('returns the parsed DosingDoc on 200', async () => {
    const doc = {
      id: 'p1|dosing',
      projectId: 'p1',
      type: 'dosing',
      windows: [],
      codes: [],
      generatedAt: '2026-07-08T00:00:00Z',
    };
    stubFetch(() => json(doc));
    await expect(getDosing('p1')).resolves.toEqual(doc);
  });
});

describe('getCost', () => {
  it('returns the NotFound sentinel before the stage has run', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getCost('p1')).resolves.toBe(NotFound);
  });

  it('returns the parsed CostDoc on 200, preserving an absent bestQuote', async () => {
    const doc = {
      id: 'p1|cost',
      projectId: 'p1',
      type: 'cost',
      generatedAt: '2026-07-08T00:00:00Z',
      // bestQuote is OMITTED, not null — the wire contract for "nothing parseable was on file".
      substances: [
        { cas: '1314-36-9', element: 'Y', suppliers: ['Strem'], priceNote: 'no price on file — quote required', risks: ['single-source'] },
      ],
    };
    stubFetch(() => json(doc));
    const res = await getCost('p1');
    expect(res).toEqual(doc);
    expect((res as CostDoc).substances[0].bestQuote).toBeUndefined();
  });
});

describe('recordLoading', () => {
  it('POSTs the loading entry to the dosing/loading endpoint', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ status: 'pending' }, 202);
    });
    const req = { cas: '1314-36-9', element: 'Y', form: 'oxide', metalLoading: 0.787, basis: 'stoichiometric Y2O3' };
    await expect(recordLoading('p1', req)).resolves.toEqual({ status: 'pending' });
    expect(seen?.url).toBe('/api/projects/p1/dosing/loading');
    expect(seen?.init?.method).toBe('POST');
    expect(JSON.parse(String(seen?.init?.body))).toEqual(req);
  });

  it("surfaces the server's 422 for a loading outside (0, 1]", async () => {
    stubFetch(() => json({ error: 'metalLoading must be a mass fraction in (0, 1]' }, 422));
    await expect(
      recordLoading('p1', { cas: 'c', element: 'Y', form: 'oxide', metalLoading: 78.7, basis: 'b' }),
    ).rejects.toThrow('metalLoading must be a mass fraction in (0, 1]');
  });

  it("surfaces the server's 422 for a blank basis", async () => {
    stubFetch(() =>
      json({ error: 'a metal loading requires a basis — the source that makes it checkable' }, 422),
    );
    await expect(
      recordLoading('p1', { cas: 'c', element: 'Y', form: 'oxide', metalLoading: 0.787, basis: '' }),
    ).rejects.toThrow('a metal loading requires a basis');
  });

  it('returns NotFound when the project is gone', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(
      recordLoading('p1', { cas: 'c', element: 'Y', form: 'oxide', metalLoading: 0.5, basis: 'b' }),
    ).resolves.toBe(NotFound);
  });
});

describe('reviewDosing', () => {
  it('POSTs the note to the dosing/review endpoint', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ reviewed: true }, 202);
    });
    await expect(reviewDosing('p1', { note: 'PL + physics reviewed the ratios' })).resolves.toEqual({
      reviewed: true,
    });
    expect(seen?.url).toBe('/api/projects/p1/dosing/review');
    expect(JSON.parse(String(seen?.init?.body))).toEqual({ note: 'PL + physics reviewed the ratios' });
  });

  it("surfaces the server's 422 for a blank note", async () => {
    stubFetch(() =>
      json({ error: 'a review note is required — the checkpoint records what was reviewed' }, 422),
    );
    await expect(reviewDosing('p1', { note: '' })).rejects.toThrow('a review note is required');
  });

  it('returns NotFound when there is no dosing record to review', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(reviewDosing('p1', { note: 'n' })).resolves.toBe(NotFound);
  });
});
