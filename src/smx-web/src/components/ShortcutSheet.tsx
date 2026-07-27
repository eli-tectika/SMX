import { useEffect, useRef, useState } from 'react';

/**
 * Every binding this app implements, in one place, behind `?`.
 *
 * The bindings were all real and all invisible: ⌘K opened a finder nobody knew existed, `f`
 * walked the matrix's flagged queue, ⌘\ toggled the agent dock, and so on — each one shipped
 * with a tooltip at best, on the one control that happens to render it. A shortcut nobody can
 * discover is a shortcut nobody uses, and this is a single-operator instrument they will drive
 * every working day.
 *
 * This list is written by hand rather than derived from the listeners themselves, so it can go
 * stale. Adding a binding without adding a row here is the bug; ShortcutSheet.test.tsx asserts
 * the current set so that drift fails a test instead of just quietly happening.
 */
const KEYS: { keys: string; what: string; where?: string }[] = [
  { keys: '⌘K / Ctrl K', what: 'Open the finder', where: 'anywhere' },
  { keys: '⌘\\ / Ctrl \\', what: 'Show or hide the agent dock', where: 'a project stage' },
  { keys: '↵', what: 'Send the message', where: 'the interview' },
  { keys: '⌘↵ / Ctrl ↵ / ⇧↵', what: 'Start a new line', where: 'the interview' },
  { keys: 'F', what: 'Jump to the next flagged cell', where: 'the matrix' },
  { keys: '↑ ↓ ← →', what: 'Move between cells', where: 'the matrix' },
  { keys: '↵ / Space', what: 'Open the evidence for a cell', where: 'the matrix' },
  { keys: 'Esc', what: 'Close this, the finder, or a document viewer', where: 'anywhere' },
  { keys: '?', what: 'Show this list', where: 'anywhere' },
];

/**
 * True when the keystroke belongs to whatever the operator is typing into.
 *
 * A "?" is punctuation almost everywhere in this app's own text — the interview brief, the
 * agent composer, the finder's search box — before it is ever a command. Checking the tag
 * (rather than, say, only `<input>`) also covers the finder's search field and the interview's
 * textarea, and `isContentEditable` covers any future rich-text surface without needing to be
 * taught about it by name.
 */
function isTyping(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el) return false;
  const tag = el.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
}

/**
 * The shortcut sheet — `?`, from anywhere that is not a text field.
 *
 * Kept as a single, always-mounted component in AppShell rather than something each screen
 * opts into, for the same reason the finder is: the list is only useful if it is truly always
 * one keystroke away, and a binding that only works on some screens is worse than no binding,
 * because the operator cannot predict which is which.
 *
 * Two things this deliberately does NOT do, recorded here so they are a decision on file
 * rather than something the next person rediscovers by hitting them:
 *
 * 1. No focus trap. `role="dialog"` + `aria-modal="true"` say this is modal, but nothing stops
 *    Tab from walking out of the panel and back into the page behind it. Neither
 *    FileViewerOverlay nor Finder traps focus either, so fixing it only here would still leave
 *    two of the app's three overlays doing the same thing wrong — it would look fixed on this
 *    one dialog and be just as broken everywhere else. The real fix is one shared
 *    `useFocusTrap` all three overlays call, which is out of scope for this task.
 *
 * 2. The finder can end up open UNDERNEATH this sheet, invisibly. Reproduction: open the
 *    finder (⌘K), press Tab so real DOM focus leaves the search input and lands on a
 *    `finder__hit` result button, then press `?`. `isTyping()` sees a `<button>`, not an input,
 *    so it does not block the keystroke — the sheet opens, and because `.finder__scrim` is
 *    z-index 100 against this sheet's 65, the finder stays visually on top while the sheet
 *    silently steals DOM focus into a dialog the operator cannot see. It is narrower than it
 *    sounds (it needs a deliberate Tab away from the search field, which the finder's own
 *    keyboard handling does not encourage), and the honest fix is the two overlays coordinating
 *    — e.g. this sheet checking whether a higher-priority overlay already owns focus before
 *    taking it — not a one-off patch inside either file. Leaving the repro steps here for
 *    whoever adds that coordination.
 */
export function ShortcutSheet() {
  const [open, setOpen] = useState(false);
  const panel = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        // Harmless when the sheet is already closed: setOpen(false) on a false state is a
        // no-op React bails out of before it even re-renders, so this listener does not
        // steal Escape from the finder or the document viewer when this sheet has nothing
        // open. It also never calls preventDefault/stopPropagation, so their own Escape
        // handlers still see the keystroke regardless of listener order.
        setOpen(false);
        return;
      }
      // Deliberately `e.key === '?'`, not "Shift + /". `key` is the character the keyboard
      // layout actually produced, and on a US layout that already requires Shift — testing
      // e.shiftKey too would be redundant at best and wrong on any layout (and there are
      // several) where "?" is not a shifted character.
      if (e.key !== '?' || e.metaKey || e.ctrlKey || e.altKey) return;
      if (isTyping(e.target)) return;
      e.preventDefault();
      setOpen((v) => !v);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  /**
   * Focus the panel so Escape reaches it and a screen reader announces the dialog; return
   * focus where it came from on close, or the operator's next keystroke goes nowhere.
   *
   * This effect is keyed on `[open]`, unlike FileViewerOverlay's mount-only `[]` version of the
   * same pattern — and that difference is deliberate, not an oversight. FileViewerOverlay is
   * conditionally mounted by its caller: every open IS a fresh mount, so `[]` already runs once
   * per open. This sheet is the opposite — mounted once, for good, by AppShell — so `open` has
   * to be a dependency or the effect would only ever run for the sheet's first open and never
   * again.
   *
   * That raised the exact hazard documented on AppShell's own route-focus effect: React 18
   * StrictMode (main.tsx) double-invokes an effect right after the commit that mounts it,
   * to shake out effects that are not idempotent. But that doubling happens once, at the
   * commit where the component (or this effect's dependency) first takes on its initial
   * value — not on every later commit that changes the dependency. At the initial mount here,
   * `open` is `false`, so the doubled run is `if (!open) return;` twice: a no-op twice over,
   * nothing captured, nothing to restore. The state transitions that matter — `?` flipping
   * `open` true, Escape flipping it back — are ordinary updates after that first commit, not
   * re-mounts, so each one gets exactly one effect run and one cleanup. Verified empirically
   * against this exact shape (an effect keyed on a boolean that starts false) before trusting
   * this: StrictMode's doubled run showed up only for the false-to-true transition. So a
   * single opener is captured once per open and restored once per close; nothing is
   * overwritten by a phantom second invocation the way AppShell's one-shot mount flag was.
   */
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
          {/* A glyph, not the word "Close" — the list below already has a row whose own text
              is "Close this, the finder, or a document viewer", and a visible "Close" button
              label here would give the operator (and screen.getByText) two matches for the same
              word on one screen. FileViewerOverlay's close button already made this call. */}
          <button
            type="button"
            className="sheet__close"
            onClick={() => setOpen(false)}
            aria-label="Close"
          >
            ×
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
