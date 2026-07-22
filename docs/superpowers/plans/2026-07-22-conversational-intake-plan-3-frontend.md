# Conversational Intake — Plan 3: The Frontend

> **STATUS: COMPLETE (2026-07-22).** All 7 tasks executed subagent-driven. `src/Smx.Backend.sln`
> **816 tests** (812 baseline, +4), `src/Smx.Functions.sln` unchanged at 177, `src/smx-web`
> **136 tests / 15 files** (98 baseline, +38 — the plan projected 125). Both solutions build with
> **zero warnings**, `npm run build` clean, working tree clean. All four named properties in Task 7
> Step 3 pass, and `MockBadge` came off `routes/stages/Intake.tsx` and **nowhere else** — the five
> other mocked stage screens keep theirs.
>
> **Corrections this plan needed, found during execution:**
> - **Two holes in this plan's own gate mirror**, both in the permissive direction — the button would
>   have armed on a project the server refuses. `IntakeGate.Check` also rejects duplicate component ids
>   and an `agent-proposed` dossier entry carrying no confidence; `createBlocker` as written here
>   checked neither. Both added, with a test each. Task 3's "read the server gate line by line against
>   the mirror" step is what found them — keep that step in any future mirror.
> - **Two of this plan's test assertions were blind**, and both were verified blind by deleting the
>   feature and watching the test stay green:
>   - Task 4's `getByText(/still open|summary|component/i)` was matched by the Create button's own
>     static hint copy, so it passed with the blocker text removed. Replaced with the verbatim string
>     `createBlocker` returns.
>   - Task 5's `getByText(/agent/i)` was matched by the screen's mandatory "tell the agent why"
>     sentence, so provenance could vanish entirely. Even the obvious repair — "the two rows must read
>     differently" — was satisfied by the state label alone. Provenance now renders in its own
>     `data-said-by` element that the test targets; three mutations (delete it, collapse both phrases
>     to one, drop the confidence) each fail.
>   **The lesson for the next plan: a loose regex over the whole document is not an assertion.** Any
>   screen carrying explanatory copy will contain the words its own tests search for.
> - **`Interview.tsx` must not let a session re-read delete a turn already on screen.** The plan's
>   literal "re-read and replace" would wipe the operator's message and the just-streamed reply if the
>   read lagged the write. The refresh now takes the record for everything the tools wrote but keeps
>   local turns when the record has fewer.
> - **Polling stops on `awaiting-confirmation`** (`anyRunning` counts only `running`/`pending`), so
>   nothing would ever re-read after Start Processing. `useProject` now returns a `refresh` function
>   and `ProjectLayout` passes it down as `onRefresh`.
> - `chip--warn` does not exist in the stylesheet; `AttachmentChip` uses `chip--neutral` plus an inline
>   `--text-warning`, the same move `StageStatusCard` already makes. The verdict chip families
>   (`v/l/x/n`) were deliberately not reused — they *mean* a verdict about a substance.
>
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "New project" opens a conversation instead of a form. The operator talks, drops files, and watches the reply stream in; when the agent creates the project they open it, read what was written, and press **Start Processing**. This is the plan that makes Plans 1 and 2 visible.

**Architecture:** `/new` mints an interview session and redirects to `/new/{sessionId}`, so a closed tab resumes from the record (Law 6). Turns stream over `fetch` + a tested SSE frame parser — `EventSource` cannot POST. The **Create the project** button mirrors `IntakeGate.Check` client-side over a question catalogue **served by the backend**, so the mirror cannot drift from the gate it mirrors. `/p/{id}/intake` renders the brief read-only and carries **Start Processing**.

**Tech Stack:** React 18 + TypeScript + Vite, react-router-dom 6, vitest + @testing-library/react (jsdom for `.test.tsx`, node for `.test.ts`), and two new .NET minimal-API endpoints.

---

## Read this before you touch anything

- **The design:** [`docs/superpowers/specs/2026-07-21-conversational-intake-design.md`](../specs/2026-07-21-conversational-intake-design.md) §8 is what this plan implements; §4.3 (the gate) and §5.2 (an unreadable file is a visible fact) are what the screens must not soften.
- **Plans 1 and 2**, both complete. Plan 1 built the session, the agent, the SSE surface and `POST /projects/{id}/start`; Plan 2 built attachments, extraction and `read_attachment`. This plan adds no agent behaviour.
- **`src/smx-web/README.md`** and the **`MockBadge`** contract. The badge is load-bearing: *"a fabricated verdict that renders identically to a real one is the exact failure this badge prevents."* This plan removes it from exactly one screen, and only because that screen starts reading a real endpoint.
- **The interaction laws in `CLAUDE.md`.** Three govern this plan:
  - **Law 4 — no direct edits to agent output.** The brief screen has NO editable control. Changing the component table means telling the agent why. A tripwire test pins this.
  - **Law 6 — frictionless re-entry.** The interview survives a closed tab; the session id lives in the URL and the transcript is re-read from the record.
  - **Law 9 — gates are operator-signed records.** **Start Processing** is the operator's signature. The agent cannot press it and has no tool that does.

**Baselines — write these in your working notes:**
- `src/smx-web`: **98 tests, 10 files** (`npm test`). `node_modules` is gitignored — run `npm install` first or `vitest` is not found.
- `src/Smx.Backend.sln`: **812 tests**, 0 warnings.

### Five traps, four of which this codebase has already sprung

