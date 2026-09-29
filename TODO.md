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

- [ ] **Install-aware agent selection.** `InstallPackage` and `ExecutePipeline` both pick *any*
  connected agent (`AgentConnectionRegistry.TryGetAnyConnectedAgentId`). A run sent to an agent
  without the package, or one that evicted it, fails with exit code -1. Either track installs per
  agent and dispatch accordingly, or install on demand when a run arrives.
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

- [ ] **Survive a Server restart.** Connected agents, pending installs and `RunStatusStore` history
  are in memory only. After a restart, `StreamRunProgress` can't replay earlier stages of in-flight
  runs, and pending `InstallPackage` calls fail.
- [ ] **Evict `RunStatusStore` history.** It grows with every run for the lifetime of the process.

## Missing API surface

- [ ] **`ManagementService.ListAgents`**: still `UNIMPLEMENTED`. The data is in the `Agents` table.
- [ ] **`ManagementService.UninstallPackage`**: still `UNIMPLEMENTED`.
- [ ] **`ManagementService.UpdatePackage`**: still `UNIMPLEMENTED`. The intended behaviour per
  SERVER.md: resolve the latest stable version on the feed, and leave in-flight runs on the old one.
- [ ] **Wire `ResourceMonitor` in.** It exists in the Agent, and the Server already stores
  `RESOURCE_SAMPLE` reports in `AgentResourceSamples`, but the agent never starts sampling.
- [ ] **gRPC server reflection**, so `grpcurl` works without the `.proto` files.

## Caches

- [ ] **Tell the Server about evictions.** `CacheEvictor` deletes versions silently, so the catalog
  still lists them as installed.
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
