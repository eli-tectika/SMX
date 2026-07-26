# Operator Usability Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `src/smx-web` operable — keyboard-reachable, screen-reader-audible, legible on a laptop, and terse — without touching its visual language, its `MockBadge`s, or its disabled gate controls.

**Architecture:** Ten independent tasks over the existing React + Vite frontend. Each adds a small focused module (`hooks/useStickToBottom.ts`, `components/ShortcutSheet.tsx`, `domain/relativeTime.ts`) or edits one existing file; no task depends on another's output except Task 3, which extends `domain/blocking.ts` and is consumed by `ContextBar`. CSS changes go in the existing `styles/base.css` and `styles/craft.css`; no new stylesheet, no new design token except one breakpoint value.

**Tech Stack:** React 18, TypeScript, React Router 6, Vitest + @testing-library/react + jsdom, plain CSS with custom properties. No new dependencies.

---

## Conventions for every task

Run commands from `src/smx-web`:

```
npm test                     # vitest run — the whole suite
npm test -- <path>           # one file
npm run build                # tsc --noEmit && vite build
```

Test style already established in this repo (see `src/components/AppShell.test.tsx`): render with
`MemoryRouter`, query by role and accessible name, and write the comment above a load-bearing test
explaining what breaking it would cost.

**Two rules that override any instruction below.** If a step appears to violate one, stop and ask:

1. No `MockBadge` is removed, and no text asserting that data is fixture data is removed.
2. No disabled gate control is enabled. There is no endpoint to sign a determination against.

---

## File Structure

| File | Status | Responsibility |
| --- | --- | --- |
| `src/hooks/useStickToBottom.ts` | Create | Scroll-follow behaviour for a growing list; used by both conversations |
| `src/hooks/useStickToBottom.test.ts` | Create | Its tests |
| `src/domain/relativeTime.ts` | Create | `secondsAgo(iso)` → "12s ago" / "4m ago"; pure, no React |
| `src/domain/relativeTime.test.ts` | Create | Its tests |
| `src/components/ShortcutSheet.tsx` | Create | The `?` overlay listing every binding |
| `src/components/ShortcutSheet.test.tsx` | Create | Its tests |
| `src/components/AppShell.tsx` | Modify | Skip link, labelled rail, focus-on-route-change, sheet mount |
| `src/components/ContextBar.tsx` | Modify | The Next line; poll freshness |
| `src/components/ContextBar.test.tsx` | Create | Its tests |
| `src/domain/blocking.ts` | Modify | `where` parameter so the line reads correctly inside a project |
| `src/hooks/useProject.ts` | Modify | Expose `lastUpdated` and `polling` |
| `src/routes/Interview.tsx` | Modify | Stick-to-bottom, ⌘↵, dragover, dismissible error, live regions, copy |
| `src/components/AgentPanel.tsx` | Modify | Stick-to-bottom, live regions, copy |
| `src/routes/stages/Matrix.tsx` | Modify | Scroll-edge affordance + column count |
| `src/routes/Projects.tsx` | Modify | Copy |
| `src/styles/base.css` | Modify | Skip link, rail labels, ctxbar next line, 1400px breakpoint |
| `src/styles/craft.css` | Modify | Matrix scroll-edge shadow |

---

## Task 1: Skip link and focus on route change

Five rail tabs and the finder stand between a keyboard user and the page. Nothing moves focus when
the route changes, so a keyboard or screen-reader user who follows a link stays parked in the nav.

**Files:**
- Modify: `src/components/AppShell.tsx`
- Modify: `src/styles/base.css`
- Test: `src/components/AppShell.test.tsx`

- [ ] **Step 1: Write the failing tests**

Append to `src/components/AppShell.test.tsx`, inside a new `describe`:

```tsx
describe('AppShell keyboard access', () => {
  it('puts a skip link first in the tab order, pointing at main', () => {
    shell();
    const skip = screen.getByRole('link', { name: /skip to content/i });
    expect(skip).toHaveAttribute('href', '#main');
    // First in the DOM is first in the tab order — that is the whole point of a skip link.
    const links = screen.getAllByRole('link');
    expect(links[0]).toBe(skip);
  });

  it('gives main a focus target so a route change can land there', () => {
    shell();
    const main = document.querySelector('main')!;
    expect(main).toHaveAttribute('id', 'main');
    expect(main).toHaveAttribute('tabindex', '-1');
  });
});
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `npm test -- src/components/AppShell.test.tsx`
Expected: FAIL — `Unable to find an accessible element with the role "link" and name /skip to content/i`.

- [ ] **Step 3: Implement**

In `src/components/AppShell.tsx`, add imports:

```tsx
import { useEffect, useRef } from 'react';
```

Inside `AppShell`, above the `return`, add the focus-on-route-change effect:

```tsx
  const main = useRef<HTMLElement | null>(null);
  const firstRender = useRef(true);

  /**
   * Move focus into the page when the route changes.
   *
   * A single-page app replaces the content without telling anyone: a keyboard user who follows a
   * rail link is still in the rail, and a screen reader announces nothing at all. Focusing <main>
   * makes the new screen the next thing read and the next thing tabbed from.
   *
   * `preventScroll` matters here — the masthead, the rail and the context bar are all sticky, and
   * a browser scrolling a focused element into view would fight all three on every navigation.
   * Skipped on first render, where the browser's own initial focus is already correct.
   */
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    main.current?.focus({ preventScroll: true });
  }, [pathname]);
```

Then in the returned JSX, add the skip link as the very first element inside the fragment, before
`<header className="masthead">`:

```tsx
      {/* First in the DOM, visible only on focus. Five rail tabs plus the finder otherwise stand
          between the keyboard and the page. */}
      <a className="skip" href="#main">
        Skip to content
      </a>
```

And change the `<main>` element to:

```tsx
        <main className="wrap" data-frame={frame} id="main" tabIndex={-1} ref={main}>
          <Outlet />
        </main>
```

- [ ] **Step 4: Add the skip-link styles**

In `src/styles/base.css`, immediately before the `.masthead {` rule (around line 314), add:

```css
/* ---- skip link -------------------------------------------------------------

   Off-screen until focused, then it lands over the masthead as an ordinary
   control. It gets the standard focus ring, not a bespoke one — a skip link is
   the first thing a keyboard user ever sees, and it should look like the rest of
   the instrument. */

.skip {
  position: absolute;
  left: var(--s3);
  top: -60px;
  z-index: 40; /* over the masthead (25) */
  padding: var(--s2) var(--s3);
  border-radius: var(--r2);
  background: var(--surface-0);
  border: var(--hair) solid var(--border-strong);
  font-size: var(--t-small);
  color: var(--text-primary);
  transition: top var(--dur-2) var(--ease-out);
}
.skip:focus {
  top: var(--s2);
  text-decoration: none;
  box-shadow: var(--focus-ring);
}

/* `main` takes focus programmatically on route change; it is not a control and
   must not draw a focus ring for it. */
.wrap:focus {
  outline: none;
}
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `npm test -- src/components/AppShell.test.tsx`
Expected: PASS, all tests in the file.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/AppShell.tsx src/smx-web/src/components/AppShell.test.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): skip link and focus handoff on route change"
```

---

## Task 2: Label the navigation rail

The rail is five icons with hover-only `title` attributes. The operator chip at the rail's foot
already uses `icon + 10px uppercase micro-label`; the nav items adopt that same treatment, so the
rail becomes internally consistent rather than merely more legible.

**Files:**
- Modify: `src/components/AppShell.tsx`
- Modify: `src/styles/base.css`, `src/styles/tokens.css`
- Test: `src/components/AppShell.test.tsx`

- [ ] **Step 1: Write the failing test**

Append inside the `AppShell keyboard access` describe (or a new `AppShell rail` describe) in
`src/components/AppShell.test.tsx`:

```tsx
describe('AppShell rail', () => {
  /**
   * The rail is the only way to reach four of the five top-level surfaces. Icon-plus-hover-title
   * means the operator must either already know the icons or hunt with the mouse; a visible label
   * is what makes a destination findable the first time.
   */
  it('renders a visible label beside each destination icon', () => {
    shell();
    for (const label of ['Projects', 'Library', 'Learned', 'MSDS', 'Docs']) {
      expect(screen.getByText(label, { selector: '.rail__label' })).toBeInTheDocument();
    }
  });

  /** The full name stays the accessible name; the visible label is the short form. */
  it('keeps the full accessible name on every rail link', () => {
    shell();
    for (const tab of ['Projects', 'Marker library', 'Learned conclusions', 'MSDS registry', 'Documents']) {
      expect(screen.getByRole('link', { name: tab })).toBeInTheDocument();
    }
  });
});
```

- [ ] **Step 2: Run it and verify it fails**

Run: `npm test -- src/components/AppShell.test.tsx`
Expected: FAIL — `Unable to find an element with the text: Library`.

- [ ] **Step 3: Implement**

In `src/components/AppShell.tsx`, replace the `TABS` constant with:

```tsx
/**
 * `label` is the accessible name — the full name of the destination.
 * `short` is what fits under a 76px rail icon. Both exist because truncating the accessible
 * name to fit a layout would make the screen reader read the layout's problem aloud.
 */