1. **`[FromServices]` is mandatory on every store parameter in a minimal-API handler.** Miss it and routing breaks for **every** route in the app, `/healthz` included. Task 1 adds two endpoints; both are exposed to this.
2. **The frontend test suite was once blind by construction.** It used to be `environment: 'node'` over `*.test.ts` only, so *not one render test existed* and the entire visual layer could be deleted with the suite still green. See the long comment in `vite.config.ts`. **A screen this plan adds must have a `.test.tsx` that renders it** — a `.test.ts` over its helpers is not coverage of the screen.
3. **`EventSource` cannot POST and cannot send an `Authorization` header.** The interview turn is a POST with a body and a bearer token, so it must be `fetch` + `response.body.getReader()`. An SSE frame **can split across chunk boundaries** — that is why Task 2 makes the parser a separately tested pure function rather than an inline `split('\n\n')`.
4. **`Smx.Backend.Tests` targets `net10.0`; every other project is `net8.0` with `RollForward=Major`.** Do not "fix" it.
5. **A `.tsx` render test needs a router.** Every screen here uses `useParams`/`useNavigate`; rendering one outside a `MemoryRouter` throws. `AppShell.test.tsx` shows the shape to copy.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Backend/Api/IntakeBriefEndpoints.cs` | `GET /projects/{id}/intake-brief`, `GET /intake-questions` |
| `src/smx-web/src/api/sse.ts` | The SSE frame parser. Pure, chunk-boundary-safe. |
| `src/smx-web/src/domain/intakeGate.ts` | Coverage + the client-side mirror of `IntakeGate.Check`. Pure. |
| `src/smx-web/src/routes/Interview.tsx` | `/new` and `/new/:sessionId` — the interview |
| `src/smx-web/src/components/AttachmentChip.tsx` | One attachment + its extraction status |
| `src/smx-web/src/components/IntakeBrief.tsx` | The read-only dossier, used by the intake stage screen |
| `src/smx-web/src/api/sse.test.ts` · `src/domain/intakeGate.test.ts` | |
| `src/smx-web/src/routes/Interview.test.tsx` · `src/components/IntakeBrief.test.tsx` | |
| `src/Smx.Backend.Tests/IntakeBriefEndpointsTests.cs` | |

**Delete:** `src/smx-web/src/routes/NewProject.tsx` (the form).

**Modify:** `src/Smx.Backend/Program.cs` · `src/smx-web/src/App.tsx` · `src/api/types.ts` · `src/api/client.ts` · `src/routes/stages/Intake.tsx` · `src/routes/Projects.tsx` · `src/domain/stages.ts` (only if it enumerates statuses)

---

## Task 1: The two endpoints the frontend has nothing to read without

Plan 1 put `GetIntakeBriefAsync` on `IRecordStore` and never exposed it. Nothing can render a brief today.

The second endpoint is the question catalogue. The coverage line (*"11 of 17 covered"*) and the client-side gate mirror both need to know what the full question set **is**. Hard-coding 18 ids in TypeScript would put a second copy of the catalogue in a second language, and the two would drift — the exact failure `IntakeQuestions.Description` exists to prevent on the tool-description side. Serving it keeps one source.

**Files:**
- Create: `src/Smx.Backend/Api/IntakeBriefEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/IntakeBriefEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

