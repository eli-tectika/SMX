import type { SessionAttachment } from '../api/types';

/**
 * One attachment and what became of it.
 *
 * An `unsupported` or `failed` file is shown as prominently as a read one, by name and type. That is
 * design §5.2 on the screen: the agent is about to ask the operator what the file shows, and the
 * operator needs to already know why. A chip that quietly omitted unreadable files would make the
 * agent's question look like a non-sequitur — and, worse, make a file the analysis never saw look
 * like one it did.
 *
 * Colour: `chip--neutral` is the app's chip for anything that is NOT a verdict, and an attachment is
 * not one. The unread state is drawn in `--text-warning` inline, the way StageStatusCard tints a
 * retry — never in `.n` or `.x`, which mean NeedsReview and Fail about a substance.
 */
export function AttachmentChip({ attachment }: { attachment: SessionAttachment }) {
  const unread = attachment.status !== 'extracted';
  return (
    <span
      className="chip chip--neutral"
      style={{
        gap: 5,
        height: 'auto',
        padding: '3px 8px',
        color: unread ? 'var(--text-warning)' : undefined,
        background: unread ? 'var(--bg-warning)' : undefined,
      }}
      title={attachment.error ?? undefined}
    >
      <i className="ti ti-paperclip" aria-hidden="true" />
      <b>{attachment.filename}</b>
      <span className="tiny muted">
        {unread ? "couldn't read this one — the agent will ask you about it" : 'read'}
      </span>
    </span>
  );
}
