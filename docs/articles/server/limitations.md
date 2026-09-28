# Known limitations

The Server and Agent work end to end: install, execute, configuration pull, progress reporting,
liveness, and cache eviction are all covered by tests against real containers. They are still a first
version, though, and the gaps below are deliberate scope cuts rather than oversights. Each one is a
candidate for a later piece of work.

## Security

- **No authentication or authorization.** Anyone who can reach port 5000 can install packages, run
  pipelines, and read or overwrite configuration. Agents are not authenticated either, so anything
  that can reach the Server can register as an agent and receive work.
- **No TLS.** gRPC is served over cleartext HTTP/2, and bagetter over plain HTTP.
- **Configuration entries are global.** Every run receives every entry, so any installed package can
  read every stored secret.
- **Data Protection is not a KMS.** Values are encrypted at rest, but with keys that sit on a volume
  next to the service. See [Configuration](configuration.md#pipeline-configuration-entries).

Run the stack only on a network you trust.

## Scheduling and agents

- **Agents are picked arbitrarily.** `InstallPackage` and `ExecutePipeline` each go to *any*
  connected agent. `Agent:Tags` are recorded but not used for selection, and neither load nor what an
  agent already has installed is taken into account.
- **Installs are per agent.** A package is installed on the one agent that received the
  `InstallPackage` work item. If a later run is dispatched to an agent that does not have it, the run
  fails with exit code `-1`. Agent replicas that share one `agent-cache` volume (as
  `docker compose up --scale agent=N` does) see each other's installs. Agents on separate machines do
  not. Until agent selection is aware of installs, run one agent per cache.
- **One work item at a time per agent.** An agent handles its `Subscribe` stream sequentially, so a
  long run holds up every install and run queued behind it on that agent. An `InstallPackage` stuck
  behind a run longer than five minutes times out.
- **Agents do not reconnect by themselves.** If the `Subscribe` stream drops, for example because the
  Server restarted, the agent process exits and relies on its restart policy
  (`restart: unless-stopped` in compose) to come back. It then registers as a new agent with a new id.
  The old id is marked `Offline` by the liveness sweep.
- **No per-run liveness.** Liveness is tracked per agent. A pipeline process that hangs, never
  exiting and never reporting, while its agent keeps heartbeating stays `Running` indefinitely.
  `PipelineExecutionService.Heartbeat` exists, but nothing records it yet.
- **Nothing is retried.** A run that ends `AgentLost` stays that way (see
  [Operations and reliability](operations.md#agentlost-runs-are-never-retried)). There is no
  per-pipeline retry policy.

## State kept in memory

The Server keeps connected agents, pending installs, and each run's progress history in memory. After
a Server restart:

- runs that were still in flight keep going and can still report their results, because those go
  straight to the database, but `StreamRunProgress` for them no longer replays their earlier stages;
- pending `InstallPackage` calls fail;
- agents reconnect as described above.

Progress history is also never evicted while the process runs, so it grows with the number of runs
since the last restart.

## Missing API surface

- `ManagementService.ListAgents`, `UninstallPackage` and `UpdatePackage` return `UNIMPLEMENTED`. To
  see agents, query the `Agents` table. To update, install the newer version explicitly.
- `ReportExecutionStatus` accepts `RESOURCE_SAMPLE` (CPU and memory for a run), but the agent does not
  send samples yet. The `ResourceMonitor` that would produce them exists but is not wired in.
- gRPC server reflection is not enabled.

## Caches

- Evicting a version from an agent's cache does not tell the Server, and a run dispatched against an
  evicted version fails instead of reinstalling it. Install it again to recover.
- There is no cache shared across agents, so every agent machine downloads and installs its own copy
  of each package.

## Versioning

Every contract is `v1` and versioned so a `v2` can be hosted alongside it, but so far the Server,
the Agent and `EtlPipelines.GrpcClient` have always shipped together. Rolling any of them out
independently of the others has not been exercised yet.
