# WebUI and REST API

The Server ships with a web interface for looking at what is installed and running, and for starting
a run by hand. It is a single-page React app (`web/`, built with bun, Vite and
[Mantine](https://mantine.dev/)) that the Server serves itself, so there is nothing more to deploy:
with the [Docker Compose stack](deployment.md) up, open <http://localhost:5080>.

## What it shows

| Page | |
|---|---|
| **Pipelines** | Every installed pipeline with its package and version, how many runs of it are in flight, how its last run went, and a **Run now** button. Starting a run sends it to an agent, exactly as `ManagementService.ExecutePipeline` does, and opens the run's page. If nothing can take it, such as no agent being connected, the reason is shown. |
| **Runs** | Two tabs: the runs in flight, with a progress bar, and the finished ones, newest first. The *Running* tab refreshes every two seconds. |
| **Run** | One run, live. The steps are drawn left to right as a flow: each completed stage shows the rows it took in and passed on, the rows that failed, and its elapsed time. The stages still to come are drawn too, from the stages of the pipeline's last successful run, with the running one highlighted. Below are CPU and memory charts, sampled by the agent running the process. A failed run shows the failing stage's error code and description. |
| **Agents** | Every agent that has registered: its status, OS and architecture, IP addresses, CPU count, uptime, last heartbeat, version and tags, and the runs it is working on right now. |

The run page updates by itself: the browser holds a server-sent-events stream open
(`/api/runs/{id}/stream`) and the Server pushes the run's full state whenever a stage completes, a
resource sample arrives or the status changes. The stream ends when the run does.

A pipeline that has never succeeded has no stages to compare against, so its first run only shows the
stages as they complete. The same goes for the progress bar, which then has no total to fill towards.

## Why there is a REST API

A browser cannot call the gRPC services: they are served over cleartext HTTP/2 only, which browsers
do not speak, and gRPC-Web would need a proxy or a second protocol stack. So the Server listens on a
second port, `Server:HttpPort` (5080), over HTTP/1.1, and exposes the same data as minimal APIs under
`/api`. Both ports are served by the same process and the same database.

| Route | |
|---|---|
| `GET /api/pipelines` | Installed pipelines with `activeRuns` and `lastRun`. |
| `POST /api/pipelines/{id}/runs` | Starts a run. `202` with `{ "runId": ... }` and a `Location` header. `404` for an unknown pipeline, `409` when it cannot be run, which includes no agent being connected. The body is a [problem details](https://www.rfc-editor.org/rfc/rfc9457) with the reason. |
| `GET /api/runs?active=&pipelineId=&limit=` | Runs, newest first. `active=true` keeps those in flight, `active=false` the finished ones. `limit` is 1 to 200, 50 by default. |
| `GET /api/runs/{id}` | One run with its completed `stages`, the `expectedStages` names, and the latest `resourceSamples`. |
| `GET /api/runs/{id}/stream` | The same payload as server-sent events (`event: run`), once on connect and again on every change. |
| `GET /api/agents` | Agents with their host details, `uptimeSeconds` (online agents only) and `activeRuns`. |

Every timestamp in the API is ISO 8601 in UTC, ending in `Z`, and its name ends in `Utc`
(`requestedAtUtc`, `startedAtUtc`, `completedAtUtc`, `sampledAtUtc`, `lastHeartbeatAtUtc`), so no client
has to guess a zone or convert. The Server enforces it when it serializes, whatever the database
returns.

Anything else under `/api` is a `404`.

The agent's host details come from its registration, and are stored as one JSON document
(`Agents.Info`, a `jsonb` column) rather than a column each, so a new detail needs no schema change.
`RegisterAgentRequest.host_info` is a free-form `google.protobuf.Struct`, which the Server stores
as it is, and `GET /api/agents` returns it as `info`. Today the agent sends:

```json
{
  "osDescription": "Ubuntu 24.04.5 LTS",
  "osArchitecture": "X64",
  "processorCount": 4,
  "ipAddresses": ["192.168.1.20", "2001:db8::20"],
  "startedAtUtc": "2026-10-31T08:36:59.640Z"
}
```

`ipAddresses` is an array: a host commonly has IPv4 and IPv6 addresses, and several of each.
`uptimeSeconds` is worked out from `startedAtUtc`. An agent reports every timestamp the same way:
UTC, with the `Utc` postfix on the key. To report something more, add a key in
`src/EtlPipelines.Agent/HostInfo.cs`: the Server passes it through, and the agents page lists any key
it has no dedicated row for. Agents registered before this existed have no `info`, and show `-`.

> **There is no authentication.** Port 5080 can start any installed pipeline. Like the gRPC port, it
> belongs on a trusted network. See [Known limitations](limitations.md).

## Developing the UI

You need [bun](https://bun.sh). With a Server running locally (`dotnet run --project
src/EtlPipelines.Server`, which needs Postgres; the compose stack's `postgres` service will do):

```bash
cd web
bun install
bun run dev
```

The dev server on <http://localhost:5173> proxies `/api` to `http://localhost:5080`. Set
`ETL_SERVER_URL` to point it somewhere else, for example a Server running in compose.

```bash
bun run test        # unit tests (vitest)
bun run build       # typecheck, then a production build into web/dist
```

The Docker image builds the UI itself (`docker/Dockerfile.server`) and copies it into the Server's
`wwwroot/`. A Server run without a `wwwroot/index.html`, such as a plain `dotnet run`, has no UI and
answers `/` with a short text message; the REST API is there either way.
