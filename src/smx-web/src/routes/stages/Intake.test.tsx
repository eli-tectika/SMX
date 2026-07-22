import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Intake } from './Intake';
import type { IntakeBrief as Brief, ProjectSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  NotFound: Symbol.for('NotFound'),
  getIntakeBrief: vi.fn(),
  startProject: vi.fn(),
}));
import * as api from '../../api/client';

const project = (intakeStatus = 'awaiting-confirmation'): ProjectSummary => ({
  projectId: 'proj-1',
  client: 'Acme',
  product: 'PET bottle',
  stages: {
    intake: { status: intakeStatus as ProjectSummary['stages'][string]['status'], attempts: 0 },
    discovery: { status: 'pending', attempts: 0 },
    regulatory: { status: 'pending', attempts: 0 },
    matrix: { status: 'pending', attempts: 0 },
  },
});

const brief: Brief = {
  projectId: 'proj-1',
  sessionId: 'isx-1',
  summary: 'Acme make a 500 ml PET bottle.',
  components: [
    { id: 'bottle', material: 'PET', application: 'food contact', markets: ['EU'], objective: 'brand' },
  ],
  dossier: [],
  attachments: [],
  transcript: [],
  createdAt: '2026-07-22T10:00:00Z',
};

const show = (p = project(), onRefresh = vi.fn()) => {
  render(
    <MemoryRouter>
      <Intake project={p} onRefresh={onRefresh} />
    </MemoryRouter>,
  );
  return onRefresh;
};

beforeEach(() => {
  vi.mocked(api.getIntakeBrief).mockResolvedValue(brief);
  vi.mocked(api.startProject).mockResolvedValue({ status: 'pending' });
});

describe('the intake stage screen', () => {
  it('says a form-made project has no brief, rather than showing an empty panel', async () => {
    // NotFound is the NORMAL state for every project created through the old form. Nothing was
    // fabricated and nothing failed — so the screen states the fact instead of rendering a void.
    vi.mocked(api.getIntakeBrief).mockResolvedValue(api.NotFound);
    show();

    expect(await screen.findByText(/created through the form/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /start processing/i })).not.toBeInTheDocument();
  });

  it('renders the brief and starts the analysis on the operator’s press', async () => {
    const onRefresh = show();

    await userEvent.click(await screen.findByRole('button', { name: /start processing/i }));

    expect(api.startProject).toHaveBeenCalledWith('proj-1');
    // The spine has to catch up with the record the press just changed.
    await waitFor(() => expect(onRefresh).toHaveBeenCalled());
  });

  it('shows an error when start finds no such project, instead of failing silently', async () => {
    vi.mocked(api.startProject).mockResolvedValue(api.NotFound);
    const onRefresh = show();

    await userEvent.click(await screen.findByRole('button', { name: /start processing/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/no project with id proj-1/i);
    expect(onRefresh).not.toHaveBeenCalled();
  });

  it('does not offer Start once the stage has left awaiting-confirmation', async () => {
    show(project('running'));

    expect(await screen.findByText(/500 ml PET bottle/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /start processing/i })).not.toBeInTheDocument();
  });
});