const TABS = [
  { to: '/', label: 'Projects', short: 'Projects', end: true, icon: 'ti-layout-grid' },
  { to: '/marker-library', label: 'Marker library', short: 'Library', icon: 'ti-books' },
  { to: '/learned-conclusions', label: 'Learned conclusions', short: 'Learned', icon: 'ti-bulb' },
  { to: '/msds-registry', label: 'MSDS registry', short: 'MSDS', icon: 'ti-clipboard-list' },
  // Not `end`: /docs/:id is the same surface, and the tab should stay lit while reading one.
  { to: '/docs', label: 'Documents', short: 'Docs', icon: 'ti-files' },
];
```

Replace the `NavLink` body in the rail with:

```tsx
              <NavLink
                key={t.to}
                to={t.to}
                end={t.end}
                aria-label={t.label}
                title={t.label}
                className={({ isActive }) => (isActive ? 'rail__item on' : 'rail__item')}
              >
                <i className={`ti ${t.icon}`} aria-hidden="true" />
                <span className="rail__label">{t.short}</span>
              </NavLink>
```

- [ ] **Step 4: Widen the rail and style the labels**

In `src/styles/tokens.css`, change the rail width:

```css
  --rail-w: 76px; /* vertical nav rail — icon over a micro-label, matching the operator chip */
```

In `src/styles/base.css`, replace the `.rail__item` rule (around line 436) with:

```css
.rail__item {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 3px;
  width: 64px;
  padding: 6px 0 5px;
  border-radius: var(--r2);
  color: var(--text-secondary);
  font-size: 20px;
  line-height: 1;
}

/* The same treatment the operator chip at the foot of this rail already uses. Making the
   nav items match it is why the rail reads as one component instead of two. */
.rail__label {
  font-size: var(--t-micro);
  text-transform: uppercase;
  letter-spacing: var(--track-eyebrow);
  line-height: 1.1;
}
```

Leave `.rail__item:hover` and `.rail__item.on` exactly as they are.

- [ ] **Step 5: Run the tests and verify they pass**

Run: `npm test -- src/components/AppShell.test.tsx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/AppShell.tsx src/smx-web/src/components/AppShell.test.tsx src/smx-web/src/styles/base.css src/smx-web/src/styles/tokens.css
git commit -m "feat(web): label the navigation rail"
```

---

## Task 3: The Next line in the context bar

`whatsBlocking()` produces the single most useful sentence the app can build. It renders on the
dashboard card and nowhere inside the project, where the context bar says only "in progress".

**Files:**
- Modify: `src/domain/blocking.ts`
- Modify: `src/components/ContextBar.tsx`
- Test: `src/domain/blocking.test.ts`, `src/components/ContextBar.test.tsx` (create)

- [ ] **Step 1: Write the failing test for the `where` parameter**

Append to `src/domain/blocking.test.ts`:

```ts
describe('whatsBlocking — where', () => {
  /**
   * The same fact, addressed from two places. On the dashboard the operator has not opened the
   * project yet, so the line tells them to; inside the project they are already there, and
   * "open it and press Start Processing" would send them somewhere they are standing.
   */
  it('drops the "open it" instruction when already inside the project', () => {
    const project = projectWith({ intake: { status: 'awaiting-confirmation', attempts: 1 } });

    expect(whatsBlocking(project)!.text).toMatch(/open it and press Start Processing/i);
    expect(whatsBlocking(project, undefined, 0, 'project')!.text).toMatch(
      /^Not started — press Start Processing/i,
    );
  });
});
```

Use whatever project-building helper `blocking.test.ts` already defines; if it has none, add:

```ts
function projectWith(stages: ProjectSummary['stages']): ProjectSummary {
  return {
    projectId: 'proj-test',
    client: 'Acme',
    product: 'Bottle',
    createdAt: '2026-07-01T00:00:00Z',
    stages,
  };
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `npm test -- src/domain/blocking.test.ts`
Expected: FAIL — `Expected 4 arguments, but got 4` is not the error; expect the assertion to fail
because the second call returns the same "open it and press Start Processing" string.

- [ ] **Step 3: Implement the `where` parameter**

In `src/domain/blocking.ts`, change the signature:

```ts
/** Where the line will be read. Inside a project, an instruction to open the project is noise. */
export type BlockingWhere = 'list' | 'project';

export function whatsBlocking(
  project: ProjectSummary,
  matrix?: MatrixSummary,
  unopenedFlagged = 0,
  where: BlockingWhere = 'list',
): Blocking | null {
```

And change rule 4 (the `awaiting-confirmation` branch) to:

```ts
  if (entries.some(([, s]) => s.status === 'awaiting-confirmation')) {
    return {
      tone: 'warning',
      icon: 'ti-player-play',
      text:
        where === 'project'
          ? 'Not started — press Start Processing to dispatch the agents'
          : 'Created but not started — open it and press Start Processing to dispatch the agents',
    };
  }
```

- [ ] **Step 4: Run it and verify it passes**

Run: `npm test -- src/domain/blocking.test.ts`
Expected: PASS.

- [ ] **Step 5: Write the failing ContextBar test**

Create `src/components/ContextBar.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { ContextBar } from './ContextBar';
import type { ProjectSummary } from '../api/types';

function project(stages: ProjectSummary['stages']): ProjectSummary {
  return {
    projectId: 'proj-test',
    client: 'Acme',
    product: 'Bottle',
    createdAt: '2026-07-01T00:00:00Z',
    stages,
  };
}

function bar(p: ProjectSummary) {
  return render(
    <MemoryRouter>
      <ContextBar project={p} />
    </MemoryRouter>,
  );
}

describe('ContextBar next line', () => {
  /**
   * The load-bearing one. A project runs in bursts across days, and this is the sentence the
   * operator reads on re-entry. "in progress" told them a stage was moving but never which one
   * or who it was stopped on — so the one thing they came back to find out was the one thing the
   * status bar would not say.
   */
  it('names who the project is parked on', () => {
    bar(project({ regulatory: { status: 'awaiting-RE', attempts: 1 } }));
    expect(screen.getByText(/awaiting the Regulatory Expert's determination/i)).toBeInTheDocument();
  });

  it('renders a halted agent verbatim, not paraphrased', () => {
    bar(project({ discovery: { status: 'failed', attempts: 2, error: 'search_web timed out' } }));
    expect(screen.getByText(/Discovery halted/i)).toBeInTheDocument();
    expect(screen.getByText('search_web timed out')).toBeInTheDocument();
  });

  it('says all stages are settled when nothing is blocking', () => {
    bar(project({ intake: { status: 'done', attempts: 1 } }));
    expect(screen.getByText(/all stages settled/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 6: Run it and verify it fails**

Run: `npm test -- src/components/ContextBar.test.tsx`
Expected: FAIL — the awaiting-RE text is not in the document; the bar renders "in progress".

- [ ] **Step 7: Implement the Next line**

Replace the whole of `src/components/ContextBar.tsx` with:

```tsx
import { Link } from 'react-router-dom';
import type { ProjectSummary } from '../api/types';
import { whatsBlocking } from '../domain/blocking';
import { StageSpine } from './StageSpine';
import { Data } from './ui/Data';

/**
 * The project context bar — sticky, pinned directly beneath the masthead.
 *
 * The masthead is a compact brand/utility top bar (logo, finder, corpus stamp); this is
 * the per-project status board. Thirty rows into a compatibility matrix you need to know
 * which project you are in and what it is waiting on — so this pins where it stays useful.
 *
 * z-index must clear the matrix's own sticky `thead` (craft.css puts it at 2, and its
 * corner cell at 3). They do not compete for scroll — the table has its own container —
 * but they do compete for paint order.
 */
export function ContextBar({ project }: { project: ProjectSummary }) {
  /**
   * The needle.
   *
   * A project runs in bursts across days, parking in an explicit `awaiting <X>` state each time
   * it needs a human. `whatsBlocking` folds the record into one prioritised sentence naming the
   * wait and whom it is on — and it used to render only on the dashboard card, so the operator
   * lost it the moment they opened the project. The dashboard calls the SAME function, so the two
   * surfaces cannot drift apart.
   *
   * No matrix summary is passed: this bar is on every stage screen, and fetching the matrix to
   * render a status line would make the whole workspace wait on it. The matrix-derived rules
   * (inconsistent, uncited, unopened-flagged) stay the dashboard's job, where the summary is
   * already loaded.
   */
  const blocking = whatsBlocking(project, undefined, 0, 'project');
  const tone = blocking ? blocking.tone : 'success';

  return (
    <div className="ctxbar">
      <div className="ctxbar__row">
        <Link to="/" className="ctxbar__back" title="All projects">
          <i className="ti ti-chevron-left" aria-hidden="true" />
          Projects
        </Link>

        <span className="ctxbar__sep" aria-hidden="true" />

        <span className="ctxbar__product">{project.product}</span>
        <span className="ctxbar__meta">
          client {project.client} · <Data kind="id">{project.projectId}</Data>
        </span>

        {/* Never a celebration — a settled project is quiet (see the motion policy in craft.css). */}
        <span className="ctxbar__next" data-tone={tone}>
          <i
            className={`ti ${blocking ? blocking.icon : 'ti-check'}`}
            aria-hidden="true"
            data-running={blocking?.icon === 'ti-loader' ? '' : undefined}
          />
          <span>
            {blocking ? blocking.text : 'All stages settled'}
            {blocking?.detail && <span className="ctxbar__detail data">{blocking.detail}</span>}
          </span>
        </span>
      </div>

      <StageSpine project={project} />
    </div>
  );
}
```

- [ ] **Step 8: Style it**

In `src/styles/base.css`, replace the `.ctxbar__status` rule with:

```css
/* The Next line. Tone is set by data-tone, from the record — never by the screen. */
.ctxbar__next {
  margin-left: auto;
  display: inline-flex;
  align-items: flex-start;
  gap: 5px;
  max-width: 46ch;
  font-size: var(--t-small);
  line-height: var(--lh-tight);
  text-align: right;
  justify-content: flex-end;
}
.ctxbar__next[data-tone='danger'] {
  color: var(--text-danger);
}
.ctxbar__next[data-tone='warning'] {
  color: var(--text-warning);
}
.ctxbar__next[data-tone='accent'] {
  color: var(--text-accent);
}
.ctxbar__next[data-tone='muted'] {
  color: var(--text-muted);
}
.ctxbar__next[data-tone='success'] {
  color: var(--text-success);
}

/* The agent's own error string, verbatim and monospaced — it is machine output, not prose. */
.ctxbar__detail {
  display: block;
  font-size: var(--t-tiny);
  opacity: 0.85;
  margin-top: 2px;
}
```

- [ ] **Step 9: Run the tests and verify they pass**

Run: `npm test -- src/components/ContextBar.test.tsx src/domain/blocking.test.ts`
Expected: PASS.

- [ ] **Step 10: Check nothing else asserted the old status text**

Run: `npm test`
Expected: PASS. If `ProjectLayout.test.tsx` asserts on `in progress`, update that assertion to the
new sentence — the fact under test is unchanged.

- [ ] **Step 11: Commit**

```bash
git add src/smx-web/src/domain/blocking.ts src/smx-web/src/domain/blocking.test.ts src/smx-web/src/components/ContextBar.tsx src/smx-web/src/components/ContextBar.test.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): surface what a project is blocked on inside the project"
```

---

## Task 4: Poll freshness

While a stage runs, `useProject` re-polls every 3s and the screen shows nothing to say so. A stalled
tab and a live one are identical.

**Files:**
- Create: `src/domain/relativeTime.ts`, `src/domain/relativeTime.test.ts`
- Modify: `src/hooks/useProject.ts`, `src/components/ContextBar.tsx`, `src/routes/ProjectLayout.tsx`, `src/styles/base.css`

- [ ] **Step 1: Write the failing test for `relativeTime`**

Create `src/domain/relativeTime.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { agoLabel } from './relativeTime';

