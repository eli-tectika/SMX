import type {
  CreateProjectRequest,
  CreateProjectResponse,
  LearnedConclusion,
  MarkerLibraryEntry,
  MatrixDoc,
  MsdsEntry,
  ProjectSummary,
} from './types';

/**
 * All requests go to /api/*. In dev, Vite's proxy strips the prefix and forwards
 * to the backend on :5169; in Azure, App Gateway's apiPathRule routes /api/* to
 * the backend container. Either way the request is same-origin, which is why the
 * backend needs no CORS policy.
 */
const BASE = '/api';

type TokenProvider = () => Promise<string | null>;
let tokenProvider: TokenProvider = async () => null;

/** Set by the MSAL bootstrap (src/auth/msal.ts). Default no-op keeps local dev open. */
export function setAccessTokenProvider(provider: TokenProvider): void {
  tokenProvider = provider;
}

/** fetch() wrapper that adds `Authorization: Bearer <token>` when a provider yields one. */
async function authorizedFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const token = await tokenProvider();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  return fetch(url, { ...init, headers });
}

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
  const res = await authorizedFetch(`${BASE}/projects`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as CreateProjectResponse;
}

export async function getProject(projectId: string): Promise<ProjectSummary | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ProjectSummary;
}

export async function getMatrix(projectId: string): Promise<MatrixDoc | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/matrix`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as MatrixDoc;
}

export function matrixXlsxUrl(projectId: string): string {
  return `${BASE}/projects/${encodeURIComponent(projectId)}/matrix?format=xlsx`;
}

/* ---------------------------------------------------------------------------
   The cross-project knowledge layer — spec §6.

   Marker Library, Learned Conclusions and the MSDS Registry rendered fixtures behind a
   MockBadge because "the backend has no endpoint". It does:
   `src/Smx.Backend/Api/KnowledgeEndpoints.cs` serves all three, each accepting `?search=`,
   and the search runs server-side against Cosmos. The frontend simply never called them.

   Empty is a legitimate answer. A fresh subscription has an empty Marker Library, because
   nothing has been through the VP gate yet — and an empty library rendered honestly is
   worth more than a full one rendered from a fixture.
   --------------------------------------------------------------------------- */

function q(search?: string): string {
  return search?.trim() ? `?search=${encodeURIComponent(search.trim())}` : '';
}

export async function getMarkerLibrary(search?: string): Promise<MarkerLibraryEntry[]> {
  const res = await authorizedFetch(`${BASE}/marker-library${q(search)}`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as MarkerLibraryEntry[];
}

export async function getLearnedConclusions(search?: string): Promise<LearnedConclusion[]> {
  const res = await authorizedFetch(`${BASE}/learned-conclusions${q(search)}`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as LearnedConclusion[];
}

export async function getMsdsRegistry(search?: string): Promise<MsdsEntry[]> {
  const res = await authorizedFetch(`${BASE}/msds-registry${q(search)}`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as MsdsEntry[];
}

/**
 * Sign the MSDS review for one substance.
 *
 * This is an operator-signed record, not a UI flag: the MSDS-before-order hard precondition
 * (spec §5) reads it, and an order stays blocked until its sheet is current AND reviewed.
 * The backend stamps `reviewedAt` so that *when* it was signed stays recoverable.
 */
export async function reviewMsds(cas: string): Promise<MsdsEntry | NotFound> {
  const res = await authorizedFetch(`${BASE}/msds-registry/${encodeURIComponent(cas)}/review`, {
    method: 'POST',
  });
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as MsdsEntry;
}
