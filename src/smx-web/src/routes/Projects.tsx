import { Link } from 'react-router-dom';
import { matrixXlsxUrl } from '../api/client';
import { MiniSpine } from '../components/ui/MiniSpine';
import { Card, EmptyState, SectionHeader, Skeleton, StatCard } from '../components/ui/Primitives';
import { VerdictRibbon } from '../components/ui/VerdictRibbon';
import { BUCKET_LABEL, bucket, bucketTone, whatsBlocking, type Bucket } from '../domain/blocking';
import { forgetProject, useProjectsOverview, type ProjectCard } from '../hooks/useProjectsOverview';
import { isDemo, loadDemoProject } from '../mocks/demo';

/**
 * The re-entry surface (spec §2).
 *
 * It must answer, at a glance: what is blocked and on whom, what is ready to
 * continue, and what needs signing.
 *
 * Everything here is REAL. localStorage supplies only the project ids; the stage
 * states and verdict counts come from the record. That is why this screen carries
 * no MockBadge — there is nothing fabricated on it to warn about.
 */
export function Projects() {
  const { cards, loading, refresh } = useProjectsOverview();

  const ready = cards.filter((c) => c.state.kind === 'ready');
  const stale = cards.filter((c) => c.state.kind === 'stale');

  const bucketOf = (c: ProjectCard): Bucket | null =>
    c.state.kind === 'ready'
      ? bucket(c.state.project, c.state.matrix, c.state.unopenedFlagged)
      : null;

  const groups: Record<Bucket, ProjectCard[]> = {
    'needs-you': ready.filter((c) => bucketOf(c) === 'needs-you'),
    running: ready.filter((c) => bucketOf(c) === 'running'),
    settled: ready.filter((c) => bucketOf(c) === 'settled'),
  };

  const flaggedVerdicts = ready.reduce(
    (n, c) => n + (c.state.kind === 'ready' ? (c.state.matrix?.flagged.length ?? 0) : 0),
    0,
  );

  if (cards.length === 0 && !loading) return <ProjectsEmpty onLoadDemo={refresh} />;

  return (
    <>
      <SectionHeader
        title="Projects"
        hint="What is blocked, what is ready to continue, and what needs signing."
        actions={
          <>
            <button className="btn" onClick={refresh} disabled={loading}>
              <i className={`ti ti-refresh ${loading ? 'spin' : ''}`} aria-hidden="true" /> Refresh
            </button>
            <Link className="btn primary" to="/new">
              <i className="ti ti-plus" aria-hidden="true" /> New project
            </Link>
          </>
        }
      />

      <div className="stat-strip">
        <StatCard
          label="Blocked"
          value={groups['needs-you'].length}
          tone={groups['needs-you'].length > 0 ? 'warning' : undefined}
          hint="need a human"
        />
        <StatCard
          label="Running"
          value={groups.running.length}
          tone={groups.running.length > 0 ? 'accent' : undefined}
          hint="agents in flight"
        />
        <StatCard
          label="Flagged verdicts"
          value={flaggedVerdicts}
          tone={flaggedVerdicts > 0 ? 'warning' : undefined}
          hint="must be opened before a gate arms"
        />
        <StatCard label="Settled" value={groups.settled.length} hint="all backed stages done" />
        {/*
          Spec §2 requires a "needs signing" count. No endpoint reports gate state,
          so no number here could be true. Showing the hole and naming it is the
          audit-ledger move — and it documents exactly what the backend still owes.
        */}
        <StatCard
          label="Needs signing"
          absent
          hint="not reportable — no gate state in the record"
        />
      </div>

      {(['needs-you', 'running', 'settled'] as const).map((b) =>
        groups[b].length > 0 ? (
          <section key={b}>
            <SectionHeader eyebrow={BUCKET_LABEL[b]} count={groups[b].length} />
            <div className="card-list">
              {groups[b].map((c) => (
                <ProjectRow
                  key={c.recent.projectId}
                  card={c}
                  onForget={() => {
                    forgetProject(c.recent.projectId);
                    refresh();
                  }}
                />
              ))}
            </div>
          </section>
        ) : null,
      )}

      {loading && cards.length > 0 && ready.length === 0 && (
        <div className="card-list">
          {cards.map((c) => (
            <ProjectRow
              key={c.recent.projectId}
              card={c}
              onForget={() => {
                forgetProject(c.recent.projectId);
                refresh();
              }}
            />
          ))}
        </div>
      )}

      {stale.length > 0 && (
        <section>
          <SectionHeader
            eyebrow="Stale pointers"
            count={stale.length}
            hint="this browser remembers an id the record no longer has"
          />
          {stale.map((c) => (
            <div
              key={c.recent.projectId}
              className="region"
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 10,
                marginBottom: 6,
                borderStyle: 'dashed',
              }}
            >
              <span className="small muted">{c.recent.product}</span>
              <span className="tiny muted data">
                {c.recent.projectId}
              </span>
              <button
                className="btn"
                style={{ marginLeft: 'auto' }}
                onClick={() => {
                  forgetProject(c.recent.projectId);
                  refresh();
                }}
              >
                Forget
              </button>
            </div>
          ))}
        </section>
      )}
    </>
  );
}