describe('agoLabel', () => {
  it('reads in seconds under a minute', () => {
    expect(agoLabel(0)).toBe('just now');
    expect(agoLabel(12_000)).toBe('12s ago');
    expect(agoLabel(59_000)).toBe('59s ago');
  });

  it('reads in minutes under an hour', () => {
    expect(agoLabel(60_000)).toBe('1m ago');
    expect(agoLabel(3_599_000)).toBe('59m ago');
  });

  it('reads in hours beyond that', () => {
    expect(agoLabel(3_600_000)).toBe('1h ago');
    expect(agoLabel(7_200_000)).toBe('2h ago');
  });

  /** A clock that has drifted backwards must not print "-3s ago". */
  it('clamps a negative elapsed time to just now', () => {
    expect(agoLabel(-5_000)).toBe('just now');
  });
});
```

- [ ] **Step 2: Run it and verify it fails**

Run: `npm test -- src/domain/relativeTime.test.ts`
Expected: FAIL — `Failed to resolve import "./relativeTime"`.

- [ ] **Step 3: Implement**

Create `src/domain/relativeTime.ts`:

```ts
/**
 * How long ago, in the shortest form that is still unambiguous.
 *
 * Takes elapsed milliseconds rather than a timestamp so it is a pure function of its argument —
 * a helper that read the clock itself could not be tested without freezing time.
 */
export function agoLabel(elapsedMs: number): string {
  const s = Math.floor(elapsedMs / 1000);
  if (s < 1) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  return `${Math.floor(m / 60)}h ago`;
}
```

- [ ] **Step 4: Run it and verify it passes**

Run: `npm test -- src/domain/relativeTime.test.ts`
Expected: PASS, 4 tests.

- [ ] **Step 5: Expose freshness from `useProject`**

In `src/hooks/useProject.ts`, change the return type and body. Replace the whole
`export function useProject` declaration and body with:

```ts
export function useProject(projectId: string | undefined): {
  state: ProjectState;
  refresh: () => void;
  /** Epoch ms of the last successful read, or null before the first one. */
  readAt: number | null;
  /** True while the loop is still scheduled — i.e. a stage is running. */
  polling: boolean;
} {
  const [state, setState] = useState<ProjectState>({ kind: 'loading' });
  const [readAt, setReadAt] = useState<number | null>(null);
  const [polling, setPolling] = useState(false);
  const [nonce, setNonce] = useState(0);
  const timer = useRef<number>();

  const load = useCallback(async (id: string) => {
    try {
      const result = await getProject(id);
      if (result === NotFound) {
        setState({ kind: 'missing' });
        return false;
      }
      setState({ kind: 'ready', project: result });
      setReadAt(Date.now());
      return anyRunning(result.stages);
    } catch (err) {
      setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
      return false;
    }
  }, []);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;

    const tick = async () => {
      const keepPolling = await load(projectId);
      if (cancelled) return;
      setPolling(keepPolling);
      if (!keepPolling) return;
      timer.current = window.setTimeout(tick, POLL_MS);
    };
    void tick();

    return () => {
      cancelled = true;
      window.clearTimeout(timer.current);
    };
  }, [projectId, load, nonce]);

  const refresh = useCallback(() => setNonce((n) => n + 1), []);
  return { state, refresh, readAt, polling };
}
```

Keep the existing doc comment above the function untouched.

- [ ] **Step 6: Pass it through `ProjectLayout` into `ContextBar`**

In `src/routes/ProjectLayout.tsx`, change:

```tsx
  const { state, refresh } = useProject(projectId);
