# WebUI and REST API

The Server ships with a web interface for looking at what is installed and running, and for starting
a run by hand. It is a single-page React app (`web/`, built with bun, Vite and
[Mantine](https://mantine.dev/)) that the Server serves itself, so there is nothing more to deploy:
with the [Docker Compose stack](deployment.md) up, open <http://localhost:5080>.

## What it shows

| Page | |
|---|---|
| **Pipelines** | Every installed pipeline with its package and version, how many runs of it are in flight, how its last run went, and a **Run now** button. Starting a run sends it to an agent, exactly as `ManagementService.ExecutePipeline` does, and opens the run's page. If nothing can take it, such as no agent being connected, the reason is shown. Each pipeline has a **⋯ menu**: *Run history* (the Runs page filtered to it), *Rename*, *Choose agents…* (which agents may run it, see [Which agent runs a pipeline](#which-agent-runs-a-pipeline)), *Go to the package*, and *Clear history…* (deletes its finished runs, after a confirmation; a run in progress is left alone). |
| **Runs** | Two tabs: the runs in flight, with a progress bar, and the finished ones, newest first. It changes as runs start, progress and finish, with no refresh. |
| **Run** | One run, live. The steps are drawn left to right as a flow: each completed stage shows the rows it took in and passed on, the rows that failed, and its elapsed time. The stages still to come are drawn too, from the stages of the pipeline's last successful run, with the running one highlighted. Below are CPU and memory charts, sampled by the agent running the process. A failed run shows the failing stage's error code and description. |
| **Packages** | What the package feed offers and what is installed, one row per package. **Install** a package at its latest stable version or any version the feed has; **Update** one that has a newer stable version (its older versions are retired, runs in flight finish); **Uninstall** one (after a confirmation). **Upload a package** pushes a built `.nupkg` to the feed, after which it is listed here to install. Installing and updating are done by an agent (`dotnet tool install`), so they can take minutes; the row shows it is working. A search box filters by id. If the feed cannot be reached, the installed packages are still listed, with the reason. |
| **Configuration** | The entries pipelines read as configuration - `ConnectionStrings:<name>`, `Sftp:<name>:Host`, and so on. Set or replace one, or delete it. Values are encrypted at rest and **never shown**: the page lists keys and when they were last set. |
| **Agents** | Every agent that has registered: its status, OS and architecture, IP addresses, CPU count, uptime, last heartbeat, version and tags, and the runs it is working on right now. **Edit** its tags to give it labels pipelines can be assigned to. An agent that has stopped responding can be **removed** from its card, or all of them at once (*Remove offline agents*): it stops being listed, and the runs it carried out keep its name. An agent that is online, or still has a run in progress, cannot be removed. |

Nothing in the UI polls. The Server pushes every change to the browser as it happens, and a small
**Live** badge in the header shows the connection is up. If it drops (a Server restart, say), the
badge says *Reconnecting*, the browser retries by itself, and everything is fetched again once it is
back, so nothing that happened in between is missed.

## How updates reach the browser

The Server keeps what the UI shows **in memory** (`LiveState`): it is the one store of agents and runs,
which every gRPC call changes and the API reads, and the database is written behind it a few seconds
later - see [Live state and the database](operations.md#live-state-and-the-database). Reading the API
therefore costs no database query for anything in flight, and nothing polls: a change is applied to
memory, and the listeners are told.

A listener is told *that* something changed and reads the latest state when it gets to it, so a burst
of changes to one run (a stage, a sample, a status) is delivered once, and a slow browser never builds
up a backlog. A listener is removed when its browser tab closes.

Two streams carry the changes, as [server-sent events](https://developer.mozilla.org/docs/Web/API/Server-sent_events):

| Stream | Events |
|---|---|
| `GET /api/events`, one per open tab | `ready` (sent at once; the stream is listening, so refetch), `run` (a run's summary: status, stages completed, rows), `agent` (an agent, with the runs it is working on; sent on every heartbeat too, which keeps "last heartbeat" current), `agent-removed` (an agent is no longer listed), `pipelines` (a package was installed, updated or uninstalled, or a pipeline was renamed or had its history cleared; refetch the lists), and `ping` (a keep-alive every 15 seconds of silence). The UI patches the lists it already has from `run` and `agent` events, and refetches only when a run finishes, a run starts, or on `pipelines`. |
| `GET /api/runs/{id}/stream`, one per open run page | `run`: the run's full detail (stages, expected stages, resource samples) right away and again on every change, until the run has finished; `ping`. |

The gRPC `StreamRunProgress` is served from the same store and the same change notices.

The database is touched on a timer only to write what happened (every `LiveState:SyncInterval`, and
only if something did): not by the UI, and not by the liveness sweep, which reads memory.

A pipeline that has never succeeded has no stages to compare against, so its first run only shows the
stages as they complete. The same goes for the progress bar, which then has no total to fill towards.

## How pipelines are named

A pipeline's name is chosen by whoever writes its package: `AddEtlPipeline("orders-load", ...)`. The
package's `list` verb prints it, the Server records it, and an agent runs the pipeline with
`<tool> run orders-load`. It is the package's own identity for the pipeline, so it can't be changed from
the UI - a package that is updated registers it again under the same name.

What the UI can do is give a pipeline a **display name**: *Rename* on its card. It is how the UI labels
the pipeline - on the card, on its runs, on the run page, in the agents' running jobs - with the pipeline's
own name shown beneath it on the card, so the two are never confused. Display names are stored per
(package, pipeline name), so they survive the package being updated, and reset by saving an empty name.
They exist only in the UI and its REST API (`displayName`); gRPC, the agents and the database's `Pipelines`
table keep using the pipeline's own name.

## Which agent runs a pipeline

By default a run goes to any connected agent. Two things change that, both from the UI.

**Tags on agents.** A tag is a short label such as `gpu`, `eu-west` or `env:prod` (lower case; letters,
digits and `. _ : / -`). An agent has two kinds: the ones it reports about itself, from its `Agent:Tags`
setting, which come back every time it starts; and the ones you give it with *Edit* on its card, which are
kept. Both count when pipelines are matched.

**Where a pipeline may run.** *Choose agents…* in a pipeline's menu takes tags and/or named agents. A
pipeline may then run on an agent that has **any** of the tags, or is one of the named agents. Choosing
nothing at all means any agent again. The dialog shows who that comes to, and warns when no agent that is
online matches; the card shows `Runs on: …` for a pipeline that has a choice. It is kept per
(package, pipeline name), so it survives the package being updated.

When a run starts, the Server picks among the connected agents that are allowed: the one with the fewest
runs going (an agent runs only so many at once, four by default, and the rest wait on it while another
agent might sit idle), and among equally busy ones one that already has the package
installed, then the lowest id so the choice is repeatable. If agents are connected but none is allowed,
the run is refused with the assignment named (`409`), rather than sent somewhere it should not go.
Installing a package is not restricted: it goes to any connected agent, and the agent that runs a pipeline
installs the package on demand.

## Why there is a REST API

A browser cannot call the gRPC services: they are served over cleartext HTTP/2 only, which browsers
do not speak, and gRPC-Web would need a proxy or a second protocol stack. So the Server listens on a
second port, `Server:HttpPort` (5080), over HTTP/1.1, and exposes the same data as minimal APIs under
`/api`. Both ports are served by the same process and the same database.

| Route | |
|---|---|
| `GET /api/info` | `{ "version": ... }`: the version the Server was built as (`0.2.0.5+01d5a8c`: version, commits since it was set, commit), shown at the bottom of the navigation bar. |
| `GET /api/pipelines` | Installed pipelines with `activeRuns` and `lastRun`. |
| `POST /api/pipelines/{id}/runs` | Starts a run. `202` with `{ "runId": ... }` and a `Location` header. `404` for an unknown pipeline, `409` when it cannot be run, which includes no agent being connected. The body is a [problem details](https://www.rfc-editor.org/rfc/rfc9457) with the reason. |
| `GET /api/runs?active=&pipelineId=&limit=` | Runs, newest first. `active=true` keeps those in flight, `active=false` the finished ones. `limit` is 1 to 200, 50 by default. |
| `GET /api/runs/{id}` | One run with its completed `stages`, the `expectedStages` names, and the latest `resourceSamples`. |
| `GET /api/runs/{id}/stream` | The same payload as server-sent events (`event: run`), once on connect and again on every change. |
| `GET /api/events` | The change feed for the lists, as server-sent events. See [How updates reach the browser](#how-updates-reach-the-browser). |
| `GET /api/agents` | Agents with their host details, `tags` (all of them: what the agent reports plus what a user gave it), `assignedTags` (only the latter), `uptimeSeconds` (online agents only) and `activeRuns`. |
| `DELETE /api/agents/{id}` | Removes an agent that has stopped heartbeating, so it is no longer listed. `204`; `404` if unknown; `409` if it is online or still has a run in progress. Its row is kept (`Status = Removed`) so its past runs keep its name; the tags it was given are forgotten. If the agent process is still alive somewhere and registers again, it is the same agent and is listed again. |
| `PUT /api/agents/{id}/tags` | `{ "tags": [...] }`: the tags a user gives the agent, replacing the earlier ones (the ones the agent reports about itself are not touched). Lower-cased; letters, digits and `. _ : / -`, at most 50 characters each and 20 tags. `204`; `400` for an invalid tag; `404` if unknown. |
| `PUT /api/pipelines/{id}/display-name` | `{ "displayName": ... }`: the name the UI shows for the pipeline (at most 100 characters); empty or null goes back to its own. `204`; `404` if unknown. |
| `PUT /api/pipelines/{id}/assignment` | `{ "tags": [...], "agentIds": [...] }`: which agents may run the pipeline. Both empty means any agent. `204`; `400` for an invalid tag or an agent that does not exist; `404` if the pipeline is unknown. `GET /api/pipelines` returns it as `assignment`. |
| `DELETE /api/pipelines/{id}/runs` | Deletes the pipeline's finished runs with their stages and samples: `200` with `{ "deleted": n }`. Runs in progress are not touched. `404` if unknown. |
| `GET /api/packages?search=` | The feed's packages and the installed ones, by id: `versions` (newest first), `latestStableVersion`, `installedVersion`, `updateAvailable`, and the installed version's `pipelines`. If the feed cannot be browsed, `feedError` says why and only the installed packages are listed. Not pushed - fetched when asked. |
| `POST /api/packages/{id}/install?version=` | Installs on a connected agent, at `version` or the latest stable one, and answers when the agent has: `200` with the version and the pipelines it registered. `404` for a package the feed has no version of, `409` when no agent is connected, `502` with the agent's reason when its install fails, `504` if it does not answer within five minutes. |
| `POST /api/packages/{id}/update` | Installs the latest stable version, then retires the older ones. `409` if the package is not installed. Needs no agent when it is already current. |
| `DELETE /api/packages/{id}` | Uninstalls every version: its pipelines are no longer listed or run. `204`; `404` if none is installed. |
| `POST /api/packages/upload` | The `.nupkg` as the raw request body (up to 512 MB). The Server checks it is a .NET tool package and pushes it to the feed: `201` with its `id` and `version`; `400` if it is not a valid package or not a tool; `409` if the feed already has that version; `502` if the feed refuses it. Nothing is installed. |
| `GET /api/configuration` | The keys and when each was last set (`updatedAtUtc`) - never the values. |
| `PUT /api/configuration` | `{ "key": ..., "value": ... }`: creates or replaces an entry. `400` for a key that is not a colon-separated path of letters, digits, `.`, `-` and `_`, or an empty value. |
| `DELETE /api/configuration?key=` | Removes an entry. `204`; `404` if there is none. |

The response shapes are plain records in `src/EtlPipelines.Server.Contracts`; `web/src/api/types.ts`
mirrors them by hand.

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

> [!WARNING]
> **There is no authentication, and this API can run code.** Whoever can reach port 5080 can upload a
> package, have an agent install it and run it - arbitrary code on the agents - and can set the
> configuration pipelines receive. That is why the compose file publishes the port on `127.0.0.1` only.
> Put an authenticating reverse proxy in front of it before exposing it to anyone else. See
> [Known limitations](limitations.md).

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