function ProjectRow({ card, onForget }: { card: ProjectCard; onForget: () => void }) {
  const { recent, state } = card;

  if (state.kind === 'stale') return null;

  if (state.kind === 'loading') {
    // Identity renders instantly from localStorage; only live state is a skeleton.
    return (
      <Card>
        <CardHead recent={recent} />
        <div style={{ margin: '12px 0 10px' }}>
          <Skeleton variant="spine" height={9} />
        </div>
        <Skeleton variant="text" width="60%" />
      </Card>
    );
  }

  if (state.kind === 'error') {
    return (
      <Card tone="danger">
        <CardHead recent={recent} />
        <div className="small" style={{ color: 'var(--text-danger)', marginTop: 8 }}>
          <i className="ti ti-alert-triangle" aria-hidden="true" /> Could not read the record:{' '}
          {state.message}
        </div>
      </Card>
    );
  }

  const { project, matrix, unopenedFlagged } = state;
  const blocking = whatsBlocking(project, matrix, unopenedFlagged);
  const b = bucket(project, matrix, unopenedFlagged);
  const tone = bucketTone(b, blocking);

  return (
    <Card tone={tone} className="card--link-wrap">
      <Link
        to={`/p/${project.projectId}/intake`}
        className="card--link"
        style={{
          padding: 0,
          border: 0,
          boxShadow: 'none',
          background: 'transparent',
        }}
      >
        <CardHead recent={recent} />

        {/* The dashboard is otherwise entirely real data. The demo project is the one
            exception, so it must announce itself — a fabricated card must never be
            able to pass for a real one. */}
        {isDemo(project.projectId) && (
          <div className="banner warn" style={{ margin: '10px 0 0' }}>
            <i className="ti ti-flask" aria-hidden="true" />
            <div>
              <b>Demo data</b> — a fixture project, not a real record. Dev only. Use <i>Forget</i>{' '}
              to remove it.
            </div>
          </div>
        )}

        <div style={{ margin: '14px 0 10px' }}>
          <MiniSpine stages={project.stages} showLabels />
        </div>

        {blocking && (
          <div
            className="small"
            style={{
              display: 'flex',
              alignItems: 'flex-start',
              gap: 6,
              color: `var(--text-${blocking.tone === 'muted' ? 'muted' : blocking.tone})`,
              marginTop: 12,
            }}
          >
            <i
              className={`ti ${blocking.icon}`}
              aria-hidden="true"
              data-running={blocking.icon === 'ti-loader' ? '' : undefined}
              style={{ marginTop: 1 }}
            />
            <div>
              {blocking.text}
              {blocking.detail && (
                <div className="data" style={{ fontSize: 11, marginTop: 3, opacity: 0.9 }}>
                  {blocking.detail}
                </div>
              )}
            </div>
          </div>
        )}

        {matrix && (
          <div style={{ marginTop: 14 }}>
            <div className="tiny muted" style={{ marginBottom: 5 }}>
              {matrix.rows} substances × {matrix.cols} components · assembled{' '}
              {matrix.generatedAt.slice(0, 10)}
            </div>
            <VerdictRibbon counts={matrix.counts} />
            {(matrix.lowConfidence > 0 || unopenedFlagged > 0) && (
              <div className="tiny" style={{ color: 'var(--text-warning)', marginTop: 6 }}>
                {matrix.lowConfidence > 0 && (
                  <span>
                    {matrix.lowConfidence} verdict
                    {matrix.lowConfidence === 1 ? '' : 's'} below 75% confidence
                  </span>
                )}
                {matrix.lowConfidence > 0 && unopenedFlagged > 0 && ' · '}
                {unopenedFlagged > 0 && (
                  <span>
                    {unopenedFlagged} cell{unopenedFlagged === 1 ? '' : 's'} not yet opened
                  </span>
                )}
              </div>
            )}
          </div>
        )}
      </Link>

      {/* Outside the link: a link must not wrap another interactive control. */}
      {(matrix || isDemo(project.projectId)) && (
        <div
          style={{
            marginTop: 10,
            paddingTop: 10,
            borderTop: '1px solid var(--border)',
            display: 'flex',
            gap: 8,
          }}
        >
          {matrix && (
            <a className="btn" href={matrixXlsxUrl(project.projectId)} download>
              <i className="ti ti-download" aria-hidden="true" /> Matrix .xlsx
            </a>
          )}
          {isDemo(project.projectId) && (
            <button className="btn" style={{ marginLeft: 'auto' }} onClick={onForget}>
              <i className="ti ti-x" aria-hidden="true" /> Forget demo
            </button>
          )}
        </div>
      )}
    </Card>
  );
}

