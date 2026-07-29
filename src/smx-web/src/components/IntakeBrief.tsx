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
            {mark.label} ·{' '}
            {/* Its own element, so a test can assert on WHO SAID IT rather than on the row's whole
                text. Without the hook the only available assertion is "the two rows read
                differently", which the state label alone already satisfies — so the provenance could
                vanish with the suite still green. It was verified to do exactly that. */}
            <span data-said-by={entry.provenance}>
              {SAID_BY[entry.provenance] ?? entry.provenance}
            </span>
          </span>
        </div>
      </div>
    </div>
  );
}

/**
 * What the interview recorded, question by question.
 *
 * Read-only by law, not by omission (CLAUDE.md Law 4): there is no input, no textarea, no select and
 * no contenteditable anywhere in this component, and a test pins that. The operator changes something
 * by telling the agent why — which is also how the change earns a Learned Conclusion. A field here
 * would silently edit an analytical record with no reason captured and nothing learned.
 *
 * Two things left this component in the redesign, and both left for a reason:
 *  - **Start Processing.** It is the operator's signature (Law 9: the agent may create a project,
 *    only the operator may start one) and it was buried three sections down this panel. It is now
 *    the next-action block's button, at the top of the screen, and exists nowhere else.
 *  - **The components table**, which the intake screen now owns. The record's own component list
 *    carries more than the brief's copy of it (physical state, batch mass), and two tables of the
 *    same four components on one screen is exactly the undifferentiated column being unwound.
 *
 * What is left is the dossier and its provenance — which is the evidence behind the summary, and
 * the reason the panel is worth opening.
 */
export function IntakeBrief({ brief }: { brief: Brief }) {
  return (
    <>
      {brief.summary.trim().length > 0 && (
        <p className="prose" style={{ margin: '0 0 12px' }}>
          {brief.summary}
        </p>
      )}

      {/* The summary above is the conclusion; this is what it was drawn from. One interaction away,
          never further — the operator is about to start an analysis on it. */}
      <details style={{ marginBottom: 12 }}>
        <summary className="small secondary" style={{ cursor: 'pointer' }}>
          Question by question, and who answered ({brief.dossier.length})
        </summary>
        <div className="region" style={{ marginTop: 8 }}>
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
      </details>

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

      {/* Law 4, stated where the operator is looking at something they cannot type over. It used to
          sit beside the Start button and only appear when the project could be started — so on every
          project already running, the one rule that explains why nothing here is editable was not on
          the screen at all. */}
      <p className="small muted" style={{ margin: 0 }}>
        To change anything here, tell the agent why — it re-derives and records the reason as a
        Learned Conclusion.
      </p>
    </>
  );
}
