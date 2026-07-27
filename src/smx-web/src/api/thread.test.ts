import { afterEach, describe, expect, it, vi } from 'vitest';
import { cancelRun, getThread, rerunStage, sendMessage } from './thread';

afterEach(() => vi.unstubAllGlobals());

const okJson = (body: unknown) =>
  vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => body });

describe('thread client', () => {
  it('reads the thread for a stage', async () => {
    const fetchMock = okJson([]);
    vi.stubGlobal('fetch', fetchMock);
    await getThread('proj-1', 'discovery');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/projects/proj-1/stages/discovery/thread');
  });

  it('reports queued when a run is in flight', async () => {
    vi.stubGlobal('fetch', okJson({ messageId: 'm1', seq: 7, queued: true }));
    await expect(sendMessage('proj-1', 'discovery', 'why?')).resolves.toEqual({
      messageId: 'm1',
      seq: 7,
      queued: true,
    });
  });

  it('posts a cancel to the run, not the stage', async () => {
    const fetchMock = okJson(null);
    vi.stubGlobal('fetch', fetchMock);
    await cancelRun('proj-1', 'run|proj-1|discovery|1');
    expect(fetchMock.mock.calls[0][0]).toBe(
      '/api/projects/proj-1/runs/run%7Cproj-1%7Cdiscovery%7C1/cancel',
    );
  });

  it('posts a rerun to the stage', async () => {
    const fetchMock = okJson(null);
    vi.stubGlobal('fetch', fetchMock);
    await rerunStage('proj-1', 'discovery');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/projects/proj-1/stages/discovery/rerun');
  });

  it('throws with the server status on a refusal', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 422, text: async () => 'stage is done' }),
    );
    await expect(rerunStage('proj-1', 'discovery')).rejects.toThrow(/422/);
  });
});