```

to:

```tsx
  const { state, refresh, readAt, polling } = useProject(projectId);
```

and change the `<ContextBar ... />` line to:

```tsx
      <ContextBar project={state.project} readAt={readAt} polling={polling} />
```

- [ ] **Step 7: Render it in `ContextBar`**

In `src/components/ContextBar.tsx`, add to the imports:

```tsx
import { useEffect, useState } from 'react';
import { agoLabel } from '../domain/relativeTime';
```

Change the props:

```tsx
export function ContextBar({
  project,
  readAt = null,
  polling = false,
}: {
  project: ProjectSummary;
  readAt?: number | null;
  polling?: boolean;
}) {
```

Add above the `return`:

```tsx
  /**
   * Re-render once a second so the label ages while the operator watches.
   *
   * Only while polling: a settled project's last-read time is not interesting, and a timer that
   * ticks for the life of a tab on a screen nobody is watching is just heat.
   */
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!polling) return;
    const id = window.setInterval(() => setTick((n) => n + 1), 1000);
    return () => window.clearInterval(id);
  }, [polling]);
```

And add, immediately after the closing `</span>` of `.ctxbar__next` and before `<StageSpine`:

```tsx
      </div>

      {/* A live poll and a frozen tab look identical otherwise. `role="status"` so the fact
          reaches a screen reader too, and `polite` so it never interrupts. */}
      {polling && readAt !== null && (
        <div className="ctxbar__poll" role="status" aria-live="polite">
          <span className="ctxbar__pulse" aria-hidden="true" />
          Watching the record · updated {agoLabel(Date.now() - readAt)}
        </div>
      )}
```

(Note the `</div>` shown above closes `.ctxbar__row` — do not add a second one.)

- [ ] **Step 8: Style it**

Append to the context-bar block in `src/styles/base.css`:

```css
.ctxbar__poll {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: var(--s2);
  font-size: var(--t-tiny);
  color: var(--text-muted);
}

/* A slow pulse, not a spinner. The record is being watched, not worked on — and a spinner
   here would claim activity the operator is not waiting for. */
.ctxbar__pulse {
  width: 6px;
  height: 6px;
  border-radius: var(--r-pill);
  background: var(--text-accent);
  animation: ctxpulse 2s var(--ease-inout) infinite;
}
@keyframes ctxpulse {
  0%,
  100% {
    opacity: 0.25;
  }
  50% {
    opacity: 1;
  }
}
@media (prefers-reduced-motion: reduce) {
  .ctxbar__pulse {
    animation: none;
    opacity: 0.8;
  }
}
```

- [ ] **Step 9: Write and run the test**

Append to `src/components/ContextBar.test.tsx`:

```tsx
describe('ContextBar poll freshness', () => {
  it('says it is watching the record while polling', () => {
    bar2(project({ discovery: { status: 'running', attempts: 1 } }), Date.now(), true);
    const status = screen.getByRole('status');
    expect(status).toHaveTextContent(/watching the record/i);
    expect(status).toHaveAttribute('aria-live', 'polite');
  });

  /** A settled project is not being watched, and claiming otherwise would be a lie about the loop. */
  it('says nothing when the poll loop has stopped', () => {
    bar2(project({ intake: { status: 'done', attempts: 1 } }), Date.now(), false);
    expect(screen.queryByRole('status')).toBeNull();
  });
});
```

Add the helper beside `bar`:

```tsx
function bar2(p: ProjectSummary, readAt: number | null, polling: boolean) {
  return render(
    <MemoryRouter>
      <ContextBar project={p} readAt={readAt} polling={polling} />
    </MemoryRouter>,
  );
}
```

Run: `npm test -- src/components/ContextBar.test.tsx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/smx-web/src/domain/relativeTime.ts src/smx-web/src/domain/relativeTime.test.ts src/smx-web/src/hooks/useProject.ts src/smx-web/src/routes/ProjectLayout.tsx src/smx-web/src/components/ContextBar.tsx src/smx-web/src/components/ContextBar.test.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): show that the record is being watched, and how fresh it is"
```

---

## Task 5: Stick-to-bottom conversations

Both conversation surfaces grow downward and neither scrolls. The rule is: follow new content only
while the reader is already at the bottom; the moment they scroll up, stop following.

**Files:**
- Create: `src/hooks/useStickToBottom.ts`, `src/hooks/useStickToBottom.test.ts`
- Modify: `src/components/AgentPanel.tsx`, `src/routes/Interview.tsx`

- [ ] **Step 1: Write the failing test**

Create `src/hooks/useStickToBottom.test.ts`:

```ts
import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useStickToBottom } from './useStickToBottom';

/**
 * jsdom reports 0 for every layout property, so the element is faked. The behaviour under test is
 * arithmetic and a decision, not real layout: "was the reader at the bottom, and did we move?"
 */
function fakeScroller(scrollTop: number, clientHeight = 100, scrollHeight = 500) {
  return {
    scrollTop,
    clientHeight,
    scrollHeight,
  } as HTMLDivElement;
}

describe('useStickToBottom', () => {
  it('scrolls to the bottom when the reader is already at the bottom', () => {
    const el = fakeScroller(400); // 400 + 100 === 500 — pinned
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.current = el;
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(500);
  });

  /**
   * The load-bearing one. The operator scrolls up to re-read what an agent said three turns ago;
   * a new turn arriving must not yank them back down mid-sentence.
   */
  it('leaves the reader alone when they have scrolled up', () => {
    const el = fakeScroller(120);
    const { result, rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    result.current.current = el;
    rerender({ dep: 2 });
    expect(el.scrollTop).toBe(120);
  });

  it('does nothing when the ref is empty', () => {
    const { rerender } = renderHook(({ dep }) => useStickToBottom<HTMLDivElement>([dep]), {
      initialProps: { dep: 1 },
    });
    expect(() => rerender({ dep: 2 })).not.toThrow();
  });
});
```

- [ ] **Step 2: Run it and verify it fails**

Run: `npm test -- src/hooks/useStickToBottom.test.ts`
Expected: FAIL — `Failed to resolve import "./useStickToBottom"`.

- [ ] **Step 3: Implement**

Create `src/hooks/useStickToBottom.ts`:

```ts
import { useLayoutEffect, useRef } from 'react';

/** Within this many px of the bottom still counts as "at the bottom" — sub-pixel layout, zoom. */
const THRESHOLD = 48;

/**
 * Follow a growing list, but only for a reader who is already at the bottom of it.
 *
 * Both conversation surfaces in this app grow downward while the operator watches: the intake
 * interview streams a reply token by token, and the docked stage agent appends a turn whenever the
 * poll loop finds one. Neither used to scroll at all, so the newest content — the thing they are
 * waiting for — arrived below the fold every time.
 *
 * Unconditional auto-scroll is the other failure. The operator scrolls back to re-read what the
 * agent cited two turns ago; snapping them to the bottom mid-sentence loses their place in exactly
 * the material they went back for. So the reader's own position wins whenever they have one.
 *
 * `useLayoutEffect`, not `useEffect`: the measurement has to happen after the DOM grows but before
 * the browser paints, or the reader sees one frame of the old scroll position.
 */
export function useStickToBottom<T extends HTMLElement>(deps: React.DependencyList) {
  const ref = useRef<T | null>(null);
  const pinned = useRef(true);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    // Re-measure rather than trusting the flag alone: `onScroll` does not fire when content is
    // appended, so a reader who scrolled up would otherwise keep whatever state the last scroll
    // event left behind. The element's own geometry is the authority.
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= THRESHOLD;
    if (pinned.current && atBottom) el.scrollTop = el.scrollHeight;
    pinned.current = atBottom;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  /** Wire to the scroller's `onScroll`: re-arms following when the reader returns to the bottom. */
  const onScroll = () => {
    const el = ref.current;
    if (!el) return;
    pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight <= THRESHOLD;
  };

  return Object.assign(ref, { onScroll }) as React.MutableRefObject<T | null> & {
    onScroll: () => void;
  };
}
```

- [ ] **Step 4: Run it and verify it passes**

Run: `npm test -- src/hooks/useStickToBottom.test.ts`
Expected: PASS, 3 tests. If the second test fails, the `atBottom` guard is not being applied —
recheck Step 3's replacement body.

- [ ] **Step 5: Use it in the agent dock**

In `src/components/AgentPanel.tsx`, add to the imports:

```tsx
import { useStickToBottom } from '../hooks/useStickToBottom';
```

Inside `LiveChat`, after the `pending` const, add:

```tsx
  const scroller = useStickToBottom<HTMLDivElement>([turns.length, pending]);
```

Change the transcript container from:

```tsx
      <div style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
```

to:

```tsx
      <div
        ref={scroller}
        onScroll={scroller.onScroll}
        style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}
      >
