import type {
  CreateProjectRequest,
  CreateProjectResponse,
  MatrixDoc,
  ProjectSummary,
} from './types';

/**
 * All requests go to /api/*. In dev, Vite's proxy strips the prefix and forwards
 * to the backend on :5169; in Azure, App Gateway's apiPathRule routes /api/* to
 * the backend container. Either way the request is same-origin, which is why the
 * backend needs no CORS policy.
 */
const BASE = '/api';

/**
 * A missing matrix is the normal pre-assembly state, not a failure — the
 * assembler only writes the doc once the screening agents have run. Callers
 * distinguish it from a real error by identity, so it is a sentinel rather than
 * a thrown exception.
 */
export const NotFound = Symbol('NotFound');
export type NotFound = typeof NotFound;

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function failure(res: Response): Promise<ApiError> {
  // The backend returns `400 {"error":"..."}` from CreateProjectRequest.Validate().
  const body = await res.text();
  let message = body || res.statusText;
  try {
    const parsed = JSON.parse(body) as { error?: string };
    if (parsed.error) message = parsed.error;
  } catch {
    /* not JSON — fall back to the raw body */
  }
  return new ApiError(res.status, message);
}

export async function createProject(req: CreateProjectRequest): Promise<CreateProjectResponse> {
  const res = await fetch(`${BASE}/projects`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as CreateProjectResponse;
}

export async function getProject(projectId: string): Promise<ProjectSummary | NotFound> {
  const res = await fetch(`${BASE}/projects/${encodeURIComponent(projectId)}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ProjectSummary;
}

export async function getMatrix(projectId: string): Promise<MatrixDoc | NotFound> {
  const res = await fetch(`${BASE}/projects/${encodeURIComponent(projectId)}/matrix`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as MatrixDoc;
}

export function matrixXlsxUrl(projectId: string): string {
  return `${BASE}/projects/${encodeURIComponent(projectId)}/matrix?format=xlsx`;
}
