import { defineConfig } from 'vite';
import type { Plugin, ProxyOptions } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { hostname } from 'node:os';
import { fileURLToPath } from 'node:url';
import { checkPlaygroundRequest, playgroundRoutes } from './src/playground';

const target = process.env.SERVER_HTTPS || process.env.SERVER_HTTP;
const inferenceKey = process.env.LUCIA_PLAYGROUND_API_KEY;
const playgroundProxy: Record<string, ProxyOptions> = {};
if (inferenceKey) {
  for (const route of playgroundRoutes) {
    playgroundProxy[`^${route.path}(?:\\?.*)?$`] = {
      target,
      changeOrigin: true,
      rewrite: () => route.target,
      headers: { Authorization: `Bearer ${inferenceKey}` },
    };
  }
}

const playgroundBridge: Plugin = {
  name: 'lucia-development-playground',
  apply: 'serve',
  configureServer(server) {
    server.middlewares.use((request, response, next) => {
      if (!request.url?.startsWith('/api/playground')) return next();
      const header = (name: string) => {
        const value = request.headers[name];
        return typeof value === 'string' ? value : undefined;
      };
      const rejection = checkPlaygroundRequest(request.url, request.method ?? '', header('origin'), header('host'),
        header('content-type'), header('x-lucia-playground'))
        ?? (!inferenceKey ? { status: 503, message: 'The temporary inference connection is not configured on this dev server.' } : null);
      if (rejection) {
        server.config.logger.warn(`Playground request rejected (${rejection.status}): ${rejection.message}`);
        response.statusCode = rejection.status;
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ error: { message: rejection.message } }));
        return;
      }
      next();
    });
  },
};

export default defineConfig({
  plugins: [react(), tailwindcss(), playgroundBridge],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    allowedHosts: [hostname()],
    proxy: {
      ...playgroundProxy,
      // Proxy API calls to the app service
      '/api': {
        target,
        changeOrigin: true
      }
    }
  }
});
