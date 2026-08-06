import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  NotFound,
  createProject,
  getCandidates,
  getChatThread,
  getTable,
  postAmendment,
  getDecision,
  getDosing,
  getMatrix,
  getProject,
  getRevisions,
  getVpGate,
  listProjects,
  matrixXlsxUrl,
  orderSubstance,
  recordDetermination,
  recordLoading,
  recordVpDetermination,
  reviewDosing,
  reviewEvidence,
  reviseStage,
  sendChatMessage,
  setAccessTokenProvider,
} from './client';
import type { CreateProjectRequest, DecisionDoc } from './types';

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

describe('getCandidates', () => {
  // 404 is the normal pre-run state — Discovery has not produced a candidate pool yet. The screen
  // renders an empty state for it, so it must never arrive as a thrown error.
  it('returns the NotFound sentinel before Discovery has run', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getCandidates('p1')).resolves.toBe(NotFound);
  });

  it('returns the parsed CandidatesDoc on 200', async () => {
    const doc = {
      id: 'p1|candidates',
      projectId: 'p1',
      type: 'candidates',
      substances: [
        {
          componentId: 'bottle',
          element: 'Y',
          form: 'oxide',
          cas: '1314-36-9',
          preferred: true,
          tier: 'A',
          rationale: 'corpus match',
          citations: [],
        },
      ],
    };
    stubFetch(() => json(doc));
    await expect(getCandidates('p1')).resolves.toEqual(doc);
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

/*
 * `getRegulatoryGate` and `approveRegulatory` had three describes here — the projected gate, the
 * three-state signer fold, and the 422 on an unarmed gate.
 *
 * Both endpoints are DELETED (spec §16.4): the regulatory gate is removed, not demoted, so there is
 * nothing left to fetch or sign. The property those tests protected — that an approved gate with no
 * recorded signer reads as UNKNOWN provenance and never as a person — is not dropped: it is asserted
 * on the ONE gate that survives, in Signoff.test.tsx, which is now the only signature in the product
 * and therefore the only place the fold can go wrong.
 */

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

describe('getTable', () => {
  it('returns rows even when only Discovery has run — NOT a 404', async () => {
    // An analysis in progress is a state, not a missing resource. Treating it as absent would blank the
    // table for exactly the projects an operator watches most closely.
    const body = {
      projectId: 'p1',
      rows: [
        {
          componentId: 'bottle', cas: '1306-38-3', element: 'Ce', form: 'oxide',
          discovery: { tier: 'A', preferred: true, rationale: 'stable in melt', sources: 3 },
          regulatory: null, dosing: null, outcome: null,
          stoppedAt: null, stoppedReason: null,
        },
      ],
    };
    stubFetch(() => json(body));
    await expect(getTable('p1')).resolves.toEqual(body);
  });

  it('preserves an explicit null phase group rather than dropping the key', async () => {
    // The backend serializes these even when null so that absence is EXPLICIT on the wire. If the client
    // let them become `undefined`, "this phase has not run" and "this build has never heard of the field"
    // would arrive indistinguishable -- the exact ambiguity the backend went out of its way to remove.
    const body = {
      projectId: 'p1',
      rows: [{
        componentId: 'bottle', cas: 'c', element: 'Ce', form: 'oxide',
        discovery: null, regulatory: null, dosing: null, outcome: null,
        stoppedAt: null, stoppedReason: null,
      }],
    };
    stubFetch(() => json(body));
    const res = await getTable('p1');
    expect(Object.prototype.hasOwnProperty.call(res.rows[0], 'dosing')).toBe(true);
    expect(res.rows[0].dosing).toBeNull();
  });
});

describe('postAmendment', () => {
  it('returns the conflict body on 409 instead of throwing', async () => {
    // A 409 here is not an error -- it is the system asking a question. The amendment would void a
    // signature already on file, and the caller has to show WHICH signatures and WHAT will re-run before
    // offering to proceed. Throwing would collapse that into a generic failure toast and the operator
    // would never learn what they were about to destroy.
    const conflict = {
      error: 'this amendment re-runs a stage whose signature is already on file.',
      voids: ['regulatory'],
      rerun: ['regulatory', 'matrix', 'decision'],
    };
    stubFetch(() => json(conflict, 409));

    const res = await postAmendment('p1', { field: 'markets', value: 'EU, JP', reason: 'customer call' });

    expect(res.ok).toBe(false);
    if (!res.ok) expect(res.conflict.voids).toEqual(['regulatory']);
  });

  it('POSTs the amendment and reports success', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ projectId: 'p1' }, 202);
    });

    await expect(
      postAmendment('p1', { field: 'markets', value: 'EU, JP', reason: 'customer call' }),
    ).resolves.toEqual({ ok: true });
    expect(seen?.url).toBe('/api/projects/p1/amendments');
    expect(seen?.init?.method).toBe('POST');
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

/* ---- Decision: the VP hard gate -------------------------------------------- */

describe('getDecision', () => {
  it('returns the NotFound sentinel before the stage has assembled a decision', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    await expect(getDecision('p1')).resolves.toBe(NotFound);
  });

  it('returns the parsed DecisionDoc on 200, confirmedCode arriving as an explicit null', async () => {
    const doc: DecisionDoc = {
      id: 'p1|decision',
      projectId: 'p1',
      type: 'decision',
      components: [
        {
          componentId: 'bottle',
          rows: [
            {
              cas: '1314-36-9',
              element: 'Y',
              determination: 'Pass',
              recommendedPpm: 120,
              cleared: { regulatory: true, dosing: true, availability: true },
              traceability: { verdict: 'p1|verdict|bottle|1314-36-9', window: 'p1|dosing' },
            },
          ],
          proposedCode: { ratioSignature: 'Y:120', markerCas: ['1314-36-9'], rationale: 'stoichiometric Y2O3' },
          confirmedCode: null,
        },
      ],
      procurement: { status: 'unreleased', orderedCas: [] },
      generatedAt: '2026-07-27T00:00:00Z',
    };
    stubFetch(() => json(doc));
    const res = await getDecision('p1');
    expect(res).toEqual(doc);
    expect((res as DecisionDoc).components[0].confirmedCode).toBeNull();
  });
});

