import type {
  CandidatesDoc,
  ChatAccepted,
  ChatTurn,
  CreateProjectRequest,
  CreateProjectResponse,
  DecisionDoc,
  Determination,
  DeterminationRequest,
  DocumentBytes,
  DocumentChunk,
  DocumentDetail,
  DocumentKind,
  DocumentState,
  DocumentSummary,
  DosingDoc,
  ProjectTable,
  Amendment,
  AmendmentConflict,
  DosingReviewRequest,
  IntakeBrief,
  IntakeQuestion,
  IntakeSession,
  LearnedConclusion,
  LoadingRequest,
  MarkerLibraryEntry,
  MatrixDoc,
  MsdsEntry,
  PoolDoc,
  ProjectListItem,
  ProjectSummary,
  ReviewRequest,
  ReviseAccepted,
  ReviseRequest,
  RevisionDoc,
  SdsEnsureResult,
  SdsUploadResult,
  SessionAttachment,
  VpDeterminationRequest,
  VpGate,
  XrfConfirmed,
  XrfParseResult,
  XrfProposal,
  XrfState,
} from './types';
import { createSseParser, type SseEvent } from './sse';

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
    const parsed = JSON.parse(body) as { error?: string; detail?: string; title?: string };
    // ProblemDetails is the other shape the backend emits (Results.Problem — the document
    // endpoints' 503). Without this the operator is shown the raw JSON of the very message
    // that was written to explain the fault to them.
    if (parsed.error) message = parsed.error;
    else if (parsed.detail) message = parsed.detail;
    else if (parsed.title) message = parsed.title;
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

/**
 * Every project in the record, newest first.
 *
 * No NotFound sentinel: an empty record is an empty array, not a 404. A fresh subscription legitimately
 * has no projects, and that is a state to render honestly rather than an error to report.
 */