Follow the host-building shape `IntakeSessionEndpointsTests` uses (`IClassFixture<WebApplicationFactory<Program>>` + a `NewApp(...)` helper via `WithWebHostBuilder`) — read it and match its real signature.

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class IntakeBriefEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public IntakeBriefEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> NewApp(IRecordStore store) =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(store)));

    [Fact]
    public async Task Get_ReturnsTheBrief_WithItsDossierAndTranscript()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertIntakeBriefAsync(new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief("proj-1"), ProjectId = "proj-1", SessionId = "isx-aaaa1111",
            Summary = "Acme make a 500 ml PET bottle.",
            CreatedAt = "2026-07-22T10:00:00.0000000Z",
            Dossier = [new() { QuestionId = "raw-materials", State = DossierState.Answered,
                               Answer = "PET resin", Provenance = "operator" }],
            Transcript = [new() { Role = "operator", Text = "Acme, PET bottles.",
                                  CreatedAt = "2026-07-22T10:00:00.0000000Z" }],
        });
        using var app = NewApp(store);

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/projects/proj-1/intake-brief");

        Assert.Equal("Acme make a 500 ml PET bottle.", body.GetProperty("summary").GetString());
        Assert.Equal("raw-materials", body.GetProperty("dossier")[0].GetProperty("questionId").GetString());
        // The transcript travels WITH the conclusions. When a regulatory verdict later hinges on the
        // operator having said the adhesive is water-based, that sentence must be in the record beside
        // the dossier row it produced — not summarised away.
        Assert.Equal("Acme, PET bottles.", body.GetProperty("transcript")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Get_IsA404_ForAProjectCreatedThroughTheForm()
    {
        // Every project created before this feature has no brief. That is a normal state, not an error
        // condition — the intake screen renders the record it does have and says so plainly.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.NotFound,
            (await app.CreateClient().GetAsync("/projects/proj-nope/intake-brief")).StatusCode);
    }

    [Fact]
    public async Task Questions_ReturnTheWholeCatalogue_SoTheFrontendNeedsNoSecondCopy()
    {
        // The coverage line and the client-side gate mirror both need the full question set. A
        // hard-coded copy in TypeScript would be a second catalogue in a second language, and the two
        // would drift — which is the exact failure IntakeQuestions.Description exists to prevent on the
        // tool-description side.
        using var app = NewApp(new InMemoryRecordStore());

        var body = await app.CreateClient().GetFromJsonAsync<JsonElement>("/intake-questions");

        Assert.Equal(IntakeQuestions.All.Count, body.GetArrayLength());
        var ids = body.EnumerateArray().Select(q => q.GetProperty("id").GetString()).ToList();
        foreach (var q in IntakeQuestions.All) Assert.Contains(q.Id, ids);
        // `why` is shown to the OPERATOR when they expand "see what's open" — it is the difference
        // between a checklist and an explanation of what the analysis will be missing.
        Assert.False(string.IsNullOrWhiteSpace(body[0].GetProperty("why").GetString()));
    }

    [Fact]
    public async Task Healthz_StillRoutes_BesideTheBriefSurface()
    {
        // Trap 1's regression guard: a missing [FromServices] on any store parameter breaks routing for
        // the WHOLE app, and that failure shows up nowhere else.
        using var app = NewApp(new InMemoryRecordStore());
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/healthz")).StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter IntakeBriefEndpointsTests
```

Expected: FAIL — 404 on both new routes.

- [ ] **Step 3: Implement `src/Smx.Backend/Api/IntakeBriefEndpoints.cs`**

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// What the operator reads when they open a project the interview agent created, and the catalogue the
/// interview screen measures coverage against.
public static class IntakeBriefEndpoints
{
    public static void MapIntakeBriefEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the
        // top of ProjectEndpoints. Without it, minimal APIs mis-infer it as a body param and break
        // routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/intake-brief", async (
            string projectId, [FromServices] IRecordStore store, CancellationToken ct) =>
            await store.GetIntakeBriefAsync(projectId, ct) is { } brief
                ? Results.Json(brief, Json.Options)
                // A project created through the old form has no brief. NOT an error — the intake screen
                // renders the record it does have and says plainly that there was no interview.
                : Results.NotFound());

        // The catalogue, served rather than duplicated. The frontend's coverage line and its
        // client-side gate mirror both need the full question set; a hard-coded TypeScript copy would
        // drift from this one the first time a question is added, and the screen would then under-report
        // what the analysis is missing. Static, cacheable, and takes no store.
        app.MapGet("/intake-questions", () => Results.Json(IntakeQuestions.All, Json.Options));
    }
}
```

In `src/Smx.Backend/Program.cs`, call `app.MapIntakeBriefEndpoints();` beside the other `Map*Endpoints()` calls.

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter IntakeBriefEndpointsTests
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Whole suite and commit**

```bash
dotnet build src/Smx.Backend.sln   # must stay at 0 warnings
dotnet test src/Smx.Backend.sln
git add -A src/
git commit -m "feat(intake): expose the brief and the question catalogue"
```

Expected: 812 + 4 = 816, zero failures.

---

## Task 2: Frontend types, the API client, and a chunk-safe SSE parser

**Files:**
- Create: `src/smx-web/src/api/sse.ts`, `src/smx-web/src/api/sse.test.ts`
- Modify: `src/smx-web/src/api/types.ts`, `src/smx-web/src/api/client.ts`

- [ ] **Step 1: Write the failing SSE test** — `src/smx-web/src/api/sse.test.ts`

```ts
import { describe, expect, it } from 'vitest';
import { createSseParser } from './sse';

describe('SSE frame parser', () => {
  it('reads a whole frame', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: {"text":"Hel"}\n\n')).toEqual([
      { event: 'chunk', data: '{"text":"Hel"}' },
    ]);
  });

  it('reads several frames from one chunk', () => {
    const push = createSseParser();
    const events = push('event: chunk\ndata: a\n\nevent: done\ndata: b\n\n');
    expect(events.map((e) => e.event)).toEqual(['chunk', 'done']);
  });

  /**
   * The load-bearing one. A network chunk boundary falls wherever TCP puts it, so a frame arrives
   * split roughly whenever the reply is long enough to matter. An inline `split('\n\n')` over each
   * chunk loses the tail of every split frame — and the symptom is dropped words in the middle of a
   * streamed reply, which reads as the model being incoherent rather than as a parsing bug.
   */
  it('holds a partial frame until the rest arrives', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: {"te')).toEqual([]);
    expect(push('xt":"Hello"}\n\n')).toEqual([{ event: 'chunk', data: '{"text":"Hello"}' }]);
  });

  it('survives a boundary that lands inside the frame separator', () => {
    const push = createSseParser();
    expect(push('event: chunk\ndata: a\n')).toEqual([]);
    expect(push('\nevent: done\ndata: b\n\n').map((e) => e.event)).toEqual(['chunk', 'done']);
  });

  it('ignores keep-alive comments and blank leading lines', () => {
    const push = createSseParser();
    expect(push(': keep-alive\n\nevent: done\ndata: {}\n\n')).toEqual([
      { event: 'done', data: '{}' },
    ]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd src/smx-web && npm install && npx vitest run src/api/sse.test.ts
```

Expected: FAIL — `./sse` does not exist.

- [ ] **Step 3: Implement `src/smx-web/src/api/sse.ts`**

```ts
export interface SseEvent {
  event: string;
  data: string;
}

/**
 * Server-sent-event frames out of a byte stream that arrives in arbitrary chunks.
 *
 * A separate, tested function rather than an inline `split('\n\n')` for one reason: a chunk boundary
 * falls wherever the network puts it, so a frame arrives split roughly whenever the reply is long
 * enough to be worth streaming. Splitting per chunk silently drops the tail of every split frame, and
 * the symptom — words missing from the middle of a reply — reads as the model being incoherent rather
 * than as a parsing bug.
 *
 * Returns a `push(chunk)` that yields the frames completed by that chunk and keeps the remainder.
 */
export function createSseParser(): (chunk: string) => SseEvent[] {
  let buffer = '';

  return function push(chunk: string): SseEvent[] {
    buffer += chunk;
    const events: SseEvent[] = [];

    let separator = buffer.indexOf('\n\n');
    while (separator !== -1) {
      const frame = buffer.slice(0, separator);
      buffer = buffer.slice(separator + 2);
      const parsed = parseFrame(frame);
      if (parsed) events.push(parsed);
      separator = buffer.indexOf('\n\n');
    }
    return events;
  };
}

function parseFrame(frame: string): SseEvent | null {
  let event = 'message';
  const data: string[] = [];

  for (const line of frame.split('\n')) {
    // ':' opens a comment — the conventional keep-alive. Not an event.
    if (line.startsWith(':') || line.trim() === '') continue;
    if (line.startsWith('event:')) event = line.slice('event:'.length).trim();
    else if (line.startsWith('data:')) data.push(line.slice('data:'.length).trimStart());
  }

  return data.length > 0 ? { event, data: data.join('\n') } : null;
}
```

- [ ] **Step 4: Add the types** — in `src/smx-web/src/api/types.ts`

```ts
/** StageState.Status — src/Smx.Domain/Records/ProjectDoc.cs (StageStatus). */
export type StageStatus =
  | 'pending'
  | 'running'
  | 'failed'
  | 'needs-review'
  | 'done'
  /**
   * Intake only. The interview agent created this project and wrote its dossier, but NO agent has run
   * and none will until the operator presses Start Processing. It is the line between "the agent
   * created something" and "the analysis is running".
   */
  | 'awaiting-confirmation';

/** DossierState — src/Smx.Domain/Intake/DossierEntry.cs. There is deliberately no "never asked". */
export type DossierState = 'answered' | 'agent-proposed' | 'unknown' | 'not-applicable';

/** DossierEntry — src/Smx.Domain/Intake/DossierEntry.cs */
export interface DossierEntry {
  questionId: string;
  state: DossierState;
  answer: string;
  provenance: string;
  confidence?: string;
  recordedAt: string;
}

/** IntakeQuestion — src/Smx.Domain/Intake/IntakeQuestions.cs, served by GET /intake-questions. */
export interface IntakeQuestion {
  id: string;
  prompt: string;
  why: string;
}

/** AttachmentStatus — src/Smx.Domain/Records/IntakeDocs.cs */
export type AttachmentStatus = 'extracted' | 'unsupported' | 'failed';

/** SessionAttachment — src/Smx.Domain/Records/IntakeDocs.cs */
export interface SessionAttachment {
  fileId: string;
  filename: string;
  contentType: string;
  sizeBytes: number;
  blobPath: string;
  textBlobPath?: string;
  status: AttachmentStatus;
  error?: string;
}

/** InterviewTurn — src/Smx.Domain/Records/IntakeDocs.cs */
export interface InterviewTurn {
  role: 'operator' | 'agent';
  text: string;
  toolCalls: string[];
  createdAt: string;
}

/** IntakeSessionDoc — src/Smx.Domain/Records/IntakeDocs.cs */
export interface IntakeSession {
  sessionId: string;
  status: 'interviewing' | 'created' | 'abandoned';
  client: string;
  product: string;
  summary: string;
  turns: InterviewTurn[];
  attachments: SessionAttachment[];
  dossier: DossierEntry[];
  proposedComponents: ComponentSpec[];
  createdProjectId?: string;
  createdAt: string;
  updatedAt: string;
}

/** IntakeBriefDoc — src/Smx.Domain/Records/IntakeDocs.cs */
export interface IntakeBrief {
  projectId: string;
  sessionId: string;
  summary: string;
  dossier: DossierEntry[];
  components: ComponentSpec[];
  attachments: SessionAttachment[];
  transcript: InterviewTurn[];
  createdAt: string;
}
```

Leave `CreateProjectRequest` and `createProject` in place — `tools/Smx.Eval` and the backend tests still use `POST /projects`, and nothing in this plan removes that path. Only the *form* goes.

- [ ] **Step 5: Add the client functions** — in `src/smx-web/src/api/client.ts`, following the existing `authorizedFetch` / `failure` / `NotFound` conventions exactly

Two written out in full — the 404-sentinel shape and the multipart shape are the two that are easy to
get subtly wrong. Write the rest (`getIntakeQuestions`, `createIntakeSession`, `getIntakeSession`,
`startProject`) the same way, matching how the neighbouring functions in this file already call
`authorizedFetch` and `failure`.

```ts
/**
 * A project created through the old form has no brief. That is a normal state, not a failure, so it
 * is the NotFound sentinel — the same discipline getMatrix already uses for a pre-assembly matrix.
 */
export async function getIntakeBrief(projectId: string): Promise<IntakeBrief | NotFound> {
  const res = await authorizedFetch(`${BASE}/projects/${projectId}/intake-brief`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as IntakeBrief;
}

export async function uploadAttachment(sessionId: string, file: File): Promise<SessionAttachment> {
  const form = new FormData();
  // The field name MUST be "file" — it is what binds to the handler's `IFormFile file` parameter.
  form.append('file', file, file.name);

  const res = await authorizedFetch(`${BASE}/intake-sessions/${sessionId}/attachments`, {
    method: 'POST',
    // NO Content-Type header. The browser has to set it itself so it can append the multipart
    // boundary; setting it by hand produces a body the server cannot parse, and the error looks like
    // a malformed upload rather than a missing boundary.
    body: form,
  });
  if (!res.ok) throw await failure(res);
  return (await res.json()) as SessionAttachment;
}

/**
 * An expired or unknown session is a REAL error the screen must show — not an empty interview it
 * silently starts over, which would strand the operator in a second conversation nobody can find.
 */
export async function getIntakeSession(sessionId: string): Promise<IntakeSession | NotFound> {
  const res = await authorizedFetch(`${BASE}/intake-sessions/${sessionId}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as IntakeSession;
}