describe('getVpGate', () => {
  it('returns the parsed gate on 200', async () => {
    const gate = { status: 'locked', armable: false, blockers: ['dosing has not run — there are no finalized codes to confirm'] };
    stubFetch(() => json(gate));
    await expect(getVpGate('p1')).resolves.toEqual(gate);
  });

  it('throws rather than returning a sentinel on a non-ok status — the gate read never 404s', async () => {
    stubFetch(() => json({ error: 'server error' }, 500));
    await expect(getVpGate('p1')).rejects.toBeInstanceOf(ApiError);
  });
});

describe('recordVpDetermination', () => {
  it('POSTs the determination and returns the approved status', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ status: 'approved' });
    });
    const req = {
      determination: 'approved' as const,
      reason: 'the R.E. and physics both cleared the ratio',
      confirmations: [{ componentId: 'bottle', code: 'Y:120' }],
    };
    await expect(recordVpDetermination('p1', req)).resolves.toEqual({ status: 'approved' });
    expect(seen?.url).toBe('/api/projects/p1/decision/determination');
    expect(seen?.init?.method).toBe('POST');
    expect(JSON.parse(String(seen?.init?.body))).toEqual(req);
  });

  it("throws an ApiError carrying the server message when the gate is not armable (422)", async () => {
    stubFetch(() =>
      json({ error: 'VP gate not armable', blockers: ['a revision is in flight for this project'] }, 422),
    );
    await expect(
      recordVpDetermination('p1', { determination: 'approved', reason: 'r' }),
    ).rejects.toThrow('VP gate not armable');
    await expect(
      recordVpDetermination('p1', { determination: 'approved', reason: 'r' }),
    ).rejects.toBeInstanceOf(ApiError);
  });
});

describe('orderSubstance', () => {
  it('POSTs to the orders endpoint, URL-encoding the CAS, and returns the 202 body', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ ordered: '1314-36-9' }, 202);
    });
    await expect(orderSubstance('p1', '1314-36-9')).resolves.toEqual({ ordered: '1314-36-9' });
    expect(seen?.url).toBe('/api/projects/p1/orders/1314-36-9');
    expect(seen?.init?.method).toBe('POST');
  });

  it('URL-encodes a CAS containing characters that need escaping', async () => {
    let seen: { url: string; init?: RequestInit } | undefined;
    stubFetch((url, init) => {
      seen = { url, init };
      return json({ ordered: 'a/b' }, 202);
    });
    await orderSubstance('p1', 'a/b');
    expect(seen?.url).toBe('/api/projects/p1/orders/a%2Fb');
  });

  it("surfaces the server's 422 for MSDS-before-order verbatim", async () => {
    stubFetch(() =>
      json({ error: "MSDS-before-order: no safety sheet on file for '1314-36-9' — fetch one via POST /msds/1314-36-9/fetch (or upload one) before ordering" }, 422),
    );
    await expect(orderSubstance('p1', '1314-36-9')).rejects.toThrow('MSDS-before-order');
  });
});