```

- [ ] **Step 6: Use it in the interview**

In `src/routes/Interview.tsx`, add to the imports:

```tsx
import { useStickToBottom } from '../hooks/useStickToBottom';
```

Inside `Interview`, after the `fileInput` ref, add:

```tsx
  // `streaming` is a dep so the view follows the reply as it arrives token by token, not only
  // once the finished turn lands.
  const scroller = useStickToBottom<HTMLDivElement>([session?.turns.length, streaming, sending]);
```

Change the transcript container from:

```tsx
          <div className="region" style={{ marginBottom: 12 }}>
```

to:

```tsx
          <div
            className="region convo"
            ref={scroller}
            onScroll={scroller.onScroll}
            style={{ marginBottom: 12 }}
          >
```

- [ ] **Step 7: Give the interview transcript a height to scroll within**

In `src/styles/base.css`, at the end of the file, add:

```css
/* The interview transcript scrolls inside itself rather than growing the page. Without a height
   there is no "bottom" to stick to — the composer would keep marching down the document, and a
   long interview would put the thing the operator is typing into off-screen. */
.convo {
  max-height: 52vh;
  overflow-y: auto;
}
```

- [ ] **Step 8: Run the suite**

Run: `npm test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/smx-web/src/hooks/useStickToBottom.ts src/smx-web/src/hooks/useStickToBottom.test.ts src/smx-web/src/components/AgentPanel.tsx src/smx-web/src/routes/Interview.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): conversations follow new turns without stealing the reader's place"
```

---

## Task 6: Interview composer — keyboard send, drop feedback, dismissible error

**Files:**
- Modify: `src/routes/Interview.tsx`, `src/styles/base.css`
- Test: `src/routes/Interview.test.tsx`

- [ ] **Step 1: Write the failing tests**

Append to `src/routes/Interview.test.tsx` (reuse whatever render helper and MSW/mocked-client setup
the file already establishes — do not invent a second one):

```tsx
describe('Interview composer', () => {
  it('sends on Ctrl+Enter and inserts a newline on plain Enter', async () => {
    const user = userEvent.setup();
    await renderInterview(); // the file's existing helper
    const box = await screen.findByLabelText(/message the interview agent/i);

    await user.click(box);
    await user.keyboard('first line{Enter}second line');
    expect(box).toHaveValue('first line\nsecond line');

    await user.keyboard('{Control>}{Enter}{/Control}');
    // Sending clears the draft — that is the observable fact, independent of transport.
    await waitFor(() => expect(box).toHaveValue(''));
  });

  it('shows a drop target while a file is over the composer', async () => {
    await renderInterview();
    const zone = await screen.findByTestId('interview-dropzone');
    expect(zone).toHaveAttribute('data-dragging', 'false');

    fireEvent.dragEnter(zone, { dataTransfer: { types: ['Files'] } });
    expect(zone).toHaveAttribute('data-dragging', 'true');
    expect(screen.getByText(/drop to hand it over/i)).toBeInTheDocument();

    fireEvent.dragLeave(zone);
    expect(zone).toHaveAttribute('data-dragging', 'false');
  });
});
```

Ensure the file imports `fireEvent`, `waitFor` and `userEvent`:

```tsx
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
```

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/routes/Interview.test.tsx`
Expected: FAIL — no element with test id `interview-dropzone`, and Ctrl+Enter does not clear the box.

- [ ] **Step 3: Implement**

In `src/routes/Interview.tsx`, add state beside the others:

```tsx
  const [dragging, setDragging] = useState(false);
```

Replace `onDrop` and add the drag handlers:

```tsx
  function onDrop(e: DragEvent<HTMLDivElement>) {
    e.preventDefault();
    setDragging(false);
    void attach(e.dataTransfer?.files ?? null);
  }

  // `dragenter`/`dragleave` fire for every child the pointer crosses, so the depth is counted
  // rather than toggled — otherwise the highlight flickers off as the cursor passes the textarea.
  const dragDepth = useRef(0);

  function onDragEnter(e: DragEvent<HTMLDivElement>) {
    e.preventDefault();
    dragDepth.current += 1;
    setDragging(true);
  }

  function onDragLeave() {
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDragging(false);
  }
```

Replace the composer's wrapping `<div className="region" ...>` with:

```tsx
          <div
            className="region dropzone"
            data-testid="interview-dropzone"
            data-dragging={dragging ? 'true' : 'false'}
            style={{ marginBottom: 12 }}
            onDrop={onDrop}
            onDragOver={(e) => e.preventDefault()}
            onDragEnter={onDragEnter}
            onDragLeave={onDragLeave}
          >
            {dragging && (
              <div className="dropzone__hint" aria-hidden="true">
                <i className="ti ti-file-download" /> Drop to hand it over
              </div>
            )}
```

Add the keyboard handler to the textarea:

```tsx
            <textarea
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                // ⌘/Ctrl+Enter sends; plain Enter is a newline. The operator is writing a brief
                // here, not a chat line — paragraphs are normal, and a bare Enter that fired off a
                // half-written thought would be the worse default.
                if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) {
                  e.preventDefault();
                  if (draft.trim() && !sending) void send(draft);
                }
              }}
              placeholder="Talk to the agent…"
              aria-label="Message the interview agent"
              rows={3}
              disabled={sending}
              style={{ width: '100%', resize: 'vertical' }}
            />
```

Change the Send button to advertise the binding:

```tsx
              <button
                className="btn primary"
                type="button"
                style={{ marginLeft: 'auto' }}
                disabled={sending || !draft.trim()}
                onClick={() => void send(draft)}
                title="Send (Ctrl/Cmd + Enter)"
              >
                Send
              </button>
```

Make the error dismissible — replace the error banner with:

```tsx
      {error && (
        <div className="banner danger" role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" />
          <div>{error}</div>
          {/* It never cleared itself, so one failed turn left a stale banner over the rest of the
              interview — and the operator could not tell whether it described the last thing they
              did or something from ten minutes ago. */}
          <button
            className="btn"
            type="button"
            style={{ marginLeft: 'auto' }}
            onClick={() => setError(null)}
          >
            Dismiss
          </button>
        </div>
      )}
```

Add `useRef` to the React import if it is not already there (it is — `fileInput` uses it).

- [ ] **Step 4: Style the drop zone**

Append to `src/styles/base.css`:

```css
/* The composer accepted a dropped file with no indication that it would. */
.dropzone {
  position: relative;
  transition: border-color var(--dur-2) var(--ease-out), background var(--dur-2) var(--ease-out);
}
.dropzone[data-dragging='true'] {
  border-color: var(--text-accent);
  background: var(--bg-accent);
}
.dropzone__hint {
  position: absolute;
  inset: 0;
  z-index: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--s2);
  border-radius: var(--r3);
  background: color-mix(in srgb, var(--bg-accent) 92%, transparent);
  color: var(--text-accent);
  font-size: var(--t-small);
  font-weight: var(--w-medium);
  pointer-events: none;
}
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `npm test -- src/routes/Interview.test.tsx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/routes/Interview.tsx src/smx-web/src/routes/Interview.test.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): keyboard send, real drop feedback and a dismissible error in the interview"
```

---

## Task 7: The shortcut sheet

Four bindings exist (⌘K, ⌘\, `f`, Esc) and nothing announces any of them.

**Files:**
- Create: `src/components/ShortcutSheet.tsx`, `src/components/ShortcutSheet.test.tsx`
- Modify: `src/components/AppShell.tsx`, `src/styles/base.css`

- [ ] **Step 1: Write the failing test**

Create `src/components/ShortcutSheet.test.tsx`:

```tsx
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ShortcutSheet } from './ShortcutSheet';