export async function listProjects(): Promise<ProjectListItem[]> {
  const res = await authorizedFetch(`${BASE}/projects`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ProjectListItem[];
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
   The physicist's XRF background result (spec §4.2) — Smx.Backend/Api/XrfEndpoints.cs.

   Two endpoints, one of which writes. `parse` is pure — it reads a file and hands back
   proposals, touching nothing. `confirm` is the single writer; keeping them separate is what
   makes the operator's confirmation a real act rather than a consequence of choosing a file.
   --------------------------------------------------------------------------- */

/**
 * Parse a physicist's result file into proposals. Writes NOTHING — the operator confirms separately,
 * which is what makes the confirmation an act rather than a consequence of choosing a file.
 */
export async function parseXrf(projectId: string, file: File): Promise<XrfParseResult> {
  const form = new FormData();
  // The field name MUST be "file" — it binds to the handler's `IFormFile file` parameter.
  form.append('file', file, file.name);

  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/xrf/parse`, {
    method: 'POST',
    // NO Content-Type header: the browser has to set it itself so it can append the multipart
    // boundary. Setting it by hand produces a body the server cannot parse.
    body: form,
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as XrfParseResult;
}

/**
 * The single writer of the physicist's measured background. A 422 carries the operator-readable reason the
 * confirmation was refused.
 *
 * Returns the 409 CONFLICT body rather than throwing, for the same reason `postAmendment` does: confirming
 * a measurement RE-DOSES the project, and re-dosing voids the VP's signature. That 409 is not a failure —
 * it is the system asking whether to discard a signature — and collapsing it into a generic error toast
 * would leave the operator with a refusal they cannot act on and no idea what they were about to destroy.
 *
 * Without this the confirm was worse than refused: the endpoint 409'd and the client threw, so on a signed
 * project the physicist's number could never be recorded at all.
 */
export async function confirmXrf(
  projectId: string,
  proposals: XrfProposal[],
  confirmSignatureVoid = false,
): Promise<{ ok: true; result: XrfConfirmed } | { ok: false; conflict: AmendmentConflict }> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/xrf/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ proposals, confirmSignatureVoid }),
  });
  if (res.status === 409) return { ok: false, conflict: (await res.json()) as AmendmentConflict };
  if (!res.ok) throw await failure(res);
  return { ok: true, result: (await res.json()) as XrfConfirmed };
}

/**
 * What is already confirmed. A 404 means intake has not written constraints yet — a normal state for
 * a project the operator opened straight after creating it, not a failure.
 */
export async function getXrfState(projectId: string): Promise<XrfState | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/xrf`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as XrfState;
}

/** The template lives on the API, not in the bundle, so it cannot drift from the parser. */
export const xrfTemplateUrl = `${BASE}/xrf-template.csv`;

/* ---------------------------------------------------------------------------
   The pre-project interview — "New project" as a conversation, not a form.

   Mirrors src/Smx.Backend/Api/IntakeSessionEndpoints.cs, AttachmentEndpoints.cs and
   IntakeBriefEndpoints.cs. The session store is a scratchpad the interview agent writes turn by
   turn; the brief is the one-time deliverable create_project hands off to the project proper.
   --------------------------------------------------------------------------- */

/** GET /intake-questions — the catalogue, served rather than duplicated client-side. */
export async function getIntakeQuestions(): Promise<IntakeQuestion[]> {
  const res = await authorizedFetch(`${BASE}/intake-questions`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as IntakeQuestion[];
}

/** POST /intake-sessions — opens a new interview. Both fields are optional; the agent can ask. */
export async function createIntakeSession(
  client?: string,
  product?: string,
): Promise<{ sessionId: string }> {
  const res = await authorizedFetch(`${BASE}/intake-sessions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ client, product }),
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { sessionId: string };
}

/**
 * An expired or unknown session is a REAL error the screen must show — not an empty interview it
 * silently starts over, which would strand the operator in a second conversation nobody can find.
 */
export async function getIntakeSession(sessionId: string): Promise<IntakeSession | NotFound> {
  const res = await authorizedFetch(`${BASE}/intake-sessions/${encodeURIComponent(sessionId)}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as IntakeSession;
}

export async function uploadAttachment(sessionId: string, file: File): Promise<SessionAttachment> {
  const form = new FormData();
  // The field name MUST be "file" — it is what binds to the handler's `IFormFile file` parameter.
  form.append('file', file, file.name);

  const res = await authorizedFetch(
    `${BASE}/intake-sessions/${encodeURIComponent(sessionId)}/attachments`,
    {
      method: 'POST',
      // NO Content-Type header. The browser has to set it itself so it can append the multipart
      // boundary; setting it by hand produces a body the server cannot parse, and the error looks
      // like a malformed upload rather than a missing boundary.
      body: form,
    },
  );
  if (!res.ok) throw await failure(res);
  return (await res.json()) as SessionAttachment;
}

/**
 * One interview turn, streamed. `onEvent` is called per SSE frame as it arrives.
 *
 * fetch + a stream reader, NOT EventSource: EventSource cannot POST, cannot carry a body, and cannot
 * set an Authorization header — and this request needs all three.
 */
export async function sendInterviewMessage(
  sessionId: string,
  text: string,
  onEvent: (e: SseEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const res = await authorizedFetch(
    `${BASE}/intake-sessions/${encodeURIComponent(sessionId)}/messages`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text }),
      signal,
    },
  );
  if (!res.ok) throw await failure(res);
  if (!res.body) throw new ApiError(res.status, 'the interview stream returned no body');

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  const push = createSseParser();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    // stream: true — a multi-byte character can straddle a chunk boundary, and decoding without it
    // turns the split character into U+FFFD in the middle of the operator's reply.
    for (const event of push(decoder.decode(value, { stream: true }))) onEvent(event);
  }
}

/**
 * A project created through the old form has no brief. That is a normal state, not a failure, so it
 * is the NotFound sentinel — the same discipline getMatrix already uses for a pre-assembly matrix.
 */
export async function getIntakeBrief(projectId: string): Promise<IntakeBrief | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/intake-brief`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as IntakeBrief;
}

/**
 * The operator's signature that the dossier is right (spec §2.3). There is no agent tool for this and
 * never will be: creating a project is safe to delegate because it runs nothing, but starting the
 * analysis is the human asserting that what the agent wrote is correct.
 *
 * Returns NotFound rather than throwing on a 404 — the real endpoint 404s when the project is gone,
 * and every other project-scoped lookup in this file treats "gone" as a state to render, not an error
 * to surface as a toast (see getProject, getMatrix). A double-press is idempotent server-side: it
 * replies 202 with the stage's CURRENT status rather than re-dispatching.
 */
export async function startProject(projectId: string): Promise<{ status: string } | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${encodeURIComponent(projectId)}/start`, {
    method: 'POST',
  });
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { status: string };
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
 * Fetch the safety data sheet for one substance, now.
 *
 * This replaces `reviewMsds`. The review signature is gone (design 2026-07-29, D8) and what stands
 * in its place is not a smaller version of it but the opposite kind of control: the operator used
 * to attest to a document the system already had, and can now go and get one it does not.
 *
 * It resolves — it does not reject — when the answer is `unavailable`. That is a truthful result
 * about the world, and it arrives with `attempted[]`, the list of what was tried and why each
 * candidate failed. Throwing it away onto an error path would discard the only part of the answer
 * the operator can act on.
 */
