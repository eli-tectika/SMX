import type {
  ChatAccepted,
  ChatTurn,
  CostDoc,
  CreateProjectRequest,
  CreateProjectResponse,
  Determination,
  DeterminationRequest,
  DosingDoc,
  DosingReviewRequest,
  LearnedConclusion,
  LoadingRequest,
  MarkerLibraryEntry,
  MatrixDoc,
  MsdsEntry,
  ProjectSummary,
  RegulatoryGate,
  ReviewRequest,
  ReviseAccepted,
  ReviseRequest,
  RevisionDoc,
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

/* ---------------------------------------------------------------------------
   The WRITE side — the operator finally acts, not just looks.

   Two shapes here. Determination / review / approve are SYNCHRONOUS 200s — the record
   changes immediately, so callers just refetch. Chat and revise are 202 record-as-bus:
   the write triggers an agent that answers LATER, so callers poll the matching GET.
   --------------------------------------------------------------------------- */

const p = (projectId: string) => `${BASE}/projects/${encodeURIComponent(projectId)}`;

async function postJson(url: string, body: unknown): Promise<Response> {
  return authorizedFetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Record the operator's determination on one cell (spec §4.4).
 *
 * This is the signature that lets a chemical into a customer's product — the only field
 * CompliantSet reads. It is NEVER the agent's proposal auto-applied; the caller supplies the
 * determination and a mandatory reason (the backend 422s a blank one). A 404 means the verdict
 * no longer exists (e.g. a revise dropped the cell) — a NotFound the caller handles, not an error.
 */
export async function recordDetermination(
  projectId: string,
  req: DeterminationRequest,
): Promise<{ determination: Determination } | NotFound> {
  const res = await postJson(`${p(projectId)}/regulatory/determination`, req);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { determination: Determination };
}

/** "I have read the evidence" — short of a ruling, but enough to clear a gate blocker on a Pass cell. */
export async function reviewEvidence(
  projectId: string,
  req: ReviewRequest,
): Promise<{ reviewed: true } | NotFound> {
  const res = await postJson(`${p(projectId)}/regulatory/review`, req);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { reviewed: true };
}

/** The gate's live arming state. Never 404s — an un-run project reads locked + not armable. */
export async function getRegulatoryGate(projectId: string): Promise<RegulatoryGate> {
  const res = await authorizedFetch(`${p(projectId)}/gate/regulatory`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as RegulatoryGate;
}

/**
 * Sign the regulatory gate.
 *
 * The backend re-checks armability server-side and 422s if the analysis is incomplete or any flagged
 * cell is unreviewed — so this can fail even when the button looked enabled (a concurrent revise).
 * The caller catches the ApiError and re-reads the gate, which carries the fresh blockers.
 */
export async function approveRegulatory(projectId: string): Promise<{ status: 'approved' }> {
  const res = await authorizedFetch(`${p(projectId)}/regulatory/approve`, { method: 'POST' });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { status: 'approved' };
}

/**
 * Post a message to a stage's agent (spec §3). 202 record-as-bus: the reply is written later by the
 * orchestrator, so the caller polls getChatThread until the pending message flips to answered.
 * `stage` is a BACKEND stage key (intake | discovery | regulatory | matrix); a 422 rejects any other.
 */
export async function sendChatMessage(
  projectId: string,
  stage: string,
  text: string,
): Promise<ChatAccepted | NotFound> {
  const res = await postJson(`${p(projectId)}/stages/${encodeURIComponent(stage)}/chat`, { text });
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ChatAccepted;
}

/** The thread for one (project, stage), oldest-first. An unknown project reads an empty thread, not 404. */
export async function getChatThread(projectId: string, stage: string): Promise<ChatTurn[]> {
  const res = await authorizedFetch(`${p(projectId)}/stages/${encodeURIComponent(stage)}/chat`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ChatTurn[];
}

/**
 * Ask an agent to revise its output, with a reason (spec §1.5 — "no direct edits").
 *
 * Only discovery and regulatory are revisable; a regulatory revision must carry cas + componentId.
 * 202 record-as-bus: poll getMatrix / getRevisions for the effect. A 404 means the project is gone.
 */
export async function reviseStage(
  projectId: string,
  stage: string,
  req: ReviseRequest,
): Promise<ReviseAccepted | NotFound> {
  const res = await postJson(`${p(projectId)}/stages/${encodeURIComponent(stage)}/revise`, req);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ReviseAccepted;
}

/** The revision trail for a project, oldest-first. Never 404s. */
export async function getRevisions(projectId: string): Promise<RevisionDoc[]> {
  const res = await authorizedFetch(`${p(projectId)}/revisions`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as RevisionDoc[];
}

/* ---------------------------------------------------------------------------
   DOSING & COST — the Plan 4 surface.

   Both GETs 404 before their stage has run. That is the normal pre-run state, not a failure, so it comes
   back as the NotFound sentinel and the screens render an empty state rather than an error.
   --------------------------------------------------------------------------- */

export async function getDosing(projectId: string): Promise<DosingDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/dosing`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DosingDoc;
}

export async function getCost(projectId: string): Promise<CostDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/cost`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as CostDoc;
}

/**
 * Enter the metal loading — the operator un-parking Dosing (spec §1.2's pause/resume loop).
 *
 * This is the one number in no catalog, and it is written to the CROSS-PROJECT knowledge layer keyed by CAS
 * alone: entering it here answers it for every future project too. 202 record-as-bus — the write flips the
 * dosing stage back to `pending` and the agent RE-RUNS, so the caller polls rather than expecting an
 * in-place edit. The backend 422s a loading outside (0, 1] or a blank basis.
 */
export async function recordLoading(
  projectId: string,
  req: LoadingRequest,
): Promise<{ status: 'pending' } | NotFound> {
  const res = await postJson(`${p(projectId)}/dosing/loading`, req);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { status: 'pending' };
}

/**
 * Record the soft code-finalization checkpoint (UX §4.5).
 *
 * A REVIEW NOTE, not a gate: it writes `reviewNote` + `reviewedAt` and touches no stage status and no gate.
 * It unlocks nothing, and the UI must not imply otherwise. The note is required — it is what was reviewed.
 */
export async function reviewDosing(
  projectId: string,
  req: DosingReviewRequest,
): Promise<{ reviewed: true } | NotFound> {
  const res = await postJson(`${p(projectId)}/dosing/review`, req);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { reviewed: true };
}
