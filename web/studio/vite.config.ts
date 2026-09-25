import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Production: MyRPA.Server serves `dist` from its own origin (`--web web/studio/dist`, ADR-0022), so no proxy exists.
// Development only (`npm run dev`): this dev server stands in for that origin and forwards the API and the start link
// to a running server. See docs/architecture/web-studio.md.
const server = process.env.MYRPA_SERVER ?? 'http://127.0.0.1:5310';
const devOrigin = 'http://127.0.0.1:5173';

export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true, sourcemap: false },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: server,
        changeOrigin: true,
        configure(proxy) {
          // The server accepts state changes only from its own origin (ADR-0025). Requests from this dev page are
          // re-labelled as coming from the server's origin; any other Origin passes through unchanged and is refused.
          proxy.on('proxyReq', (request, incoming) => {
            if (incoming.headers.origin === devOrigin) {
              request.setHeader('origin', server);
            }
          });
        },
      },
      // The one-time start link: the server sets the session cookie and redirects back to `/` on this dev server.
      '^/\\?token=': { target: server, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
