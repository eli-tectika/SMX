import { http, HttpResponse, passthrough } from 'msw';
import { DEMO_PROJECT_ID } from './demo';
import demoProject from './fixtures/demo-project.json';
import { demoMatrix } from './fixtures/demoMatrix';
import { mockThread, scriptedStream } from './thread';

/**
 * Dev-only handlers.
 *
 * Real project ids pass straight through to the backend — MSW must never stand
 * between the operator and a real verdict. Only the reserved `proj-demo` id is
 * served from fixtures, so the demo can show a populated matrix while the Claude
 * Foundry deployment stays param-gated off (deployClaude=false) and no real project
 * can produce one.
 *
 * The unbacked journey stages (background, discovery detail, regulatory, dosing,
 * cost, decision) and the three cross-project surfaces have no endpoints at all;
 * their screens import fixtures directly and carry a MockBadge. When the backend
 * grows a route for one, add a real client call and delete its fixture — not a
 * handler here.
 */
export const handlers = [
  http.get('/api/projects/:projectId', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID ? HttpResponse.json(demoProject) : passthrough(),
  ),

  http.get('/api/projects/:projectId/matrix', ({ params, request }) => {
    if (params.projectId !== DEMO_PROJECT_ID) return passthrough();
    const format = new URL(request.url).searchParams.get('format');
    if (format === 'xlsx') return passthrough(); // no fixture workbook; let it 404 honestly
    return HttpResponse.json(demoMatrix);
  }),

  /*
   * The five thread routes (execution-core-design §7). Scaffolding for parallel development
   * against the pinned contract — deleted the moment the backend serves them, so MSW can never
   * stand between the operator and a real run.
   */
  http.get('/api/projects/:projectId/stages/:stage/thread', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? HttpResponse.json(mockThread(String(params.stage)))
      : passthrough(),
  ),

  http.get('/api/projects/:projectId/stages/:stage/thread/stream', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? new HttpResponse(scriptedStream(String(params.stage)), {
          headers: { 'Content-Type': 'text/event-stream' },
        })
      : passthrough(),
  ),

  http.post('/api/projects/:projectId/stages/:stage/messages', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID
      ? HttpResponse.json({ messageId: 'msg-mock', seq: 99, queued: true }, { status: 202 })
      : passthrough(),
  ),

  http.post('/api/projects/:projectId/runs/:runId/cancel', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID ? new HttpResponse(null, { status: 202 }) : passthrough(),
  ),

  http.post('/api/projects/:projectId/stages/:stage/rerun', ({ params }) =>
    params.projectId === DEMO_PROJECT_ID ? new HttpResponse(null, { status: 202 }) : passthrough(),
  ),
];
