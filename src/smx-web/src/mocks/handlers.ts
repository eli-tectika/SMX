import { http, HttpResponse, passthrough } from 'msw';
import { DEMO_PROJECT_ID } from './demo';
import demoProject from './fixtures/demo-project.json';
import { demoMatrix } from './fixtures/demoMatrix';

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
];
