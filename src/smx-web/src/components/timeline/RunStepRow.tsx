import type { RunStep } from '../../api/thread';

const ICONS: Record<RunStep['kind'], string> = {
  started: 'ti-player-play',
  'tool-call': 'ti-tool',
  rejected: 'ti-refresh',
  output: 'ti-writing-sign',
  outcome: 'ti-flag',
};

/**
 * One code-observed step.
 *
 * Every string here was written by the server from something it watched happen — never by a model
 * about itself (execution-core-design D7). So this component formats; it never hedges or qualifies.
 */
export function RunStepRow({ step }: { step: RunStep }) {
  return (
    <div className="step" data-kind={step.kind}>
      <i className={`ti ${ICONS[step.kind]}`} aria-hidden="true" />
      <div>
        {step.text}
        {(step.detail?.tool || step.detail?.recordId) && (
          <div>
            {step.detail.tool && <span className="src">{step.detail.tool}</span>}
            {step.detail.recordId && (
              <span className="src data" title="the record this step wrote">
                <i className="ti ti-writing-sign" aria-hidden="true" /> {step.detail.recordId}
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
