import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from './components/AppShell';
import { LearnedConclusions } from './routes/LearnedConclusions';
import { MarkerLibrary } from './routes/MarkerLibrary';
import { MsdsRegistry } from './routes/MsdsRegistry';
import { NewProject } from './routes/NewProject';
import { Projects } from './routes/Projects';
import { ProjectLayout } from './routes/ProjectLayout';

export function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<Projects />} />
          <Route path="new" element={<NewProject />} />
          <Route path="p/:projectId" element={<ProjectLayout />} />
          <Route path="p/:projectId/:stage" element={<ProjectLayout />} />
          <Route path="marker-library" element={<MarkerLibrary />} />
          <Route path="learned-conclusions" element={<LearnedConclusions />} />
          <Route path="msds-registry" element={<MsdsRegistry />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
