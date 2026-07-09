const KEY = 'smx.recentProjects';
const MAX = 20;

export interface RecentProject {
  projectId: string;
  client: string;
  product: string;
  createdAt: string;
}

/**
 * The backend has no list-projects endpoint, so the Projects screen remembers what
 * this browser created. These are pointers, not data — every one is re-fetched from
 * GET /projects/{id} when opened, so a stale or hand-edited entry cannot put wrong
 * project data on screen.
 */
export function readRecentProjects(): RecentProject[] {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (p): p is RecentProject => typeof p === 'object' && p !== null && 'projectId' in p,
    );
  } catch {
    return [];
  }
}

export function rememberProject(p: RecentProject): void {
  const next = [p, ...readRecentProjects().filter((r) => r.projectId !== p.projectId)].slice(0, MAX);
  try {
    localStorage.setItem(KEY, JSON.stringify(next));
  } catch {
    /* private mode / quota — recents are a convenience, not state we depend on */
  }
}
