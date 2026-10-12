import '@fontsource/ibm-plex-sans/400.css';
import '@fontsource/ibm-plex-sans/500.css';
import '@fontsource/ibm-plex-sans/600.css';
import '@fontsource/ibm-plex-sans/700.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { httpApi } from './api';
import { App } from './App';
import { httpOrchestratorApi } from './orchestrator';
import { Studio } from './studio';
import './style.css';

// Same origin as MyRPA.Server: the session cookie goes with every request and the one EventSource of this tab.
const studio = new Studio(httpApi(), (url) => new EventSource(url));

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App studio={studio} orchestrator={httpOrchestratorApi()} />
  </StrictMode>,
);