export async function getIntakeQuestions(): Promise<IntakeQuestion[]> { /* GET /intake-questions */ }

export async function createIntakeSession(
  client?: string, product?: string,
): Promise<{ sessionId: string }> { /* POST /intake-sessions, body { client, product } */ }

export async function startProject(projectId: string): Promise<{ status: string }> {
  /* POST /projects/{id}/start, no body. 202 Accepted is success. */
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
  const res = await authorizedFetch(`${BASE}/intake-sessions/${sessionId}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
    signal,
  });
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
```

Write these out fully in the real file, matching how the neighbouring functions handle `failure(res)` and the `NotFound` sentinel. Add the imports (`SseEvent`, `createSseParser`, the new types).

- [ ] **Step 6: Verify and commit**

```bash
cd src/smx-web && npx vitest run src/api/sse.test.ts && npm run typecheck && npm test
```

Expected: the 5 SSE tests pass, typecheck clean, suite at 98 + 5 = 103.

```bash
git add -A src/smx-web/
git commit -m "feat(web): the intake API surface, and an SSE parser that survives chunk boundaries"
```

---

## Task 3: The coverage count and the client-side gate mirror

Pure logic, `.test.ts` (node environment — no DOM).

**Files:**
- Create: `src/smx-web/src/domain/intakeGate.ts`, `src/smx-web/src/domain/intakeGate.test.ts`

- [ ] **Step 1: Write the failing test**

```ts
import { describe, expect, it } from 'vitest';
import { coverage, createBlocker } from './intakeGate';
import type { ComponentSpec, DossierEntry, IntakeQuestion, IntakeSession } from '../api/types';

const questions: IntakeQuestion[] = [
  { id: 'raw-materials', prompt: 'What raw materials?', why: 'Discovery screens against them.' },
  { id: 'qc-tests', prompt: 'What QC tests?', why: 'They constrain what is detectable.' },
];

const entry = (questionId: string, state: DossierEntry['state'] = 'answered'): DossierEntry => ({
  questionId, state, answer: 'a', provenance: 'operator', recordedAt: '2026-07-22T10:00:00Z',
});

const component: ComponentSpec = {
  id: 'bottle', material: 'PET', application: 'food contact', markets: ['EU'], objective: 'brand',
};

const session = (over: Partial<IntakeSession> = {}): IntakeSession => ({
  sessionId: 'isx-1', status: 'interviewing', client: 'Acme', product: 'MUFE',
  summary: 'a summary', turns: [], attachments: [],
  dossier: questions.map((q) => entry(q.id)), proposedComponents: [component],
  createdAt: '2026-07-22T10:00:00Z', updatedAt: '', ...over,
});

describe('coverage', () => {
  it('counts every reached question and names the ones that are open', () => {
    const c = coverage([entry('raw-materials')], questions);
    expect(c.covered).toBe(1);
    expect(c.total).toBe(2);
    expect(c.open.map((q) => q.id)).toEqual(['qc-tests']);
  });

  it('counts unknown and not-applicable as covered', () => {
    // The operator genuinely may not know. "unknown" is DATA — it travels downstream as a stated gap.
    // What must never count as covered is a question nobody reached.
    const c = coverage(
      [entry('raw-materials', 'unknown'), entry('qc-tests', 'not-applicable')], questions);
    expect(c.covered).toBe(2);
    expect(c.open).toEqual([]);
  });
});

describe('createBlocker — the client-side mirror of IntakeGate.Check', () => {
  it('passes when everything the server checks is present', () => {
    expect(createBlocker(session(), questions)).toBeNull();
  });

  it('names the questions that are still open', () => {
    const blocker = createBlocker(session({ dossier: [entry('raw-materials')] }), questions);
    expect(blocker).toContain('qc-tests');
  });

  it('refuses a blank summary', () => {
    expect(createBlocker(session({ summary: '  ' }), questions)).not.toBeNull();
  });

  it('refuses with no components', () => {
    expect(createBlocker(session({ proposedComponents: [] }), questions)).not.toBeNull();
  });

  it('refuses a component with no markets, and says why', () => {
    // Same rationale as the server's: zero markets EMPTIES that component's regulatory screen, which
    // is a false-pass mechanism. The message must say so, not just "markets required".
    const blocker = createBlocker(
      session({ proposedComponents: [{ ...component, markets: [] }] }), questions);
    expect(blocker).toMatch(/regulatory screen/i);
  });

  it('refuses a blank client or product', () => {
    expect(createBlocker(session({ client: '' }), questions)).not.toBeNull();
    expect(createBlocker(session({ product: ' ' }), questions)).not.toBeNull();
  });

  /**
   * The honesty rule for a mirror. With NO catalogue loaded, every question looks covered and the
   * button would arm on a project the server will refuse. A mirror that is wrong in the permissive
   * direction is worse than no mirror: the operator presses a button that then fails.
   */
  it('refuses while the catalogue has not loaded yet', () => {
    expect(createBlocker(session(), [])).not.toBeNull();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd src/smx-web && npx vitest run src/domain/intakeGate.test.ts
```

Expected: FAIL — `./intakeGate` does not exist.

- [ ] **Step 3: Implement `src/smx-web/src/domain/intakeGate.ts`**

```ts
import type { DossierEntry, IntakeQuestion, IntakeSession } from '../api/types';

const COVERED: ReadonlySet<string> = new Set([
  'answered', 'agent-proposed', 'unknown', 'not-applicable',
]);

export interface Coverage {
  covered: number;
  total: number;
  open: IntakeQuestion[];
}

/**
 * How much of the catalogue the interview has reached.
 *
 * `unknown` and `not-applicable` COUNT as covered: the operator was asked and there is an answer, even
 * when the answer is "I don't know". What must never count is a question nobody reached — that is the
 * distinction the dossier exists to preserve, and prose cannot make it.
 */
export function coverage(dossier: DossierEntry[], questions: IntakeQuestion[]): Coverage {
  const reached = new Set(dossier.filter((e) => COVERED.has(e.state)).map((e) => e.questionId));
  const open = questions.filter((q) => !reached.has(q.id));
  return { covered: questions.length - open.length, total: questions.length, open };
}

/**
 * Why the project cannot be created yet, or null when it can.
 *
 * A MIRROR of IntakeGate.Check (src/Smx.Domain/Intake/IntakeGate.cs), for the same reason the old
 * creation form mirrored CreateProjectRequest.Validate: the operator should not press a button that
 * then fails. It is a convenience, never the contract — the server re-checks and its refusal is the
 * one that counts.
 *
 * It errs toward REFUSING. An empty catalogue (still loading, or the request failed) makes every
 * question look covered, so it blocks rather than arming a button the server will reject.
 */
export function createBlocker(
  session: IntakeSession, questions: IntakeQuestion[],
): string | null {
  if (questions.length === 0) return 'still loading the question list…';
  if (!session.client.trim() || !session.product.trim())
    return 'the agent still needs the client and the product.';
  if (!session.summary.trim()) return 'the agent has not written the summary yet.';
  if (session.proposedComponents.length === 0)
    return 'the agent has not proposed the component breakdown yet — every stage downstream runs per component.';

  for (const c of session.proposedComponents) {
    if (!c.id.trim() || !c.material.trim() || !c.application.trim() || !c.objective.trim())
      return `component '${c.id || '(unnamed)'}' is incomplete.`;
    if (c.markets.length === 0)
      return `component '${c.id}' has no target markets, which would leave it with an empty regulatory screen.`;
  }

  const { open } = coverage(session.dossier, questions);
  if (open.length > 0)
    return `${open.length} question${open.length === 1 ? '' : 's'} still open: ${open
      .map((q) => q.id)
      .join(', ')}.`;

  return null;
}
```

- [ ] **Step 4: Verify and commit**

```bash
cd src/smx-web && npx vitest run src/domain/intakeGate.test.ts && npm test
```

Expected: 10 new tests, suite at 113.

```bash
git add -A src/smx-web/
git commit -m "feat(web): the coverage count and a gate mirror that errs toward refusing"
```

---

## Task 4: `/new` — the interview replaces the form

**Files:**
- Create: `src/smx-web/src/routes/Interview.tsx`, `src/smx-web/src/components/AttachmentChip.tsx`, `src/smx-web/src/routes/Interview.test.tsx`
- Delete: `src/smx-web/src/routes/NewProject.tsx`
- Modify: `src/smx-web/src/App.tsx`

- [ ] **Step 1: Write the failing test** — `src/smx-web/src/routes/Interview.test.tsx`

Mock the API client module rather than standing up msw: these assertions are about the screen's behaviour, and `vi.mock` keeps them readable. Render inside a `MemoryRouter` (trap 5).

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Interview } from './Interview';
import type { IntakeSession } from '../api/types';

vi.mock('../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeQuestions: vi.fn(),
  getIntakeSession: vi.fn(),
  createIntakeSession: vi.fn(),
  sendInterviewMessage: vi.fn(),
  uploadAttachment: vi.fn(),
}));
import * as api from '../api/client';

