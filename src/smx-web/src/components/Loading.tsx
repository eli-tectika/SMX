export function Loading({ what }: { what: string }) {
  return (
    <div className="screen" style={{ color: 'var(--text-muted)', fontSize: 12 }}>
      <i className="ti ti-loader" aria-hidden="true" /> Loading {what}…
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
