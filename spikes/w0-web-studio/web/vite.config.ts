import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Production build is served by the spike server (same origin as /api); dev proxies /api to it.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://127.0.0.1:5199' } },
});
