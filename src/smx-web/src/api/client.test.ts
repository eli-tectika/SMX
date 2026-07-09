import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, NotFound, createProject, getMatrix, getProject, matrixXlsxUrl } from './client';
import type { CreateProjectRequest } from './types';

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const stubFetch = (impl: (url: string, init?: RequestInit) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => Promise.resolve(impl(url, init))),
  );

afterEach(() => vi.unstubAllGlobals());

const request: CreateProjectRequest = {
  client: 'LVMH',
  product: 'MUFE clear bottle',
  components: [
    { id: 'bottle', material: 'PET', application: 'leave-on', markets: ['EU'], objective: 'brand' },
  ],
  substances: [{ element: 'Zr', form: 'neodecanoate', cas: '39049-04-2' }],
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
    stubFetch(() => json({ error: 'substance CAS numbers must be unique' }, 400));
    await expect(createProject(request)).rejects.toThrow('substance CAS numbers must be unique');
    await expect(createProject(request)).rejects.toBeInstanceOf(ApiError);
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
