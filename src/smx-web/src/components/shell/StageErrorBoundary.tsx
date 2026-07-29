import { Component, type ReactNode } from 'react';

interface Props {
  /** Named in the fallback so the operator knows which screen broke, not just that one did. */
  stageLabel: string;
  children: ReactNode;
}

interface State {
  caught: boolean;
}

/**
 * Catches a throw from one stage screen so it cannot take the whole shell down with it.
 *
 * The failure mode this exists for: `src/api/client.ts` casts every response with `as` and
 * validates nothing at runtime, so a shape drift between a deployed backend and this frontend —
 * a renamed field, a migrated document, a partial deploy — surfaces as a plain TypeError deep in
 * a screen (e.g. `GET /projects/{id}/pool` coming back as a bare list makes `ProposedPool` call
 * `.map` on `undefined`). Uncaught, that unmounts `ProjectHeader` and `StageStepper` right along
 * with the screen, and the operator loses the one thing that would tell them which project and
 * stage they were even looking at.
 *
 * Wraps only `{screen}` inside `WorkArea`'s artifact column (see ProjectLayout) — never the
 * header, the stepper, or the chat column — so a broken screen still leaves the operator
 * oriented and able to navigate elsewhere. `ProjectLayout` keys this by stage slug, so moving to
 * a different stage mounts a fresh instance instead of replaying the caught error underneath it.
 *
 * No retry-in-place: the failure is a code/data bug, not a transient one, so a "try again"
 * button would just throw again in the same render. Changing stage is the real recovery path,
 * and React error boundaries can only be class components — this is not a place to reach for a
 * library.
 */
export class StageErrorBoundary extends Component<Props, State> {
  state: State = { caught: false };

  static getDerivedStateFromError(): State {
    return { caught: true };
  }

  componentDidCatch(error: unknown, info: { componentStack: string }) {
    // The one place this failure is allowed to show detail — a developer's console, never the
    // operator's screen (no stack trace, no endpoint name in the rendered fallback below).
    console.error(`Stage screen "${this.props.stageLabel}" threw and was caught:`, error, info.componentStack);
  }

  render() {
    if (this.state.caught) {
      return (
        <div className="screen" role="alert">
          <div className="banner danger">
            <i className="ti ti-alert-triangle" aria-hidden="true" />
            <div>
              <b>The {this.props.stageLabel} screen could not be rendered.</b>
              <div className="tiny" style={{ marginTop: 3 }}>
                Try another stage, or come back to this one later.
              </div>
            </div>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
