import { Link } from 'react-router-dom';
import { matrixXlsxUrl } from '../api/client';
import type { ProjectListItem } from '../api/types';
import { MiniSpine } from '../components/ui/MiniSpine';
import { Card, EmptyState, SectionHeader, Skeleton, StatCard } from '../components/ui/Primitives';
import { VerdictRibbon } from '../components/ui/VerdictRibbon';
import { BUCKET_LABEL, bucket, bucketTone, whatsBlocking, type Bucket } from '../domain/blocking';
import { useProjectsOverview, type ProjectCard } from '../hooks/useProjectsOverview';
import { DEMO_ENABLED, forgetDemoProject, isDemo, loadDemoProject } from '../mocks/demo';

/**
 * The re-entry surface (spec §2).
 *
 * It must answer, at a glance: what is blocked and on whom, what is ready to
 * continue, and what needs signing.
 *
 * Everything here is REAL — the cards come from GET /projects, so this is what the
 * record holds rather than what this browser remembers. That is why the screen carries
 * no MockBadge; the opt-in demo fixture is the one exception and badges itself.
 */
export function Projects() {
  const { cards, loading, error, refresh } = useProjectsOverview();

  const bucketOf = (c: ProjectCard): Bucket => bucket(c.project, c.matrix, c.unopenedFlagged);

  const groups: Record<Bucket, ProjectCard[]> = {
    'needs-you': cards.filter((c) => bucketOf(c) === 'needs-you'),
    running: cards.filter((c) => bucketOf(c) === 'running'),
    settled: cards.filter((c) => bucketOf(c) === 'settled'),
  };

  const flaggedVerdicts = cards.reduce((n, c) => n + (c.matrix?.flagged.length ?? 0), 0);

  if (loading && cards.length === 0) return <ProjectsLoading />;
  if (error) return <ProjectsError message={error} onRetry={refresh} />;
  if (cards.length === 0) return <ProjectsEmpty onLoadDemo={refresh} />;

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
                  key={c.project.projectId}
                  card={c}
                  onForgetDemo={() => {
                    forgetDemoProject();
                    refresh();
                  }}
                />
              ))}
            </div>
          </section>
        ) : null,
      )}
    </>
  );
}

function ProjectRow({ card, onForgetDemo }: { card: ProjectCard; onForgetDemo: () => void }) {
  const { project, matrix, unopenedFlagged } = card;
  const blocking = whatsBlocking(project, matrix, unopenedFlagged);
  const b = bucket(project, matrix, unopenedFlagged);
  const tone = bucketTone(b, blocking);
  const demo = isDemo(project.projectId);

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
        <CardHead project={project} />

        {/* The dashboard is otherwise entirely real data. The demo project is the one
            exception, so it must announce itself — a fabricated card must never be
            able to pass for a real one. */}
        {demo && (
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

        {/* The stages came from the list, so the card is still true; only the verdict ribbon is
            missing. Saying so beats a card that silently looks like it has no matrix. */}
        {card.error && (
          <div className="small" style={{ color: 'var(--text-danger)', marginTop: 12 }}>
            <i className="ti ti-alert-triangle" aria-hidden="true" /> Could not read the matrix:{' '}
            {card.error}
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

      {/* Outside the link: a link must not wrap another interactive control. There is no Forget for
          a real project — it lives in the record, and a button that only cleared it from this
          browser would be undone by the next refresh. */}
      {(matrix || demo) && (
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
          {demo && (
            <button className="btn" style={{ marginLeft: 'auto' }} onClick={onForgetDemo}>
              <i className="ti ti-x" aria-hidden="true" /> Forget demo
            </button>
          )}
        </div>
      )}
    </Card>
  );
}

function CardHead({ project }: { project: ProjectListItem }) {
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
        {project.product}
      </span>
      <span className="tiny muted">
        {project.client} · <span className="data">{project.projectId}</span> · created{' '}
        {project.createdAt.slice(0, 10)}
      </span>
    </div>
  );
}

function ProjectsLoading() {
  return (
    <>
      <SectionHeader title="Projects" />
      <div className="card-list">
        {[0, 1].map((i) => (
          <Card key={i}>
            <Skeleton variant="text" width="40%" />
            <div style={{ margin: '14px 0 10px' }}>
              <Skeleton variant="spine" height={9} />
            </div>
            <Skeleton variant="text" width="60%" />
          </Card>
        ))}
      </div>
    </>
  );
}

/**
 * The list itself failed. Distinct from "no projects" on purpose: an unreachable API and an empty
 * record look identical on screen otherwise, and telling the operator "you have no projects" when
 * the truth is "I could not ask" is exactly the kind of confident wrong answer this system exists
 * to avoid.
 */
function ProjectsError({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <>
      <SectionHeader title="Projects" />
      <EmptyState
        icon="ti-alert-triangle"
        title="Could not read the project list."
        body={
          <>
            The record may be fine — this says only that <span className="data">GET /projects</span>{' '}
            did not answer: {message}
          </>
        }
        actions={
          <button className="btn primary" onClick={onRetry}>
            <i className="ti ti-refresh" aria-hidden="true" /> Try again
          </button>
        }
      />
    </>
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
        title="No projects yet."
        body={
          <>
            The record holds no projects. This is the whole record, not this browser's memory of it —
            create one here and it will be waiting on any machine you sign in from.
          </>
        }
        actions={
          <>
            <Link className="btn primary" to="/new">
              <i className="ti ti-plus" aria-hidden="true" /> New project
            </Link>
            {DEMO_ENABLED && (
              <button
                className="btn"
                onClick={() => {
                  loadDemoProject();
                  onLoadDemo();
                }}
                title="Adds a fixture project so the populated dashboard can be seen without creating a real one"
              >
                <i className="ti ti-flask" aria-hidden="true" /> Load demo data
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
            Six are backed by the API today — intake, discovery, regulatory, matrix, dosing and cost.
            The rest have no agent yet, and render fixture data behind a mock badge.
          </p>
        </div>
      </EmptyState>
    </>
  );
}