function CardHead({ recent }: { recent: ProjectCard['recent'] }) {
  return (
    <div
      style={{
        display: 'flex',
        alignItems: 'baseline',
        gap: 8,
        flexWrap: 'wrap',
      }}
    >
      <span
        style={{
          fontSize: 'var(--t-lead)',
          fontWeight: 500,
          color: 'var(--ink)',
        }}
      >
        {recent.product}
      </span>
      <span className="tiny muted">
        {recent.client} · <span className="data">{recent.projectId}</span>{' '}
        · created {recent.createdAt.slice(0, 10)}
      </span>
    </div>
  );
}

function ProjectsEmpty({ onLoadDemo }: { onLoadDemo: () => void }) {
  return (
    <>
      <SectionHeader
        title="Projects"
        actions={
          <Link className="btn primary" to="/new">
            <i className="ti ti-plus" aria-hidden="true" /> New project
          </Link>
        }
      />
      <EmptyState
        icon="ti-flask-2"
        title="No projects on this browser."
        body={
          <>
            The API has no list-projects endpoint. This page remembers the ids you created here;
            each one is re-read from the record when you open it, so a stale pointer can never put
            wrong data on screen.
          </>
        }
        actions={
          <>
            <Link className="btn primary" to="/new">
              <i className="ti ti-plus" aria-hidden="true" /> New project
            </Link>
            {import.meta.env.DEV && (
              <button
                className="btn"
                onClick={() => {
                  loadDemoProject();
                  onLoadDemo();
                }}
                title="Adds a fixture project so the populated dashboard can be seen without creating a real one"
              >
                <i className="ti ti-flask" aria-hidden="true" /> Load demo data · dev only
              </button>
            )}
          </>
        }
      >
        {/* Structure, not verdict data — this teaches the journey without fabricating one. */}
        <div style={{ maxWidth: 520, margin: '28px auto 0', textAlign: 'left' }}>
          <div className="sec__eyebrow" style={{ marginBottom: 8 }}>
            The eight stages
          </div>
          <MiniSpine showLabels />
          <p className="tiny muted" style={{ marginTop: 10 }}>
            Three are backed by the API today — intake, screening and matrix. The rest have no agent
            yet, and render fixture data behind a mock badge.
          </p>
        </div>
      </EmptyState>
    </>
  );
}
