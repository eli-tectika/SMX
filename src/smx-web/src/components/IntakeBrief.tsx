import type { DossierEntry, DossierState, IntakeBrief as Brief } from '../api/types';
import { AttachmentChip } from './AttachmentChip';
import { SectionHeader } from './ui/Primitives';

/**
 * How a dossier state is drawn. Icon AND words — never colour alone, and never the same words twice.
 *
 * The four states are not four flavours of "we know this". `answered` is the operator speaking;
 * `agent-proposed` is the model guessing and is always shown with its confidence; `unknown` is a
 * stated gap that travels INTO the analysis (data, not a failure); `not-applicable` is a fourth thing
 * again — ruled out, so nothing is missing.
 */
const STATE_MARK: Record<DossierState, { icon: string; label: string }> = {
  answered: { icon: 'ti-check', label: 'answered' },
  // Rendered DIFFERENTLY from `answered`, always with its confidence. An agent inference that reads
  // like an operator statement is the provenance collapse the dossier exists to prevent: the operator
  // signs off on the model's guess believing they said it.
  'agent-proposed': { icon: 'ti-robot', label: 'proposed, not stated' },
  // A stated gap, carried INTO the analysis rather than hidden. It is not a failure.
  unknown: { icon: 'ti-alert-triangle', label: 'unknown' },
  'not-applicable': { icon: 'ti-minus', label: 'not-applicable' },
};

/**
 * Provenance in the second person, because `operator` / `agent` are record tokens and this line is
 * read by the person about to sign. "you said this" and "the model inferred it" cannot be misread
 * for one another at a glance; two lowercase nouns can.
 */
const SAID_BY: Record<string, string> = {
  operator: 'you said this',
  agent: 'the model inferred it',
};

function DossierRow({ entry }: { entry: DossierEntry }) {
  const mark = STATE_MARK[entry.state];
  const inferred = entry.state === 'agent-proposed';
  return (
    <div className="step" data-state={entry.state}>
      <i
        className={`ti ${mark.icon}`}
        aria-hidden="true"
        style={{ marginTop: 2, color: inferred ? 'var(--text-warning)' : undefined }}
      />
      <div>
        <b className="data">{entry.questionId}</b>
        {/* The answer and its confidence sit on ONE line at ONE weight: a confidence rendered as a
            footnote to a sentence is a confidence the eye skips. The state and who said it trail in
            muted type — subordinate, but never absent. */}
        <div className="small secondary">
          {entry.answer}
          {entry.confidence ? ` · confidence ${entry.confidence}` : ''}
          <span className="tiny muted" style={{ marginLeft: 8 }}>
            {mark.label} · {SAID_BY[entry.provenance] ?? entry.provenance}
          </span>
        </div>
      </div>
    </div>
  );
}

/**
 * What the interview recorded, and the one control that starts the analysis.
 *
 * Read-only by law, not by omission (CLAUDE.md Law 4): there is no input, no textarea, no select and
 * no contenteditable anywhere in this component, and a test pins that. The operator changes something
 * by telling the agent why — which is also how the change earns a Learned Conclusion. A field here
 * would silently edit an analytical record with no reason captured and nothing learned.
 *
 * The Start button is the operator's signature (Law 9): the agent may create a project, but only the
 * operator may start one, and there is no agent tool that can.
 */
export function IntakeBrief({
  brief,
  canStart,
  onStart,
  busy = false,
}: {
  brief: Brief;
  canStart: boolean;
  onStart: () => void;
  busy?: boolean;
}) {
  const unknowns = brief.dossier.filter((d) => d.state === 'unknown').length;

  return (
    <section className="screen">
      <div className="cap">
        <b>The brief</b>
        Written by the interview, from what you said. Nothing here can be typed over.
      </div>

      {brief.summary.trim().length > 0 && (
        <p className="small prose" style={{ margin: '0 0 12px' }}>
          {brief.summary}
        </p>
      )}

      <SectionHeader eyebrow="Components" count={brief.components.length} />
      <table className="mx" style={{ marginBottom: 14 }}>
        <thead>
          <tr>
            <th style={{ width: 130 }}>Component</th>
            <th>Material</th>
            <th>Application</th>
            <th>Markets</th>
            <th>Objective</th>
          </tr>
        </thead>
        <tbody>
          {brief.components.map((c) => (
            <tr key={c.id}>
              <td className="data">{c.id}</td>
              <td>{c.material}</td>
              <td>{c.application}</td>
              <td>{c.markets.join(', ')}</td>
              <td>{c.objective}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <SectionHeader eyebrow="Dossier" count={brief.dossier.length} />
      <div className="region" style={{ marginBottom: 14 }}>
        {brief.dossier.map((e) => (
          <DossierRow key={e.questionId} entry={e} />
        ))}
      </div>

      {brief.attachments.length > 0 && (
        <>
          <SectionHeader eyebrow="Attachments" count={brief.attachments.length} />
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 14 }}>
            {brief.attachments.map((a) => (
              <AttachmentChip key={a.fileId} attachment={a} />
            ))}
          </div>
        </>
      )}

      {/* The whole conversation, closed by default. It is the provenance of every line above, so it
          must be reachable — but it is not what the operator is here to read. `<details>` because a
          disclosure needs no form control. */}
      {brief.transcript.length > 0 && (
        <details style={{ marginBottom: 14 }}>
          <summary className="small secondary" style={{ cursor: 'pointer' }}>
            The conversation, turn by turn ({brief.transcript.length})
          </summary>
          <div className="region" style={{ marginTop: 8 }}>
            {brief.transcript.map((t, i) => (
              <div className={`bub ${t.role === 'agent' ? 'ba' : 'bu'}`} key={i}>
                {t.text}
              </div>
            ))}
          </div>
        </details>
      )}

      {canStart && (
        <div className="region">
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <button className="btn primary" type="button" disabled={busy} onClick={onStart}>
              Start Processing
            </button>
            {/* Beside the button, because the gaps are part of what is being signed for. */}
            <span className="tiny muted">
              {unknowns === 0
                ? 'Nothing is open — every question was settled.'
                : unknowns === 1
                  ? '1 question will be carried into the analysis as an unknown.'
                  : `${unknowns} questions will be carried into the analysis as unknowns.`}
            </span>
          </div>
          <div className="tiny muted" style={{ marginTop: 8 }}>
            To change anything above, tell the agent why — it re-derives and records the reason as a
            Learned Conclusion.
          </div>
        </div>
      )}
    </section>
  );
}
