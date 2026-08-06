import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import type { ProjectSummary } from '../../api/types';

/**
 * The project the chrome is currently scoped to.
 *
 * The top bar names it and the sidebar reports its phase statuses, but the only component that
 * READS it from the API is `ProjectLayout`, which is a child of the shell. So the record travels
 * UP: the layout publishes what it already fetched, and the chrome renders it.
 *
 * The alternative — `AppShell` calling `useProject` itself — would open a second 3-second poll
 * against `GET /projects/{id}` for the whole life of the tab, and the two copies would disagree for
 * one interval every time a stage moved. One reader, one record.
 *
 * A project this context is handed is NOT automatically the project on screen: see `AppShell`,
 * which matches the published id against the route before showing a name. That guard is what stops
 * the top bar from naming a project the operator has already navigated away from.
 */
interface ScopeValue {
  project: ProjectSummary | null;
  publish: (project: ProjectSummary | null) => void;
}

const ScopeContext = createContext<ScopeValue>({ project: null, publish: () => {} });

export function ScopeProvider({ children }: { children: ReactNode }) {
  const [project, setProject] = useState<ProjectSummary | null>(null);
  // Stable, so the publishing effect in ProjectLayout depends on an identity that never changes —
  // a fresh callback each render would re-fire that effect on every poll tick.
  const publish = useCallback((next: ProjectSummary | null) => setProject(next), []);
  const value = useMemo(() => ({ project, publish }), [project, publish]);
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>;
}

/** What the chrome reads. */
export const useScope = () => useContext(ScopeContext).project;

/**
 * What the layout writes.
 *
 * Call it from an effect, never during render — publishing during render would be a setState on a
 * parent while a child renders, which React rejects outright.
 */
export const usePublishScope = () => useContext(ScopeContext).publish;
