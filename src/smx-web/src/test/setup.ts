import '@testing-library/jest-dom/vitest';

/**
 * jsdom does not implement `CSS.escape`, and the matrix's flagged-queue jump uses it to
 * build a selector from a cell key (a CAS number contains hyphens and the key contains a
 * pipe). Without this the `f` shortcut throws under test but works in every real browser —
 * a false failure, which is its own kind of lie.
 */
if (typeof CSS === 'undefined') {
  // @ts-expect-error — minimal shim, test environment only
  globalThis.CSS = {};
}
if (typeof CSS.escape !== 'function') {
  CSS.escape = (value: string) => value.replace(/[^a-zA-Z0-9_-]/g, (c) => `\\${c}`);
}
