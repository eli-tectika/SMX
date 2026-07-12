import { Skeleton } from './ui/Primitives';

/** The spinner spins. An idle spinner is a lie about work in progress. */
export function Loading({ what }: { what: string }) {
  return (
    <div className="screen">
      <div className="small muted" style={{ marginBottom: 14 }}>
        <i className="ti ti-loader" data-running="" aria-hidden="true" /> Loading {what}…
      </div>
      <Skeleton variant="text" width="40%" />
      <Skeleton variant="text" width="70%" />
      <Skeleton variant="text" width="55%" />
    </div>
  );
}

export function ErrorScreen({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="screen">
      <div className="banner danger">
        <i className="ti ti-alert-triangle" aria-hidden="true" />
        <div>
          <b>{title}</b>
          {detail ? <div style={{ marginTop: 3 }}>{detail}</div> : null}
        </div>
      </div>
    </div>
  );
}
