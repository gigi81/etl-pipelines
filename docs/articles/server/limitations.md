# Known limitations

The Server and Agent work end to end: install, execute, configuration pull, progress reporting,
liveness, and cache eviction are all covered by tests against real containers. They are still a first
version, though, and the gaps below are deliberate scope cuts rather than oversights. Each one is a
candidate for a later piece of work.

## Security

- **No authentication or authorization.** Anyone who can reach port 5000 can install packages, run
  pipelines, and read or overwrite configuration. Anyone who can reach port 5080, the WebUI and its
  REST API, can do all of that too - including **uploading a package, installing it on an agent and
  running it, which is arbitrary code execution on the agents** - and can see every run and agent.
  The compose file therefore publishes 5080 on `127.0.0.1` only. Agents are not authenticated either,
  so anything that can reach the Server can register as an agent and receive work.
- **No TLS.** gRPC is served over cleartext HTTP/2, and bagetter over plain HTTP.
- **Configuration entries are global.** Every run receives every entry, so any installed package can
  read every stored secret.
- **Data Protection is not a KMS.** Values are encrypted at rest, but with keys that sit on a volume
  next to the service. See [Configuration](configuration.md#pipeline-configuration-entries).

Run the stack only on a network you trust.

## Scheduling and agents

- **Agent selection only looks at installs.** `ExecutePipeline` prefers a connected agent the Server
  knows has the package version (it installed it, or ran it, since connecting); otherwise any connected
  agent takes it. `InstallPackage` goes to any connected agent. `Agent:Tags` are recorded but not used,
  and load is not taken into account.
- **Installs are per agent.** An agent that receives a run for a package version it does not have
  installs it from the feed before running it, so the first run on a new agent takes longer. Agent
  replicas that share one `agent-cache` volume (as `docker compose up --scale agent=N` does) see each
  other's installs. Agents on separate machines do not.
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

The Server holds agents and runs in memory and writes the database behind it, every
`LiveState:SyncInterval` (5 seconds by default) - see
[Live state and the database](operations.md#live-state-and-the-database). So:

- **A hard crash** (`SIGKILL`, an out-of-memory kill, power loss) **loses what happened since the last
  sync**, up to that interval: some stages or samples, a status change, or a run created in that window.
  A clean stop writes everything first.
- After a restart, runs still in flight carry on and keep reporting, and `StreamRunProgress` replays
  the stages that had been written. Connected agents, and pending `InstallPackage` calls, are
  connection state and are not kept: pending installs fail, and agents reconnect as described above.
- The tables can be up to one sync interval behind what the Server (and its API and WebUI) show.
- If the database is down, the Server keeps serving from memory and queues what is to be written - without
  a limit, so a long outage grows it.

A finished run is kept in memory for an hour; after that it is read from the database.

## Missing API surface

Nothing is missing from `ManagementService` any more. `UninstallPackage` does not delete anything
from agents' caches; their own size cap evicts unused versions eventually.

## Caches

- Evicting a version from an agent's cache does not tell the Server, which may still prefer that agent
  for the version. The run still succeeds; the agent reinstalls the version first.
- There is no cache shared across agents, so every agent machine downloads and installs its own copy
  of each package.

## Versioning

Every contract is `v1` and versioned so a `v2` can be hosted alongside it, but so far the Server,
the Agent and `EtlPipelines.GrpcClient` have always shipped together. Rolling any of them out
independently of the others has not been exercised yet.