const QUESTIONS = [
  { id: 'raw-materials', prompt: 'What raw materials?', why: 'Discovery screens against them.' },
  { id: 'qc-tests', prompt: 'What QC tests?', why: 'They constrain what is detectable.' },
];

const session = (over: Partial<IntakeSession> = {}): IntakeSession => ({
  sessionId: 'isx-1', status: 'interviewing', client: '', product: '', summary: '',
  turns: [], attachments: [], dossier: [], proposedComponents: [],
  createdAt: '2026-07-22T10:00:00Z', updatedAt: '', ...over,
});

function renderAt(sessionId = 'isx-1') {
  return render(
    <MemoryRouter initialEntries={[`/new/${sessionId}`]}>
      <Routes>
        <Route path="new/:sessionId" element={<Interview />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(api.getIntakeQuestions).mockResolvedValue(QUESTIONS);
  vi.mocked(api.getIntakeSession).mockResolvedValue(session());
});

describe('the interview screen', () => {
  it('replays the transcript from the record, so a closed tab resumes', async () => {
    // Law 6. The MAF session cannot be rehydrated; the record IS the conversation, and the screen
    // must render it rather than starting an empty one.
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      turns: [
        { role: 'operator', text: 'Acme, PET bottles.', toolCalls: [], createdAt: '…10:00:00Z' },
        { role: 'agent', text: 'What are the components?', toolCalls: [], createdAt: '…10:00:05Z' },
      ],
    }));
    renderAt();

    expect(await screen.findByText('Acme, PET bottles.')).toBeInTheDocument();
    expect(screen.getByText('What are the components?')).toBeInTheDocument();
  });

  it('shows how much is covered without presenting a checklist', async () => {
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      dossier: [{ questionId: 'raw-materials', state: 'answered', answer: 'PET',
                  provenance: 'operator', recordedAt: '…' }],
    }));
    renderAt();

    // One collapsed line — the operator can open it, but is never PRESENTED with a form to fill.
    expect(await screen.findByText(/1 of 2 covered/i)).toBeInTheDocument();
    expect(screen.queryByText('What QC tests?')).not.toBeInTheDocument();
  });

  it('lists what is open only when the operator asks', async () => {
    renderAt();
    await userEvent.click(await screen.findByRole('button', { name: /see what.s open/i }));
    expect(screen.getByText(/What QC tests\?/)).toBeInTheDocument();
  });

  /**
   * The load-bearing one. The button must mirror the server's gate, so a refusal is never a surprise —
   * and it must be DISABLED while the dossier is incomplete, with the reason visible.
   */
  it('will not arm Create the project while the gate would refuse', async () => {
    renderAt();
    const create = await screen.findByRole('button', { name: /create the project/i });
    expect(create).toBeDisabled();
    expect(screen.getByText(/still open|summary|component/i)).toBeInTheDocument();
  });

  it('streams the reply as it arrives, and keeps what the operator said', async () => {
    vi.mocked(api.sendInterviewMessage).mockImplementation(async (_id, _text, onEvent) => {
      onEvent({ event: 'chunk', data: JSON.stringify({ text: 'Good — ' }) });
      onEvent({ event: 'chunk', data: JSON.stringify({ text: 'next question.' }) });
      onEvent({ event: 'done', data: JSON.stringify({ createdProjectId: null, toolCalls: [] }) });
    });
    renderAt();

    await userEvent.type(await screen.findByRole('textbox'), 'Acme, PET bottles.');
    await userEvent.click(screen.getByRole('button', { name: /send/i }));

    // The operator's own words appear immediately — losing them to a slow or failed model call would
    // be the worst possible failure of Law 6.
    expect(await screen.findByText('Acme, PET bottles.')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/Good — next question\./)).toBeInTheDocument());
  });

  it('shows an unreadable attachment by name and says the agent cannot read it', async () => {
    // Design §5.2: an unreadable file is a VISIBLE FACT, never silence — on this screen too, so the
    // operator understands why the agent is about to ask them what it shows.
    vi.mocked(api.getIntakeSession).mockResolvedValue(session({
      attachments: [{ fileId: 'att-1', filename: 'line-photo.jpg', contentType: 'image/jpeg',
                      sizeBytes: 10, blobPath: 'p', status: 'unsupported',
                      error: 'there is no extractor for .jpg files' }],
    }));
    renderAt();

    expect(await screen.findByText('line-photo.jpg')).toBeInTheDocument();
    expect(screen.getByText(/couldn.t read|cannot read/i)).toBeInTheDocument();
  });
});
```

Two notes on this test file:

- Install `@testing-library/user-event` if it is not already a devDependency (`npm i -D @testing-library/user-event`); check `package.json` first.
- `vi.mock('../api/client', …)` replaces the **whole module**, so anything else `Interview.tsx` imports from it (`ApiError`, `NotFound`, …) must appear in the factory or it is `undefined` at runtime and the failure reads as an unrelated `TypeError`. Keep the factory in step with the component's imports.

- [ ] **Step 2: Run to verify it fails**

```bash
cd src/smx-web && npx vitest run src/routes/Interview.test.tsx
```

Expected: FAIL — `./Interview` does not exist.

- [ ] **Step 3: Implement `AttachmentChip.tsx`**

```tsx
import type { SessionAttachment } from '../api/types';

