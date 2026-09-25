import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { httpApi } from './api';
import { App } from './App';
import { Studio } from './studio';
import './style.css';

// Same origin as MyRPA.Server: the session cookie goes with every request and the one EventSource of this tab.
const studio = new Studio(httpApi(), (url) => new EventSource(url));

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App studio={studio} />
  </StrictMode>,
);
