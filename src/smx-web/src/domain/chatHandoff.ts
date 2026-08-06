/**
 * Hand a draft sentence to the agent panel's composer.
 *
 * There is no shared store between a screen and the agent panel — the shell mounts them side by side,
 * not through each other — so the handoff goes through the DOM: find the composer already on the page
 * and set its value through the native `HTMLInputElement` setter, then dispatch a real `input` event.
 * That is what makes React's own `onChange` fire; a plain `input.value = …` changes the pixel and not
 * the state, and the next render wipes it. It is a bridge, not an architecture.
 *
 * It returns FALSE when there is no composer on the page — the panel is collapsible, and on some
 * screens absent. Every caller must say so rather than pretend: a button that silently does nothing
 * is a lying affordance, and here it would be a lying affordance over the only route an operator has
 * to change something the agent wrote.
 *
 * One copy, because there were three and they had already drifted (one moved the caret, two did not).
 */
export function handOffToChat(draft: string): boolean {
  const input = document.querySelector<HTMLInputElement>('input[aria-label^="Message the"]');
  if (!input) return false;
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
  if (!setter) return false;
  setter.call(input, draft);
  input.dispatchEvent(new Event('input', { bubbles: true }));
  input.focus();
  input.setSelectionRange?.(draft.length, draft.length);
  return true;
}