/**
 * One attachment and what became of it.
 *
 * An `unsupported` or `failed` file is shown as prominently as a read one, by name and type. That is
 * design §5.2 on the screen: the agent is about to ask the operator what the file shows, and the
 * operator needs to already know why. A chip that quietly omitted unreadable files would make the
 * agent's question look like a non-sequitur — and, worse, make a file the analysis never saw look
 * like one it did.
 */
export function AttachmentChip({ attachment }: { attachment: SessionAttachment }) {
  const unread = attachment.status !== 'extracted';
  return (
    <span className={`chip ${unread ? 'chip--warn' : 'chip--neutral'}`} title={attachment.error ?? ''}>
      <i className="ti ti-paperclip" aria-hidden="true" />
      <b>{attachment.filename}</b>
      <span className="tiny muted">
        {unread ? "couldn't read this one — the agent will ask you about it" : 'read'}
      </span>
    </span>
  );
}
```

Match the real class vocabulary in `src/styles/` — read `primitives.css` / `craft.css` and use chip modifiers that exist rather than inventing `chip--warn` if it is absent.

- [ ] **Step 4: Implement `Interview.tsx`**

The two load-bearing pieces in full — the session bootstrap (Law 6) and the streaming send. Everything
else is layout around them.

```tsx
export function Interview() {
  const { sessionId } = useParams<{ sessionId?: string }>();
  const navigate = useNavigate();
  const [questions, setQuestions] = useState<IntakeQuestion[]>([]);
  const [session, setSession] = useState<IntakeSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [streaming, setStreaming] = useState<string | null>(null);   // the agent turn being received
  const [sending, setSending] = useState(false);

  // No sessionId in the URL: mint one and put it there. The id lives in the URL, not in component
  // state, precisely so a reload, a bookmark or a closed tab all resume the SAME interview — Law 6.
  // `replace` so Back does not walk into a /new that mints a second session.
  useEffect(() => {
    if (sessionId) return;
    let cancelled = false;
    createIntakeSession()
      .then(({ sessionId: id }) => { if (!cancelled) navigate(`/new/${id}`, { replace: true }); })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    return () => { cancelled = true; };
  }, [sessionId, navigate]);

  useEffect(() => {
    if (!sessionId) return;
    Promise.all([getIntakeQuestions(), getIntakeSession(sessionId)])
      .then(([qs, s]) => {
        setQuestions(qs);
        // NotFound is a real error here, NOT an empty interview. Silently starting a second
        // conversation would strand the operator in one nobody can find.
        if (s === NotFound) setError('This interview has expired or never existed.');
        else setSession(s);
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)));
  }, [sessionId]);

  async function send(text: string) {
    if (!sessionId || !text.trim() || sending) return;
    setSending(true);
    setDraft('');
    // The operator's own words go on screen IMMEDIATELY. The server persists them before the model
    // runs for the same reason: losing what they said to a slow or failed model call would be the
    // worst possible failure of Law 6.
    setSession((s) => s && { ...s, turns: [...s.turns, {
      role: 'operator', text, toolCalls: [], createdAt: new Date().toISOString(),
    }] });

    let reply = '';
    let created: string | null = null;
    try {
      await sendInterviewMessage(sessionId, text, (e) => {
        if (e.event === 'chunk') {
          reply += (JSON.parse(e.data) as { text: string }).text;
          setStreaming(reply);
        } else if (e.event === 'done') {
          created = (JSON.parse(e.data) as { createdProjectId: string | null }).createdProjectId;
        }
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setStreaming(null);
      setSending(false);
    }

    // Re-read rather than patching local state: the agent's TOOLS mutated the session while the turn
    // ran (findings, components, attachments), and only the server knows what they wrote.
    const refreshed = await getIntakeSession(sessionId);
    if (refreshed !== NotFound) setSession(refreshed);
    if (created) {
      rememberProject({ projectId: created, client: refreshed !== NotFound ? refreshed.client : '',
        product: refreshed !== NotFound ? refreshed.product : '',
        createdAt: new Date().toISOString() });
      navigate(`/p/${created}/intake`);
    }
  }
  // … render: transcript, streaming turn, attachment chips, composer, coverage line, create button
}
```

The rest, all pinned by the tests above:

- **Composer:** a `<textarea>` plus a file input and a drop zone. Dropping or choosing a file calls `uploadAttachment(sessionId, file)` then re-fetches the session. Send is disabled while `sending`.
- **Coverage line:** ONE row — `{covered} of {total} covered · see what's open` — collapsed by default, expanding to the open questions' `prompt` and `why`. Never rendered as a form: the operator came here to avoid one, and a visible checklist is a form with extra steps.
- **Create the project:** disabled while `createBlocker(session, questions)` is non-null, with the blocker text beside it. Pressing it **sends a message asking the agent to create the project** — there is no `create_project` HTTP endpoint, because creation is the agent's tool and a second path to it would be a way to create a project the gate never saw. Put that sentence in a comment; it is the reason the button is not a POST.
- **`rememberProject`** (from `hooks/useRecentProjects`) was called by the deleted form after creation. The interview navigates to a project the *agent* created, so it is called on that navigation instead — otherwise a newly created project vanishes from "recent".

- [ ] **Step 5: Delete the form and re-point the route**

```bash
git rm src/smx-web/src/routes/NewProject.tsx
```

In `src/smx-web/src/App.tsx`, replace the `NewProject` import and route with:

```tsx
        <Route path="new" element={<Interview />} />
        <Route path="new/:sessionId" element={<Interview />} />
```

Check nothing else imports `NewProject` (`grep -rn NewProject src/`). `rememberProject` from `hooks/useRecentProjects` was called by the form after creation — the interview navigates to a project the *agent* created, so call it on that navigation instead, or the new project vanishes from "recent".

- [ ] **Step 6: Verify and commit**

```bash
cd src/smx-web && npx vitest run src/routes/Interview.test.tsx && npm run typecheck && npm run build && npm test
```

Expected: 6 new tests, suite at 119. `npm run build` must succeed — it runs `tsc --noEmit` first and is what catches a type error the tests do not reach.

```bash
git add -A src/smx-web/
git commit -m "feat(web): New project opens an interview, not a form"
```

---

## Task 5: `/p/{id}/intake` — the brief, and Start Processing

**Files:**
- Create: `src/smx-web/src/components/IntakeBrief.tsx`, `src/smx-web/src/components/IntakeBrief.test.tsx`
- Modify: `src/smx-web/src/routes/stages/Intake.tsx`

- [ ] **Step 1: Write the failing test** — `src/smx-web/src/components/IntakeBrief.test.tsx`

```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { IntakeBrief } from './IntakeBrief';
import type { IntakeBrief as Brief } from '../api/types';

const brief = (over: Partial<Brief> = {}): Brief => ({
  projectId: 'proj-1', sessionId: 'isx-1', summary: 'Acme make a 500 ml PET bottle.',
  components: [{ id: 'bottle', material: 'PET', application: 'food contact',
                 markets: ['EU', 'US'], objective: 'brand' }],
  dossier: [
    { questionId: 'raw-materials', state: 'answered', answer: 'PET resin',
      provenance: 'operator', recordedAt: '…' },
    { questionId: 'qc-tests', state: 'unknown', answer: "client hasn't replied",
      provenance: 'operator', recordedAt: '…' },
    { questionId: 'marker-addition-point', state: 'agent-proposed', answer: 'after the blow moulder',
      provenance: 'agent', confidence: 'low', recordedAt: '…' },
    { questionId: 'equipment', state: 'not-applicable', answer: 'no dedicated tooling',
      provenance: 'operator', recordedAt: '…' },
  ],
  attachments: [], transcript: [], createdAt: '2026-07-22T10:00:00Z', ...over,
});

const show = (b = brief(), onStart = vi.fn()) =>
  render(<MemoryRouter><IntakeBrief brief={b} canStart onStart={onStart} /></MemoryRouter>);

describe('the intake brief', () => {
  it('renders the summary and the proposed components', () => {
    show();
    expect(screen.getByText(/500 ml PET bottle/)).toBeInTheDocument();
    expect(screen.getByText('bottle')).toBeInTheDocument();
    expect(screen.getByText(/EU/)).toBeInTheDocument();
  });

  it('distinguishes every dossier state, including the agent-proposed confidence', () => {
    // Provenance collapse is the failure this screen must not permit: an agent inference and an
    // operator statement must never render the same, or the operator signs off on the model's guess
    // believing they said it.
    show();
    expect(screen.getByText(/PET resin/)).toBeInTheDocument();
    expect(screen.getByText(/client hasn't replied/)).toBeInTheDocument();
    expect(screen.getByText(/after the blow moulder/)).toBeInTheDocument();
    expect(screen.getByText(/agent/i)).toBeInTheDocument();
    expect(screen.getByText(/low/i)).toBeInTheDocument();
    expect(screen.getByText(/not.applicable/i)).toBeInTheDocument();
  });

  it('states how many questions the analysis will carry as unknown', () => {
    // Beside the Start button, because it is what the operator is signing for.
    show();
    expect(screen.getByText(/1 question/i)).toBeInTheDocument();
  });

  /**
   * THE tripwire for Law 4. Nothing the agent produced may be hand-edited: the operator changes it by
   * telling the agent WHY, which is also how the change earns a Learned Conclusion. A stray <input>
   * here would quietly reintroduce silent edits to an analytical record, with no reason captured and
   * nothing learned.
   */
  it('offers no way to edit anything the agent wrote', () => {
    const { container } = show();
    expect(container.querySelectorAll('input, textarea, select')).toHaveLength(0);
    expect(container.querySelectorAll('[contenteditable="true"]')).toHaveLength(0);
  });

  it('says how to change something, since nothing is editable', () => {
    show();
    expect(screen.getByText(/tell the agent/i)).toBeInTheDocument();
  });

  it('only offers Start Processing when the project is awaiting confirmation', () => {
    render(<MemoryRouter><IntakeBrief brief={brief()} canStart={false} onStart={vi.fn()} /></MemoryRouter>);
    expect(screen.queryByRole('button', { name: /start processing/i })).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

```bash
cd src/smx-web && npx vitest run src/components/IntakeBrief.test.tsx
```

Expected: FAIL — `./IntakeBrief` does not exist.

- [ ] **Step 3: Implement `IntakeBrief.tsx`**

A presentational component taking `{ brief, canStart, onStart }`. The dossier row is the part worth
specifying exactly, because it is where provenance can collapse:

```tsx
const STATE_MARK: Record<DossierState, { icon: string; label: string }> = {
  answered: { icon: 'ti-check', label: 'answered' },
  // Rendered DIFFERENTLY from `answered`, always with its confidence. An agent inference that reads
  // like an operator statement is the provenance collapse the dossier exists to prevent: the operator
  // signs off on the model's guess believing they said it.
  'agent-proposed': { icon: 'ti-robot', label: 'agent-proposed' },
  // A stated gap, carried INTO the analysis rather than hidden. It is not a failure.
  unknown: { icon: 'ti-alert-triangle', label: 'unknown' },
  'not-applicable': { icon: 'ti-minus', label: 'not-applicable' },
};

function DossierRow({ entry }: { entry: DossierEntry }) {
  const mark = STATE_MARK[entry.state];
  return (
    <div className="row">
      <i className={`ti ${mark.icon}`} aria-hidden="true" />
      <b>{entry.questionId}</b>
      <span>{entry.answer}</span>
      <span className="tiny muted">
        {mark.label} · {entry.provenance}
        {entry.confidence ? ` · confidence ${entry.confidence}` : ''}
      </span>
    </div>
  );
}
```

Around it: the summary, the component table, the attachments (reusing `AttachmentChip`), a collapsible
transcript, and — only when `canStart` — the **Start Processing** button, with the unknown count stated
beside it (*"N questions will be carried into the analysis as unknowns"*) and the sentence *"To change
anything above, tell the agent why — it re-derives and records the reason."*

**No `input`, `textarea`, `select` or `contenteditable` anywhere in this component.** That is not a style
preference; it is Law 4, and the test above is its tripwire. Use `<details>`/`<summary>` for the
collapsible transcript — it needs no form control.

- [ ] **Step 4: Wire it into the intake stage screen**

In `src/smx-web/src/routes/stages/Intake.tsx`:
- Fetch `getIntakeBrief(project.projectId)` (a small `useEffect` + state, or follow whatever hook pattern the neighbouring screens use — read `hooks/useProject.ts` and match it).
- When a brief exists: render `<IntakeBrief …>` and **delete the mock zone entirely** — the marker-library reuse fixture and its `MockBadge` both go. `canStart` is `project.stages.intake.status === 'awaiting-confirmation'`; `onStart` calls `startProject` then refreshes.
- When it is `NotFound`: keep the existing "Real — the record" zone and state plainly that this project was created through the form and has no interview brief. **This is not a `MockBadge` case** — nothing is fabricated, the brief simply does not exist.

> **On removing the `MockBadge`:** it comes off *because the screen now reads a real endpoint*, which is exactly the condition its own doc comment sets. Do not remove it from any other screen in this plan.

- [ ] **Step 5: Verify and commit**

```bash
cd src/smx-web && npx vitest run src/components/IntakeBrief.test.tsx && npm run typecheck && npm run build && npm test
```

Expected: 6 new tests, suite at 125.

```bash
git add -A src/smx-web/
git commit -m "feat(web): the intake brief, read-only by law, with Start Processing"
```

---

## Task 6: The projects list distinguishes a project that has not started

**Files:**
- Modify: `src/smx-web/src/routes/Projects.tsx` (and `src/domain/stages.ts` only if it enumerates statuses)
- Test: whichever `.test.ts(x)` covers the list's grouping — **read `Projects.tsx` first** and find how it groups (`groups.settled`, `StatCard`, `MiniSpine`). If no test file exists for it, create `src/smx-web/src/routes/Projects.test.tsx`.

- [ ] **Step 1: Write the failing test**

Pin that a project whose intake stage is `awaiting-confirmation` is shown as **created but not started**, and is not counted among the running or settled ones. Write it concretely against the real grouping helper — if the grouping is a pure function, test it as `.test.ts`; if it only exists inside the component, render the component in a `MemoryRouter` and assert on what the operator sees.

The reason this matters: an interview-created project has a full dossier and looks complete. If the list renders it identically to a running project, the operator believes the analysis is under way when nothing has been dispatched, and the project sits untouched indefinitely.

- [ ] **Step 2: Run to verify it fails, implement, verify it passes**

Add the status to whatever `StageStatus` union or status-label map the file uses, give it a distinct label (*"awaiting your confirmation"* / *"not started"*), and make sure it is grouped apart from both running and settled.

- [ ] **Step 3: Commit**

```bash
cd src/smx-web && npm run typecheck && npm test
git add -A src/smx-web/
git commit -m "feat(web): a created-but-not-started project reads as one"
```

---

## Task 7: Full verification

- [ ] **Step 1: Everything builds**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet build src/Smx.Backend.sln
dotnet build src/Smx.Functions.sln
cd src/smx-web && npm run build
```

Expected: 0 warnings from both solutions; `npm run build` succeeds (it runs `tsc --noEmit` first).

- [ ] **Step 2: Every test**

```bash
cd /home/elimeshi/projects/repos/SMX
dotnet test src/Smx.Backend.sln
dotnet test src/Smx.Functions.sln
cd src/smx-web && npm test
```

Expected: `Smx.Backend.sln` **816+**, `Smx.Functions.sln` 177 unchanged, `smx-web` **125+** (from 98). A count *below* baseline means a test was deleted — find out which and why.

- [ ] **Step 3: Confirm the properties that matter, by name**

```bash
cd src/smx-web && npx vitest run -t "offers no way to edit anything the agent wrote"
npx vitest run -t "will not arm Create the project while the gate would refuse"
npx vitest run -t "holds a partial frame until the rest arrives"
npx vitest run -t "refuses while the catalogue has not loaded yet"
```

Expected: each passes. These four are the plan: Law 4 has a tripwire, the gate mirror refuses rather than misleads, streaming survives the network, and a half-loaded screen cannot arm a button the server will reject.

- [ ] **Step 4: No `MockBadge` was removed from a screen that still fabricates**

```bash
cd /home/elimeshi/projects/repos/SMX && grep -rn "MockBadge" src/smx-web/src/routes/ src/smx-web/src/components/ | sort
```

Expected: it is gone from `routes/stages/Intake.tsx` and **still present on every other stage screen**. Removing it anywhere else is out of scope for this plan and is a defect.

- [ ] **Step 5: The tree is clean**

```bash
git status --short
```

---

## What Plan 3 deliberately does not do

- **No XRF entry.** `/p/{id}/background` still has no way to enter the physicist's element pools. That is Plan 4, and until then an interview-created project reaches Background with none and the stage parks — the designed behaviour, pinned by `Start_SucceedsWithNoElementPools`.
- **No `MockBadge` removal beyond the intake screen.** Every other stage still renders fixture data and must keep saying so.
- **No agent composer on the brief screen.** Law 4 says a change goes through the agent with a reason; the *mechanism* for that on an existing project is the stage chat, which is a separate, already-disabled surface. Wiring it is not part of making the interview visible.
- **No optimistic attachment chip.** An upload shows up after the server has stored and extracted it. A chip that appeared instantly and then changed status would be showing the operator a file state the record does not yet have.
