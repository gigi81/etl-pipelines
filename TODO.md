# TODO

Follow-up work after the Server/Agent plan in [SERVER.md](SERVER.md) (all nine phases shipped,
released as `v0.1.0`). Each item is a candidate for its own PR. Operator-facing detail on most of
these is in [docs/articles/server/limitations.md](docs/articles/server/limitations.md).

## Verification

- [ ] **Manual smoke test against the built images.** SERVER.md's whole-subsystem verification step 5:
  `docker compose up --build`, then install a sample with `grpcurl`, execute it, watch
  `StreamRunProgress`, and check `Runs`/`StageResults` in Postgres. The Docker end-to-end tests
  cover the same flow, but in-process rather than against the images. The commands are in
  [docs/articles/server/deployment.md](docs/articles/server/deployment.md).

## Security

- [ ] **Authentication and authorization.** Nothing is authenticated today: management clients,
  agents, or pipeline processes. This is the prerequisite for running the Server anywhere other
  than a trusted network. SERVER.md's plan: agents get their own credential, and pipeline processes
  are limited to the run their session id belongs to.
- [ ] **TLS.** gRPC is served over cleartext HTTP/2 (h2c), and bagetter over plain HTTP.
- [ ] **Scope configuration entries.** `GetConfiguration` returns *every* entry to every run, so any
  installed package can read every secret. This needs a schema change: entries scoped per pipeline
  or package, plus a way to set them.
- [ ] **Real secret management.** Data Protection with a key ring on a volume is explicitly not
  production-grade. Make the protector swappable for a KMS.

## Scheduling and agents

- [x] **Install-aware agent selection.** `ExecutePipeline` prefers an agent known to have the version
  (tracked in memory per connection), and an agent without it installs it on demand from the feed
  URLs the work item now carries.
- [ ] **Use `Agents.Tags` for selection.** Tags are recorded at registration and never used.
- [ ] **Concurrent work items per agent.** `AgentRegistration` handles its `Subscribe` stream one
  item at a time, so a long run blocks installs, which time out after 5 minutes, and other runs.
- [ ] **In-process reconnect.** A dropped `Subscribe` stream ends the agent process, which relies on
  `restart: unless-stopped` to come back as a new registration. Reconnect instead, keeping the
  same agent id.
- [ ] **Per-run liveness.** `PipelineExecutionService.Heartbeat` records nothing, so a hung pipeline
  process on a healthy agent stays `Running` forever. It needs a column (e.g.
  `Runs.LastHeartbeatAt`) and a sweep like `AgentLivenessMonitor`'s.
- [ ] **Retry policy (maybe).** `AgentLost` runs are never retried, deliberately, because a partial
  load may not be idempotent. A per-pipeline opt-in retry policy is the planned escape hatch.

## Server state

- [x] **Survive a Server restart.** Agents and runs (in flight and recent, with their stages and
  samples) are loaded from the database on startup; `StreamRunProgress` replays what had been written.
  Still lost: pending `InstallPackage` calls, and the events of the last `LiveState:SyncInterval` after a hard crash.
- [x] **One store.** `LiveState` replaced `RunStatusStore`: gRPC, the REST API and the WebUI all use it.
- [ ] **Bound the outbox.** A long database outage grows the queue of events waiting to be written without
  limit. Cap it, dropping the high-volume resource samples first.
- [ ] **A durable event log (maybe).** Events are replayed into the existing tables, so a status that
  is overwritten inside one sync window leaves only its timestamps. Writing the events themselves
  to an append-only table would keep every transition, and allow rebuilding the state from it.

## Missing API surface

- [x] **`ManagementService.ListAgents`**: reads the `Agents` table.
- [x] **`ManagementService.UninstallPackage`**: marks the versions `Uninstalled`.
- [x] **`ManagementService.UpdatePackage`**: installs the latest stable version and marks older ones
  `Superseded`; in-flight runs are untouched.
- [x] **Wire `ResourceMonitor` in.** The agent samples each pipeline process every
  `Agent:ResourceSampleInterval` and reports `RESOURCE_SAMPLE`.
- [x] **gRPC server reflection**, so `grpcurl` works without the `.proto` files.
- [x] **WebUI** (`web/`): pipelines with a manual **Run now**, runs in flight with live per-stage
  progress and CPU/memory charts, and agents with their OS, IP and uptime. It talks to a REST API
  (`/api`) on a second, HTTP/1.1 port, since browsers cannot call the gRPC services. See
  [docs/articles/server/webui.md](docs/articles/server/webui.md).
- [ ] **WebUI: manage packages and configuration.** It is read-only apart from starting runs. Installing,
  updating and uninstalling packages, and setting configuration entries, are still gRPC-only.
- [ ] **WebUI: a stage-by-stage view for a run that has not reported yet.** Stages are only known once
  they complete, so a pipeline's first run draws no stages ahead of time. The pipeline process could
  report its stage plan when it starts.
- [ ] **Prune stale agents.** Every agent restart registers a new row, so the agents page accumulates
  `Offline` duplicates. Reconnecting under the same agent id (see above) would fix it at the source.

## Caches

- [ ] **Tell the Server about evictions.** `CacheEvictor` deletes versions silently. Runs no longer
  fail because of it (the agent reinstalls on demand), but the Server may keep preferring an agent
  that has to reinstall.
- [ ] **Shared package cache across agents.** Every agent machine downloads and installs its own copy.

## Housekeeping

- [ ] **Prune old GHCR images.** `docker.yml` now publishes only for `v*` tags and `release/**`
  branches, but the per-commit images pushed from `main` before that change are still in the
  registry. Delete them (your profile → Packages → `etl-pipelines/server` and `/agent`), and
  consider `actions/delete-package-versions` to cap `release-*` SHA-tagged versions.
- [ ] **Turn on NuGet publishing** when ready: set the `PUBLISH_TO_NUGET` repository variable to
  `true`, add the `NUGET_USER` secret, and configure a nuget.org trusted-publishing policy for
  `ci.yml`. See "Releasing" in
  [docs/articles/contributing/building-and-testing.md](docs/articles/contributing/building-and-testing.md).
- [ ] **Versioning in practice.** Every contract is `v1` and has room for a `v2`, but the Server,
  the Agent and `EtlPipelines.GrpcClient` have only ever shipped together. Rolling any of them out
  independently hasn't been exercised.