describe('ShortcutSheet', () => {
  it('opens on ? and closes on Escape', () => {
    render(<ShortcutSheet />);
    expect(screen.queryByRole('dialog')).toBeNull();

    fireEvent.keyDown(window, { key: '?' });
    expect(screen.getByRole('dialog', { name: /keyboard shortcuts/i })).toBeInTheDocument();

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  /**
   * A "?" typed into the finder, the agent composer or the interview is a question mark, not a
   * command. Swallowing it would make every text field in the app drop a character.
   */
  it('ignores ? while a text field has focus', () => {
    render(
      <>
        <input aria-label="a field" />
        <ShortcutSheet />
      </>,
    );
    const field = screen.getByLabelText('a field');
    field.focus();
    fireEvent.keyDown(field, { key: '?', bubbles: true });
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('lists every binding the app actually implements', () => {
    render(<ShortcutSheet />);
    fireEvent.keyDown(window, { key: '?' });
    for (const label of [/open the finder/i, /agent dock/i, /send/i, /flagged cell/i, /close/i]) {
      expect(screen.getByText(label)).toBeInTheDocument();
    }
  });
});
```

- [ ] **Step 2: Run it and verify it fails**

Run: `npm test -- src/components/ShortcutSheet.test.tsx`
Expected: FAIL — `Failed to resolve import "./ShortcutSheet"`.

- [ ] **Step 3: Implement**

Create `src/components/ShortcutSheet.tsx`:

```tsx
import { useEffect, useRef, useState } from 'react';

/**
 * Every binding this app implements, in one place, behind `?`.
 *
 * The bindings were all real and all invisible: ⌘K opens the finder (Finder.tsx), ⌘\ toggles the
 * agent dock (Dock.tsx), `f` jumps the matrix's flagged queue (Matrix.tsx). A shortcut nobody can
 * discover is a shortcut nobody uses, and this is a single-operator instrument they will drive
 * every working day.
 *
 * This list is written by hand rather than derived, so it can go stale. Adding a binding without
 * adding a row here is the bug; ShortcutSheet.test.tsx asserts the current set.
 */
const KEYS: { keys: string; what: string; where?: string }[] = [
  { keys: '⌘K / Ctrl K', what: 'Open the finder', where: 'anywhere' },
  { keys: '⌘\\ / Ctrl \\', what: 'Show or hide the agent dock', where: 'a project stage' },
  { keys: '⌘↵ / Ctrl ↵', what: 'Send the message', where: 'the interview' },
  { keys: 'F', what: 'Jump to the next flagged cell', where: 'the matrix' },
  { keys: '↑ ↓ ← →', what: 'Move between cells', where: 'the matrix' },
  { keys: '↵', what: 'Open the evidence for a cell', where: 'the matrix' },
  { keys: 'Esc', what: 'Close this, the finder, or a document', where: 'anywhere' },
  { keys: '?', what: 'Show this list', where: 'anywhere' },
];

/** True when the keystroke belongs to whatever the operator is typing into. */
function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

export function ShortcutSheet() {
  const [open, setOpen] = useState(false);
  const panel = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setOpen(false);
        return;
      }
      if (e.key !== '?' || e.metaKey || e.ctrlKey || e.altKey) return;
      if (isTyping(e.target)) return;
      e.preventDefault();
      setOpen((v) => !v);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Focus the panel so Escape reaches it and a screen reader announces the dialog; return focus
  // where it came from on close, or the operator's next keystroke goes nowhere.
  useEffect(() => {
    if (!open) return;
    const opener = document.activeElement as HTMLElement | null;
    panel.current?.focus();
    return () => opener?.focus?.();
  }, [open]);

  if (!open) return null;

  return (
    <div className="sheet">
      <div className="sheet__backdrop" onClick={() => setOpen(false)} />
      <div
        ref={panel}
        className="sheet__panel"
        role="dialog"
        aria-modal="true"
        aria-label="Keyboard shortcuts"
        tabIndex={-1}
      >
        <div className="sheet__head">
          <span className="sec__title">Keyboard shortcuts</span>
          <button type="button" className="btn" onClick={() => setOpen(false)}>
            Close
          </button>
        </div>
        <dl className="sheet__list">
          {KEYS.map((k) => (
            <div className="sheet__row" key={k.keys + k.what}>
              <dt>
                <kbd>{k.keys}</kbd>
              </dt>
              <dd>
                {k.what}
                {k.where && <span className="tiny muted"> · {k.where}</span>}
              </dd>
            </div>
          ))}
        </dl>
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Mount it and style it**

In `src/components/AppShell.tsx`, add the import:

```tsx
import { ShortcutSheet } from './ShortcutSheet';
```

and render it as the last child of the fragment, after `</div>` closing `.shell`:

```tsx
      <ShortcutSheet />
```

Append to `src/styles/base.css`:

```css
/* ---- shortcut sheet ---------------------------------------------------------
   A true overlay, so it is one of the few things in this app allowed a shadow. */

.sheet {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  align-items: center;
  justify-content: center;
}
.sheet__backdrop {
  position: absolute;
  inset: 0;
  background: hsl(var(--shadow-hue) / 0.28);
}
.sheet__panel {
  position: relative;
  width: min(520px, calc(100vw - 2 * var(--s5)));
  max-height: 78vh;
  overflow-y: auto;
  padding: var(--s5);
  border-radius: var(--r3);
  border: var(--hair) solid var(--border);
  background: var(--surface-0);
  box-shadow: var(--shadow-3);
}
.sheet__panel:focus {
  outline: none;
}
.sheet__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--s3);
  margin-bottom: var(--s4);
}
.sheet__list {
  margin: 0;
}
.sheet__row {
  display: grid;
  grid-template-columns: 130px minmax(0, 1fr);
  gap: var(--s3);
  align-items: baseline;
  padding: var(--s2) 0;
  border-top: var(--hair) solid var(--border);
  font-size: var(--t-body);
}
.sheet__row dt,
.sheet__row dd {
  margin: 0;
}
.sheet__row kbd {
  font-family: var(--font-mono);
  font-size: var(--t-tiny);
  color: var(--text-secondary);
  background: var(--surface-2);
  border: var(--hair) solid var(--border);
  border-radius: var(--r1);
  padding: 2px 6px;
  white-space: nowrap;
}
```

- [ ] **Step 5: Advertise it in the masthead**

In `src/components/AppShell.tsx`, inside `masthead__end`, before `<CorpusStamp />`:

```tsx
          <span className="masthead__hint" title="Press ? for keyboard shortcuts">
            <kbd>?</kbd>
          </span>
```

and append to `base.css`:

```css
.masthead__hint kbd {
  font-family: var(--font-mono);
  font-size: var(--t-tiny);
  color: var(--text-muted);
  border: var(--hair) solid var(--border);
  border-radius: var(--r1);
  padding: 1px 6px;
}
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `npm test -- src/components/ShortcutSheet.test.tsx src/components/AppShell.test.tsx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/smx-web/src/components/ShortcutSheet.tsx src/smx-web/src/components/ShortcutSheet.test.tsx src/smx-web/src/components/AppShell.tsx src/smx-web/src/styles/base.css
git commit -m "feat(web): a shortcut sheet behind ?"
```

---

## Task 8: Live regions for every async state

The app has no `aria-live` region anywhere. Every async state is silent to assistive technology.

**Files:**
- Modify: `src/routes/Interview.tsx`, `src/components/AgentPanel.tsx`
- Test: `src/routes/Interview.test.tsx`, a new `src/components/AgentPanel.test.tsx`

- [ ] **Step 1: Write the failing tests**

Append to `src/routes/Interview.test.tsx`:

```tsx
describe('Interview live regions', () => {
  /**
   * Without this, an operator using a screen reader gets no signal at all that they are waiting:
   * the send button empties, and then nothing happens audibly until the whole reply has landed.
   */
  it('announces that the agent is working, politely', async () => {
    const user = userEvent.setup();
    await renderInterview();
    const box = await screen.findByLabelText(/message the interview agent/i);
    await user.type(box, 'the client is Acme');
    await user.click(screen.getByRole('button', { name: /^send$/i }));

    const status = await screen.findByRole('status');
    expect(status).toHaveAttribute('aria-live', 'polite');
  });
});
```

Create `src/components/AgentPanel.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AgentPanel } from './AgentPanel';

describe('AgentPanel', () => {
  /** A stage with no backend agent states the fact plainly — it is not mocked, so it is not badged. */
  it('says plainly when a stage has no agent', () => {
    render(<AgentPanel projectId="proj-test" stageSlug="decision" stageLabel="Decision" />);
    expect(screen.getByText(/no agent on this stage/i)).toBeInTheDocument();
  });
});
```

If `decision` turns out to be chat-capable, use a slug for which `canChat()` in
`src/domain/stages.ts` returns false — read that file and pick one.

- [ ] **Step 2: Run them and verify they fail**

Run: `npm test -- src/routes/Interview.test.tsx src/components/AgentPanel.test.tsx`
Expected: FAIL — no element with role `status`; and "No agent for this stage" ≠ "No agent on this stage".

- [ ] **Step 3: Add the live regions in the interview**

In `src/routes/Interview.tsx`:

The "agent is thinking" line becomes:

```tsx
            {sending && streaming === null && (
              <div className="tiny muted" role="status" aria-live="polite">
                <i className="ti ti-loader" data-running="" aria-hidden="true" /> Working…
              </div>
            )}
```

The upload line becomes:

```tsx
              {uploading && (
                <span className="tiny muted" role="status" aria-live="polite">
                  Reading the file…
                </span>
              )}
```

The streamed reply gets a polite region so it is announced as it arrives:

```tsx
            {streaming !== null && (
              <div className="bub ba" aria-live="polite">
                {streaming}
              </div>
            )}
```

The coverage counter becomes:

```tsx
              <span className="tiny muted" role="status" aria-live="polite">
                {cov.covered} of {cov.total} covered
              </span>
```

- [ ] **Step 4: Add the live regions in the dock**

In `src/components/AgentPanel.tsx`, change the two waiting indicators:

```tsx
        {state.kind === 'loading' && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading…
          </div>
        )}
```

```tsx
        {pending && (
          <div className="tiny muted" role="status" aria-live="polite">
            <i className="ti ti-loader" data-running="" aria-hidden="true" /> Working…
          </div>
        )}
```

and the two error nodes get `role="alert"`:

```tsx
        {state.kind === 'error' && (
          <div className="tiny" style={{ color: 'var(--text-danger)' }} role="alert">
            <i className="ti ti-alert-triangle" aria-hidden="true" /> {state.message}
          </div>
        )}
```

```tsx
      {error && (
        <div className="tiny" style={{ color: 'var(--text-danger)', margin: '4px 0' }} role="alert">
          <i className="ti ti-alert-triangle" aria-hidden="true" /> {error}
        </div>
      )}
```

- [ ] **Step 5: Shorten the closed-panel copy (the Task 10 rule, applied here)**

Replace `ClosedPanel`'s body text:

```tsx
      <div
        className="tiny muted"
        style={{ marginTop: 'auto', marginBottom: 'auto', textAlign: 'center', padding: 12 }}
      >
        <i
          className="ti ti-message-off"
          aria-hidden="true"
          style={{ fontSize: 20, display: 'block', marginBottom: 6 }}
        />
        No agent on this stage.
      </div>
```

(The enumeration of which stages have agents is deleted: the stage spine already shows it, and the
sentence was three lines of the panel's whole height.)

- [ ] **Step 6: Run the tests and verify they pass**

Run: `npm test -- src/routes/Interview.test.tsx src/components/AgentPanel.test.tsx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/smx-web/src/routes/Interview.tsx src/smx-web/src/routes/Interview.test.tsx src/smx-web/src/components/AgentPanel.tsx src/smx-web/src/components/AgentPanel.test.tsx
git commit -m "feat(web): announce every async state to assistive technology"
```

---

## Task 9: Laptop breakpoint and matrix scroll-edge affordance

The matrix's scroll container gives no indication that component columns continue past its right
edge. A silently hidden component column in a verdict matrix is a correctness risk, not a cosmetic
one.

**Files:**
- Modify: `src/styles/base.css`, `src/styles/craft.css`, `src/routes/stages/Matrix.tsx`
- Test: `src/routes/stages/Matrix.test.tsx` (create if absent)

- [ ] **Step 1: Add the laptop breakpoint**

In `src/styles/base.css`, immediately before the existing `@media (max-width: 1100px)` block, add:

```css
/* A 1280×800 laptop lands here. The dock keeps its doctrine — always present, never zero — but
   360px of it against a compatibility matrix is the wrong split at this width. */
@media (max-width: 1400px) {
  :root {
    --dock-w: 300px;
  }
}
```

- [ ] **Step 2: Write the failing test for the column count**

`src/routes/stages/Matrix.test.tsx` does **not** exist — create it. `Matrix` fetches through
`getMatrix` from `../../api/client`, so mock that module. Follow the shape of the existing
`src/routes/stages/Decision.test.tsx` for anything this omits.

```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { MatrixDoc, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return { ...actual, getMatrix: vi.fn(), matrixXlsxUrl: () => '/x.xlsx' };
});

import { getMatrix } from '../../api/client';
import { Matrix } from './Matrix';

const project: ProjectSummary = {
  projectId: 'proj-test',
  client: 'Acme',
  product: 'Bottle',
  createdAt: '2026-07-01T00:00:00Z',
  stages: { matrix: { status: 'done', attempts: 1 } },
};

/**
 * A minimal two-substance × two-component document. Build it from the real `MatrixDoc` type so a
 * change to the record's shape fails this test at compile time rather than at runtime in a browser.
 */
const doc = {
  projectId: 'proj-test',
  generatedAt: '2026-07-20T00:00:00Z',
  columns: ['bottle', 'label'],
  rows: [
    { substance: 'Yttrium oxide', cas: '1314-36-9' },
    { substance: 'Cerium oxide', cas: '1306-38-3' },
  ],
  cells: [],
} as unknown as MatrixDoc;

beforeEach(() => {
  vi.mocked(getMatrix).mockResolvedValue(doc);
});

describe('Matrix', () => {
  /**
   * The matrix scrolls horizontally, and on a narrow screen the right-hand components leave the
   * viewport with nothing to say they exist. An operator who reads four of six component columns
   * and signs a gate has approved a marker against components they never saw. The count is the
   * cheap guarantee that the number of columns is stated, not inferred from what is visible.
   */
  it('states how many component columns the matrix has', async () => {
    render(
      <MemoryRouter>
        <Matrix project={project} refreshProject={() => {}} />
      </MemoryRouter>,
    );
    expect(await screen.findByText(/2 components/i)).toBeInTheDocument();
  });
});
```

If the real `MatrixDoc` requires fields this fixture omits, add them — read
`src/api/types.ts` and fill in the actual shape rather than widening the cast.

- [ ] **Step 3: Run it and verify it fails**

Run: `npm test -- src/routes/stages/Matrix.test.tsx`
Expected: FAIL — no matching text.

- [ ] **Step 4: Implement**

In `src/routes/stages/Matrix.tsx`, replace the scroll container:

```tsx
        <div style={{ overflowX: 'auto', maxHeight: '70vh', overflowY: 'auto' }}>
```

with:

```tsx
        <div className="mxscroll">
          <div className="mxscroll__count tiny muted">
            {m.rows.length} substance{m.rows.length === 1 ? '' : 's'} × {m.columns.length} component
            {m.columns.length === 1 ? '' : 's'}
          </div>
          <div className="mxscroll__pane">
```

and close both new `<div>`s after the `</table>`:

```tsx
          </div>
        </div>
```

Check the surrounding JSX in the file and match the existing indentation; the `<table>` element and
everything inside it is unchanged.

- [ ] **Step 5: Style the edge affordance**

Append to `src/styles/craft.css`:

```css
/* ---- matrix scroll edge ------------------------------------------------------

   The pane scrolls horizontally, and a component column that has slid out of view leaves no
   trace. In a verdict matrix that is not a styling problem: the operator signs a gate against
   what they read, and a column they never knew existed is a column they never read.

   `background-attachment: local` on the two inner gradients pins them to the CONTENT, so each
   shadow paints only while there is content on that side to hide. Scroll to the end and the
   shadow goes; scroll back and it returns. */

.mxscroll__pane {
  overflow-x: auto;
  overflow-y: auto;
  max-height: 70vh;
  background:
    linear-gradient(to right, var(--surface-0) 30%, transparent) left center / 24px 100% no-repeat,
    linear-gradient(to left, var(--surface-0) 30%, transparent) right center / 24px 100% no-repeat,
    radial-gradient(farthest-side at 0 50%, hsl(var(--shadow-hue) / 0.14), transparent) left center /
      12px 100% no-repeat,
    radial-gradient(farthest-side at 100% 50%, hsl(var(--shadow-hue) / 0.14), transparent) right
      center / 12px 100% no-repeat;
  background-attachment: local, local, scroll, scroll;
}

.mxscroll__count {
  margin-bottom: var(--s2);
}
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `npm test -- src/routes/stages/Matrix.test.tsx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/smx-web/src/styles/base.css src/smx-web/src/styles/craft.css src/smx-web/src/routes/stages/Matrix.tsx src/smx-web/src/routes/stages/Matrix.test.tsx
git commit -m "feat(web): laptop breakpoint and a scroll edge the matrix cannot hide a column behind"
```

---

## Task 10: The copy pass

**The rule:** screen copy states a fact or an action. Rationale moves to a `title`, a disclosure, or
stays in the code comment. **A factual claim is never deleted — only its explanation.**

The test for each edit: is the sentence I am cutting a *reason* or a *fact*? "These verdicts are
fixture data" is a fact and stays. "…because a fabricated verdict that renders identically to a real
one is precisely the failure this badge prevents" is a reason and moves to the comment.

**Files:**
- Modify: `src/routes/Projects.tsx`, `src/routes/Interview.tsx`
- Test: `src/routes/Projects.test.tsx`, `src/routes/Interview.test.tsx`

- [ ] **Step 1: Check what the existing tests assert**

Run: `npm test -- src/routes/Projects.test.tsx src/routes/Interview.test.tsx`
Expected: PASS (baseline). Then read both files and note every assertion that matches on prose.
Assertions on a *fact* ("Demo data", a blocker sentence, a mock badge) stay and must keep passing.
Assertions on an *explanation* get retargeted at the fact in Step 4.

- [ ] **Step 2: Edit `Projects.tsx`**

Empty state — replace the `body` prop:

```tsx
        body="Start one — the agent asks what it needs."
```

Empty state — replace the trailing explanation under the spine:

```tsx
          <p className="tiny muted" style={{ marginTop: 10 }}>
            Six stages are backed by the API. The rest render fixture data behind a mock badge.
          </p>
```

Error state — replace the `body` prop:

```tsx
        body={
          <>
            <span className="data">GET /projects</span> did not answer: {message}
          </>
        }
```

Stat hint — replace `hint="not reportable — no gate state in the record"` with:

```tsx
          hint="not reported"
```

(This matches `CorpusStamp`'s wording, so absence has one vocabulary across the app.)

Demo banner — replace the banner body with:

```tsx
            <div>
              <b>Demo data</b> — a fixture project, not a real record.
            </div>
```

- [ ] **Step 3: Edit `Interview.tsx`**

The cap — replace with:

```tsx
      <div className="cap">
        <b>New project</b>
        Tell the agent about the job. It asks the rest, and creates the project itself.
      </div>
```

The opening prompt — replace with:

```tsx
              <div className="tiny muted">
                Start with the client and the job. Drop any file you already have.
              </div>
```

The create-button hint — delete the `<span className="tiny muted">` explaining that creation is the
agent's tool. The blocker line beneath it already names what is missing. Add the reason as a `title`
on the button instead:

```tsx
            <button
              className="btn primary"
              type="button"
              title="The agent calls its own create_project tool — it needs the summary and the component breakdown first"
              disabled={blocker !== null || sending}
              onClick={() => void send('Everything looks right. Please create the project now.')}
            >
              Create the project
            </button>
```

Keep the comment above the button exactly as it is — that is where the reasoning belongs.

- [ ] **Step 4: Update any test that asserted on removed prose**

For each failing assertion from Step 1, retarget it at the surviving fact. Example — if a test
asserted the long empty-state sentence, assert the heading instead:

```tsx
expect(screen.getByText(/no projects yet/i)).toBeInTheDocument();
```

Do **not** weaken an assertion that guards a fact. If a test checks that the demo banner says
"Demo data", it must still check that.

- [ ] **Step 5: Run the whole suite**

Run: `npm test`
Expected: PASS.

- [ ] **Step 6: Sweep the remaining screens**

Apply the same rule, one file at a time, to: `src/routes/stages/Background.tsx`,
`src/routes/stages/Discovery.tsx`, `src/routes/stages/Regulatory.tsx`,
`src/routes/stages/Dosing.tsx`, `src/routes/stages/Cost.tsx`, `src/routes/stages/Decision.tsx`,
`src/routes/stages/Intake.tsx`, `src/routes/MarkerLibrary.tsx`, `src/routes/LearnedConclusions.tsx`,
`src/routes/MsdsRegistry.tsx`, `src/routes/Documents.tsx`.

For each file: read every user-facing string, and for each one longer than about 12 words ask
whether it states a fact/action or explains a reason. Cut reasons; keep facts. **Every `MockBadge`
and every sentence identifying data as fixture data stays.** Run `npm test` after each file.

- [ ] **Step 7: Commit**

```bash
git add -A src/smx-web/src
git commit -m "refactor(web): state facts on screen, keep the reasoning in the comments"
```

---

## Task 11: Final verification

- [ ] **Step 1: Full test suite**

Run: `npm test`
Expected: PASS, no skipped tests introduced by this work.

- [ ] **Step 2: Types and production build**

Run: `npm run build`
Expected: `tsc --noEmit` clean, then a successful `vite build`.

- [ ] **Step 3: Confirm the two hard rules held**

Run:

```bash
grep -rn "MockBadge" src/smx-web/src/routes src/smx-web/src/components | grep -v test
```

Expected: every screen that carried a badge before this work still carries one. Compare against
`git show main:src/smx-web/src` if unsure.

Run:

```bash
git diff main --stat -- src/smx-web/src/components/ui/Gate.tsx src/smx-web/src/components/DeterminationForm.tsx
```

Expected: **no changes**. No gate control was enabled by this work.

- [ ] **Step 4: Focus-ring audit**

The spec asks that `--focus-ring` actually reach every control. Two findings from the survey that
produced this plan, to re-verify now that the rail has changed:

1. `styles/craft.css:34` already applies `box-shadow: var(--focus-ring)` to
   `:where(a, button, input, select, textarea, summary, [tabindex]):focus-visible`, and its
   `outline: none` is paired with that replacement. This is correct and must stay correct — check
   nothing added in this branch sets `outline: none` or `outline: 0` **without** a replacement.
2. `--focus-ring` is `0 0 0 2px var(--page-bg), 0 0 0 4px var(--text-accent)` — the inner ring is
   the *page* background, but the rail's background is `--surface-0`. Tab through the rail and
   confirm the halo still reads cleanly against white; if it does not, add to `base.css`:

```css
/* The rail sits on --surface-0, so the focus halo's inner ring must match that, not the page. */
.rail__item:focus-visible {
  box-shadow: 0 0 0 2px var(--surface-0), 0 0 0 4px var(--text-accent);
}
```

`FileViewerOverlay` already captures the opener and restores focus on unmount
(`src/components/FileViewerOverlay.tsx:32-37`) — **no change needed there**; verify it still works
by opening a document from an MSDS Registry row and pressing Escape.

- [ ] **Step 5: Look at it**

Run: `npm run dev`, open `http://localhost:5173`, and check by hand:

1. Tab from the address bar → "Skip to content" appears first.
2. The rail reads PROJECTS / LIBRARY / LEARNED / MSDS / DOCS.
3. Press `?` → the sheet opens; Esc closes it; typing `?` in the finder does not open it.
4. Open a project → the context bar names what it is blocked on, not "in progress".
5. `/new` → type a paragraph with Enter, then Ctrl+Enter to send. Drag a file over the composer.
6. Narrow the window to 1280px → the dock is 300px and the matrix shows its scroll edge.

- [ ] **Step 6: Commit anything the pass turned up, then hand off**

```bash
git add -A && git commit -m "fix(web): usability pass follow-ups from manual review"
```

Then use `superpowers:finishing-a-development-branch` to decide how this branch integrates.
