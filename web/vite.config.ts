/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// `bun run dev` proxies /api to a locally running EtlPipelines.Server (its REST endpoint, port
// 5080 by default). Override with ETL_SERVER_URL, e.g. when the server runs in docker compose.
const server = process.env.ETL_SERVER_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': { target: server, changeOrigin: true } },
  },
  test: { environment: 'node' },
});
