// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { BackLink } from './BackLink';

/** A two-screen app: a list that links into a detail screen carrying (or not) a `from` label. */
function App({ withLabel }: { withLabel: boolean }) {
  return (
    <Routes>
      <Route
        path="/list"
        element={
          <div>
            <h1>the list</h1>
            <Link to="/detail" state={withLabel ? { from: { label: 'the MSDS registry' } } : undefined}>
              open
            </Link>
          </div>
        }
      />
      <Route
        path="/detail"
        element={<BackLink fallback="/list" fallbackLabel="documents" />}
      />
    </Routes>
  );
}

describe('BackLink', () => {
  it('steps back to the screen it was opened from, and names it', async () => {
    render(
      <MemoryRouter initialEntries={['/list']}>
        <App withLabel />
      </MemoryRouter>,
    );
    await userEvent.click(screen.getByRole('link', { name: 'open' }));

    const back = screen.getByRole('button', { name: /back to the msds registry/i });
    await userEvent.click(back);
    expect(screen.getByRole('heading', { name: 'the list' })).toBeInTheDocument();
  });

  it('says plain "Back" when the caller did not prove where it came from', async () => {
    render(
      <MemoryRouter initialEntries={['/list']}>
        <App withLabel={false} />
      </MemoryRouter>,
    );
    await userEvent.click(screen.getByRole('link', { name: 'open' }));

    // Not "Back to documents": history goes to the list, so naming the library would be a lie.
    expect(screen.getByRole('button', { name: 'Back' })).toBeInTheDocument();
  });

  it('offers the fallback as a real link when the detail screen is the first entry', () => {
    // A pasted URL or a refresh. Stepping back from here leaves the app, so there is a
    // destination instead of a history step — and it is a link, so it behaves like one.
    render(
      <MemoryRouter initialEntries={['/detail']}>
        <App withLabel={false} />
      </MemoryRouter>,
    );
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /back to documents/i })).toHaveAttribute(
      'href',
      '/list',
    );
  });
});
