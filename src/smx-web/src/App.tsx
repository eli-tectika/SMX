import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from './components/AppShell';
import { DocumentView } from './routes/DocumentView';
import { Documents } from './routes/Documents';
import { Interview } from './routes/Interview';
import { LearnedConclusions } from './routes/LearnedConclusions';
import { MarkerLibrary } from './routes/MarkerLibrary';
import { MsdsRegistry } from './routes/MsdsRegistry';
import { Projects } from './routes/Projects';
import { ProjectLayout } from './routes/ProjectLayout';

export function App() {
  return (
    // Opt into the React Router v7 behaviours now, which also silences the two dev-console
    // future-flag warnings. v7_startTransition wraps route state updates in React.startTransition;
    // v7_relativeSplatPath changes relative resolution under a splat — the only splat here is the
    // catch-all redirect to "/", which resolves nothing relative, so both are safe no-ops today.
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<Projects />} />
          <Route path="new" element={<Interview />} />
          <Route path="new/:sessionId" element={<Interview />} />
          <Route path="p/:projectId" element={<ProjectLayout />} />
          <Route path="p/:projectId/:stage" element={<ProjectLayout />} />
          <Route path="marker-library" element={<MarkerLibrary />} />
          <Route path="learned-conclusions" element={<LearnedConclusions />} />
          <Route path="msds-registry" element={<MsdsRegistry />} />
          <Route path="docs" element={<Documents />} />
          <Route path="docs/:documentId" element={<DocumentView />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
