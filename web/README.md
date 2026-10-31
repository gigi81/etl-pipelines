# EtlPipelines WebUI

React + TypeScript single-page app for the EtlPipelines Server: installed pipelines (with a manual
**Run now**), runs in flight with live per-stage progress, and agents. Built with
[bun](https://bun.sh), [Vite](https://vite.dev) and [Mantine](https://mantine.dev).

```bash
bun install
bun run dev      # http://localhost:5173, proxies /api to http://localhost:5080
bun run test     # vitest
bun run build    # tsc --noEmit, then vite build into dist/
```

Set `ETL_SERVER_URL` to proxy to a Server that is not on `localhost:5080`.

In production there is nothing to run: `docker/Dockerfile.server` builds this app and the Server
serves `dist/` itself. It talks to the Server's REST API (`/api`); see
[docs/articles/server/webui.md](../docs/articles/server/webui.md) for the routes and how the live view works.

| | |
|---|---|
| `src/api/` | Types mirroring the records in `src/EtlPipelines.Server.Contracts/`, the fetch client, and the react-query / EventSource hooks. |
| `src/pages/` | Pipelines, Runs, Run (live), Agents. |
| `src/components/` | The stage flow, charts, status badge, run table. |