export async function fetchSds(cas: string): Promise<SdsEnsureResult> {
  const res = await authorizedFetch(`${BASE}/msds/${encodeURIComponent(cas)}/fetch`, {
    method: 'POST',
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as SdsEnsureResult;
}

/**
 * Hand the system a sheet it could not find.
 *
 * The fallback that has never existed — until now the only exit from a missing sheet was a
 * hand-rolled HTTP POST with a base64 PDF. There is no gate behind it: nothing is approved by
 * uploading, and the file faces the same content validation a fetched sheet does.
 *
 * `supplier` and `revisionDate` are required by the backend because, with the CAS, they ARE the
 * sheet's identity in the registry. A sheet stored without them can never be opened again.
 */
export async function uploadSds(
  cas: string,
  file: File,
  supplier: string,
  revisionDate: string,
): Promise<SdsUploadResult> {
  const form = new FormData();
  form.append('file', file);
  form.append('supplier', supplier);
  form.append('revisionDate', revisionDate);
  const res = await authorizedFetch(`${BASE}/msds/${encodeURIComponent(cas)}/upload`, {
    method: 'POST',
    body: form,
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as SdsUploadResult;
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

/*
 * `getRegulatoryGate` and `approveRegulatory` lived here. Both endpoints are DELETED — the regulatory
 * gate is removed, not demoted (spec §16.4) — so the client cannot offer them: a helper for a route
 * that 404s is a control somebody will wire to a button.
 *
 * `reviewEvidence` above and `recordDetermination` are what is left of that screen's write surface,
 * and the arming rule they used to feed now guards the two irreversible acts instead
 * (`EvidenceReview.Outstanding` on the VP determination and on every order).
 */

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
   Documents — the file viewer (design 2026-07-22).

   Bytes stream through the backend rather than a SAS URL: the storage account
   denies public access behind private endpoints, so a SAS would be unreachable
   from the browser AND would put a hole in private-by-default.
   --------------------------------------------------------------------------- */

/**
 * The library listing.
 *
 * The filter values are unions rather than strings because the backend 400s an unrecognised
 * one on purpose — a typo'd `kind` must not read as "no documents". Keeping the allowed set
 * in the type moves that failure from a runtime error to a compile error.
 */
export async function getDocuments(filter: {
  kind?: DocumentKind | 'all';
  q?: string;
  state?: DocumentState | 'all';
}): Promise<DocumentSummary[]> {
  const params = new URLSearchParams();
  if (filter.kind) params.set('kind', filter.kind);
  if (filter.q) params.set('q', filter.q);
  if (filter.state) params.set('state', filter.state);
  const qs = params.toString();
  const res = await authorizedFetch(`${BASE}/documents${qs ? `?${qs}` : ''}`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentSummary[];
}

export async function getDocument(id: string): Promise<DocumentDetail | NotFound> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentDetail;
}

/**
 * Fetch the raw bytes.
 *
 * This exists as a fetch rather than an <iframe src> because MSAL bearer tokens cannot ride
 * on a frame's src attribute — the browser will not attach the header. Everything downstream
 * (object URL for PDFs, srcdoc for HTML) follows from that constraint.
 *
 * null means "no bytes to show, and the detail endpoint says why": 409 for a document the
 * system knows it never obtained, 404 for a registry row whose blob has vanished. A 503
 * deliberately does NOT collapse into null — that one is a claim about the deployment, not
 * about the document, and reporting an unconfigured library as a fileless document would
 * tell the operator something false about the document itself.
 */
export async function getDocumentContent(id: string): Promise<DocumentBytes | null> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}/content`);
  if (res.status === 404 || res.status === 409) return null;
  if (!res.ok) throw await failure(res);
  return {
    blob: await res.blob(),
    contentType: res.headers.get('Content-Type')?.split(';')[0].trim() ?? 'application/octet-stream',
  };
}

export async function getDocumentText(id: string): Promise<DocumentChunk[]> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}/text`);
  if (res.status === 404 || res.status === 409) return [];
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentChunk[];
}

/**
 * The Discovery agent's ranked candidate pool.
 *
 * 404 before Discovery has run — the normal pre-run state, hence the sentinel. The doc is READ-ONLY:
 * the operator never re-tiers a candidate by hand (spec §1.4). To change one they tell the agent why,
 * through POST /projects/{id}/stages/discovery/revise, and the reason is recorded as a Learned Conclusion.
 */
export async function getCandidates(projectId: string): Promise<CandidatesDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/candidates`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as CandidatesDoc;
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

/**
 * The unified project table — the ONE projection every phase screen and the XLSX export read.
 *
 * Deliberately NOT a NotFound union: a project that has only reached Discovery returns 200 with rows
 * carrying just that group. An analysis in progress is a state, not a missing resource, and treating it as
 * absent would blank the table for exactly the projects an operator watches most closely.
 */
export async function getTable(projectId: string): Promise<ProjectTable> {
  const res = await authorizedFetch(`${p(projectId)}/table`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as ProjectTable;
}

export async function getAmendments(projectId: string): Promise<Amendment[]> {
  const res = await authorizedFetch(`${p(projectId)}/amendments`);
  if (!res.ok) throw await failure(res);
  return ((await res.json()) as { amendments: Amendment[] }).amendments;
}

/**
 * Amend a requirement. Returns the conflict body on 409 rather than throwing, because a 409 here is not an
 * error — it is the system asking a question. The amendment would void a signature already on file, and the
 * caller has to show the operator WHICH signatures and WHAT will re-run before offering to proceed.
 *
 * Throwing would collapse that into a generic failure toast, and the operator would never learn what they
 * were about to destroy.
 */
/*
 * NOTE: no screen calls this any more. Amending is a conversation (spec §16.2) — the operator tells
 * the intake agent what changed and why, and the agent's tool posts to this endpoint server-side. The
 * helper stays because the ENDPOINT stays, and because the 409 contract it documents is the one the
 * agent has to honour. It is not a control anybody may wire to a button: a form here would be the
 * direct edit Law 4 forbids, with the reason demoted to one more field.
 */
export async function postAmendment(
  projectId: string,
  body: { field: string; value: string; reason: string; componentId?: string; confirmSignatureVoid?: boolean },
): Promise<{ ok: true } | { ok: false; conflict: AmendmentConflict }> {
  const res = await authorizedFetch(`${p(projectId)}/amendments`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (res.status === 409) return { ok: false, conflict: (await res.json()) as AmendmentConflict };
  if (!res.ok) throw await failure(res);
  return { ok: true };
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

/* ---------------------------------------------------------------------------
   DECISION — the VP hard gate and procurement release.
   --------------------------------------------------------------------------- */

/** The assembled decision, or the sentinel before the Decision stage has run. */
export async function getDecision(projectId: string): Promise<DecisionDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/decision`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DecisionDoc;
}

/** The VP gate's armability, computed server-side against the rules the POST enforces. Never 404s. */
export async function getVpGate(projectId: string): Promise<VpGate> {
  const res = await authorizedFetch(`${p(projectId)}/gate/vp`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as VpGate;
}

/**
 * Sign or reject the VP gate — the highest-consequence call in the system.
 *
 * Approval writes the Marker Library and a Learned Conclusion and releases procurement; rejection
 * records a locked gate WITH the reason, so the audit trail shows the VP looked and said no.
 *
 * The backend re-checks armability and 422s if the record moved (a revision in flight, a stage no
 * longer parked at awaiting-VP, a code that is not in the DosingDoc). So this can fail even when the
 * button looked enabled — catch the ApiError, re-read the gate, and show its fresh blockers.
 */
export async function recordVpDetermination(
  projectId: string,
  req: VpDeterminationRequest,
): Promise<{ status: 'approved' | 'rejected' }> {
  const res = await postJson(`${p(projectId)}/decision/determination`, req);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { status: 'approved' | 'rejected' };
}

/**
 * Order one substance — gated by MSDS-before-order (spec §5).
 *
 * The 422 chain is release → signed-code membership → MSDS, so the error is always the FIRST rule the
 * order breaks and a 4xx always means no order record exists. Surface the message verbatim: it names
 * which rule stopped it.
 */
export async function orderSubstance(projectId: string, cas: string): Promise<{ ordered: string }> {
  const res = await authorizedFetch(`${p(projectId)}/orders/${encodeURIComponent(cas)}`, {
    method: 'POST',
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as { ordered: string };
}

/**
 * GET /projects/{id}/pool — the need-driven pool the discovery agent proposed (ProjectEndpoints.cs:186).
 *
 * 404 before that agent has run, which is a STATE and not an error: a project sitting at
 * awaiting-confirmation has no pool yet and never should.
 */
export async function getPool(projectId: string): Promise<PoolDoc | NotFound> {
  const res = await authorizedFetch(`${p(projectId)}/pool`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as PoolDoc;
}
