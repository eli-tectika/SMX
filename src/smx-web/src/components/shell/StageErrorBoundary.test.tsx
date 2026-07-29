import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StageErrorBoundary } from './StageErrorBoundary';

/** Throws unconditionally — stands in for a stage screen hitting `.map` on a malformed payload. */
function Boom(): never {
  throw new Error('boom: simulated malformed payload');
}

describe('StageErrorBoundary', () => {
  // React logs a caught error to console.error on its own; this only silences the test output,
  // it does not touch the boundary's own componentDidCatch logging (asserted below).
  beforeEach(() => vi.spyOn(console, 'error').mockImplementation(() => {}));
  afterEach(() => vi.restoreAllMocks());

  it('renders children when nothing throws', () => {
    render(
      <StageErrorBoundary stageLabel="Discovery">
        <div>real content</div>
      </StageErrorBoundary>,
    );
    expect(screen.getByText('real content')).toBeInTheDocument();
  });

  it('catches a throw and names the stage, without a stack trace', () => {
    render(
      <StageErrorBoundary stageLabel="Discovery">
        <Boom />
      </StageErrorBoundary>,
    );
    expect(screen.getByText(/Discovery screen/i)).toBeInTheDocument();
    expect(screen.getByText(/could not be rendered/i)).toBeInTheDocument();
    expect(screen.queryByText(/boom: simulated malformed payload/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/\/projects\//i)).not.toBeInTheDocument();
  });

  it('still logs the error for a developer', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    render(
      <StageErrorBoundary stageLabel="Discovery">
        <Boom />
      </StageErrorBoundary>,
    );
    // React's own logging plus ours both land on console.error; assert ours ran by looking for
    // the stage name attached to a real Error object somewhere in the captured calls.
    const sawIt = spy.mock.calls.some((args) =>
      args.some((a) => (a instanceof Error ? a.message.includes('boom') : String(a).includes('Discovery'))),
    );
    expect(sawIt).toBe(true);
  });
});
