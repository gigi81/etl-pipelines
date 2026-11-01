# gRPC API

The `.proto` files in `protos/v1/` are the source of truth. Each field is commented there, and those
comments are worth reading alongside this page. Everything is served on one cleartext HTTP/2
endpoint (port 5000 by default), with no TLS and no authentication. Server reflection is on, so
`grpcurl -plaintext localhost:5000 list` works without the proto files; passing them
(`-import-path protos -proto v1/management.proto`) still works too.

## ManagementService

`etlpipelines.management.v1.ManagementService`, in `protos/v1/management.proto`. This is the operator
and tooling surface.

| RPC | Status | What it does |
|---|---|---|
| `ListAvailablePackages(search_term)` | ✅ | Searches the configured feed and returns each package id with its versions. An empty `search_term` lists everything. |
| `InstallPackage(package_id, version)` | ✅ | Asks a connected agent to install the package, then returns the recorded `package_version_id` and the pipeline names the package registers. An empty `version` means the latest stable version. The call waits for the agent, up to 5 minutes. |
| `ListInstalledPipelines()` | ✅ | Every pipeline from every successfully installed package version, with the `pipeline_id` that `ExecutePipeline` takes. |
| `ListUpdates()` | ✅ | Each installed package for which the feed has a newer stable version than the one most recently installed. |
| `ExecutePipeline(pipeline_id)` | ✅ | Creates a run, dispatches it to a connected agent, and returns its `run_id` at once, without waiting for the run to finish. |
| `StreamRunProgress(run_id)` | ✅ | A server stream of `StageCompleted` events, then one `RunCompleted`, after which the stream ends. Every stage the run has already completed is sent first, so subscribing late loses nothing. Served from the Server's in-memory state; a finished run older than an hour (no longer held there) is answered from `Runs` and `StageResults`. `NOT_FOUND` for a `run_id` the Server has no record of. |
| `SetConfigurationEntry(key, value)` | ✅ | Creates or replaces one encrypted configuration entry. See [Configuration](configuration.md#pipeline-configuration-entries). |
| `ListAgents()` | ✅ | Every agent that has ever registered (from the in-memory state), with its tags (reported and given), version, `ONLINE`/`OFFLINE` status and last heartbeat. An agent that registers again with its id is the same agent, so a restart does not add one; an old agent that is gone for good can be removed from the WebUI. |
| `UninstallPackage(package_id)` | ✅ | Every installed version of the package stops being listed by `ListInstalledPipelines` and can no longer be executed (`FAILED_PRECONDITION`). Runs already in flight finish; agents' copies are left to cache eviction. `NOT_FOUND` if no version is installed. `InstallPackage` brings a version back. |
| `UpdatePackage(package_id)` | ✅ | Installs the feed's latest stable version on a connected agent, then retires every older installed version, so the package's pipelines are listed and run at the new version only. Runs already in flight are not interrupted. Already on the latest version, it only retires the older ones and needs no agent. `FAILED_PRECONDITION` if the package is not installed. |

### Errors

| Code | When |
|---|---|
| `FAILED_PRECONDITION` | `InstallPackage` or `ExecutePipeline` is called while no agent is connected. |
| `NOT_FOUND` | `InstallPackage` finds no installable version on the feed, or `ExecutePipeline` gets an unknown `pipeline_id`. |
| `INVALID_ARGUMENT` | A `pipeline_id` or `run_id` is not a GUID. |
| `DEADLINE_EXCEEDED` | The agent did not report an install result within 5 minutes. |
| `INTERNAL` | The agent reported that the install failed. The message carries the agent's error. |

### Run status

`RunCompleted.status` and the `Runs.Status` column use the same values:

| Status | Meaning |
|---|---|
| `QUEUED` | Reserved. The Server currently creates runs directly as `DISPATCHED`. |
| `DISPATCHED` | The run was created and its work item sent to an agent. |
| `RUNNING` | The process started (reported by the agent, or implied by the process's first `GetConfiguration` call). |
| `SUCCEEDED` / `FAILED` | The process reported its result or exited. `exit_code` and the row counts are filled in. |
| `AGENT_LOST` | The agent stopped heartbeating while the run was active. Such a run is never retried automatically. See [Operations and reliability](operations.md). |

## AgentService

`etlpipelines.agent_execution.v1.AgentService`, in `protos/v1/agent_execution.proto`. Only agents
call it.

| RPC | What it does |
|---|---|
| `RegisterAgent(machine_name, tags, version, host_info, agent_id)` | Returns the agent's id. An agent registers once each time it starts. With no `agent_id` (its first start, or an older agent) the Server issues a new one; with the `agent_id` the agent was issued before, it is the same agent starting over: its row is updated in place, keeping the tags a user gave it and the runs it carried out (`INVALID_ARGUMENT` if the id is not a GUID). The agent keeps the id in a file in its cache directory. |
| `Heartbeat(agent_id)` | Updates `Agents.LastHeartbeatAt`. Agents call it every 10 seconds, and it is what the liveness sweep watches. |
| `Subscribe(agent_id)` | A long-lived server stream of `WorkItem`s, each either an `InstallPackageWorkItem` or an `ExecutePipelineWorkItem`. The agent counts as connected, and can be dispatched to, for as long as this stream is open. |
| `ReportInstallResult(work_item_id, succeeded, pipeline_names, error)` | Resolves a pending `InstallPackage`. |
| `ReportExecutionStatus(session_id, status, exit_code, ...)` | `STARTED` when the process launches and `EXITED` with its exit code when it ends. While the process runs, the agent also sends `RESOURCE_SAMPLE` (CPU and working set) every `Agent:ResourceSampleInterval`, stored in `AgentResourceSamples`. |

## PipelineExecutionService

`etlpipelines.pipeline_execution.v1.PipelineExecutionService`, in
`protos/v1/pipeline_execution.proto`. It is called from inside a launched pipeline process by
`EtlPipelines.GrpcClient`, which `EtlPipelines.Hosting` wires up. Every request carries the run's
`session_id`, and an unknown one returns `NOT_FOUND`.

| RPC | What it does |
|---|---|
| `GetConfiguration(session_id)` | Returns every configuration entry, decrypted, as a flat `map<string, string>` of `IConfiguration` keys. A launched process calls it first, during host startup, with a 30-second deadline. |
| `ReportStageResult(...)` | Records one `StageResults` row (sequence, name, rows in/out/failed, elapsed time, error) and publishes a `StageCompleted` event. |
| `ReportRunResult(...)` | Settles the run's outcome, exit code and row counts, and publishes `RunCompleted`. |
| `Heartbeat(session_id)` | Validates the session. It does not record anything yet: there is no per-run liveness. |

## Versioning

Each contract's version appears in its file path (`protos/v1/`), in its proto package (`...v1`) and in
its C# namespace (`EtlPipelines.Management.V1`, and so on). A breaking change becomes a new
`protos/v2/` file with its own package, which the Server can host next to `v1`. Methods that `v2`
replaces are marked `option deprecated = true` in `v1` rather than deleted, so pipeline packages that
are already installed, with an older `EtlPipelines.GrpcClient` compiled in, keep working.
