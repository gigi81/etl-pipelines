## Acrchitecture
Add the following projects:
- EtlPipelines.Server (the GRPC server)
- EtlPipelines.Server.Database (the Entity Framework database layer used by the server, using postgresql)
- EtlPipelines.Agent (the agent that will execute the pipelines in a separate process)
- EtlPipelines.GrpcClient (the GRPC client to communicate with the server, used by the pipelines processes)
- EtlPipelines.Agent.GrpcClient (the GRPC client to communicate with the server, used by the agents)

## Description
The server is a GRPC server that handles ETL (Extract, Transform, Load) processes.
It provides endpoints for clients to retrieve configuration but also for reporting back pipeline execution metrics, status and results.
The server is built using .NET Core and utilizes Entity Framework for database interactions.

The single pipelines are published as NuGet packages that are self-contained and can be run in isolation.
For example the samples projects are an example of how to use the pipelines in a standalone manner.
The server:
- connects to the nuget server to download the nuget pipelines packages
- stores configuration items like database connection strings, secrets, sftp details, etc. in a secure manner
- is responsible for orchestrating the execution of the pipelines and managing their lifecycle
- delegates actual execution of the pipelines processes to one or more agents so that load can be distributed
- provides logging and monitoring capabilities to track the progress and status of the ETL processes
- maintains a local repository of the downloaded nuget packages to avoid downloading them multiple times

## Available pipelines
The server will have apis to:
- provide a list of installed pipelines
- provide a list of updates available for the installed pipelines
- provide a list of available pipelines packages that can be installed from the nuget server
- install a pipeline package
- uninstall a pipeline package
- update a pipeline package

## Install a pipeline package
- a client will send a request to the server to install a specific pipeline
- the server will download the nuget package for the requested pipeline and store it in a cache folder
- the server will extract the nuget package and store the extracted files in a cache folder
- the server will run the `list` command of the pipeline executable (see samples projects)
- the server will store the list of available pipelines in a database for future reference

## Execute a pipeline:
To execute a pipeline, the server will:
- receive a request from a client to execute a specific pipeline
- delegate execution to an agent (the agent can be on the same machine or on a different machine)
- the agent downloads the nuget package from a nuget server and extracts the nuget package for the requested pipeline in a cache folder
- the agent executes the pipeline in a separate process passing as a parameter a session ID and server url
- the pipeline executable will use the EtlPipelines.Client to request from the server any configuration or data needed for the execution
- the pipeline will report its progress and results back to the server using the provided session ID and server URL
- the agent monitors the execution of the process and reports back to th server CPU and memory usage, start and completion as well as any errors encountered during execution

## Docker compose file
Add a `docker` folder to the root of the solution and add a docker compose file that will define the following services:
- server: the GRPC server
- agent: the agent that will execute the pipelines in a separate process
- postgres: the postgresql database used by the server
- nuget: a nuget server to host the pipeline packages (ex. https://www.bagetter.com/)

The docker compose file will include volumes for persisting:
- the database data
- the server cache
- the nuget server data
- the agent cache

## Development strategy
Divide the development into manageable phases. Each phase should have clear objectives and deliverables.
Each phase will consist of the following steps:
- create a feature branch for the phase
- implement the deliverables for the phase including code, tests but excluding documentation
- raise a PR and wait for review and approval
- once the PR is approved, merge the feature branch into the main branch
- move to the next phase

Once all phases are completed, have a final phase to update the documentation to reflect the final state of the project and ensure that all features are properly documented.
For the documentation, consider using a tool like DocFX and create a docs folder in the root of the solution to manage the docset.

-----------------------


# Server/Agent distributed execution subsystem

## Context

Every pipeline today is a library consumed directly by an application that references
`EtlPipelines.Hosting` and runs in-process, on one machine, under whatever credentials that
application has. `SERVER.md` sketches turning this into a distributed system: a central server that
tracks which pipeline packages exist, hands out their configuration (connection strings, SFTP
credentials) without the pipeline author ever seeing the underlying secret, and farms actual execution
out to one or more agent processes so load can be spread across machines.

`SERVER.md` is a rough sketch, not a spec — it names five projects and a rough flow but leaves open
exactly the decisions that determine how much work this is and where the risk sits: how a "pipeline
package" is actually packaged and invoked, how a spawned pipeline process authenticates back to the
server, what "secure" secret storage means with zero existing precedent in this repo, whether the
server ever executes third-party downloaded code itself, and how three very different callers (an
end-user client, an agent, and an arbitrary pipeline process) are supposed to share one undifferentiated
"GRPC client."

This plan resolves those decisions against the codebase as it actually is today, and breaks the result
into the same phase-per-PR shape `SERVER.md`'s own "Development strategy" section asks for — starting
with the one decision everything else depends on: how a pipeline becomes an installable, runnable
package in the first place.

## Decisions already made (do not re-open)

| | |
|---|---|
| Pipeline packaging | **A real `dotnet tool` package** (`PackAsTool`), installed and invoked exactly the way `dotnet tool install`/`dotnet tool run` already work for any .NET CLI tool — not a bespoke format. Proven first, against the existing samples, before anything else is built (Phase 1). |
| Install validation | **Delegated to an agent.** The server never downloads or executes a third-party package itself — it asks an already-registered agent to install it and report back what `list` prints. Keeps the server's own attack surface limited to "orchestrator," never "code runner for arbitrary NuGet content." |
| Secrets at rest | **ASP.NET Core Data Protection** (`Microsoft.AspNetCore.DataProtection`), key ring persisted to a Docker volume. No new external service. Explicitly not production-grade — swappable for a real KMS later, documented as such. |
| Auth/authz | **Deferred entirely** for phase 1. No agent auth, no client auth. The whole stack is not safe to expose beyond a trusted network until its own later phase adds this. |
| Package feed | **A configurable list of NuGet feed URLs**, read from server configuration, defaulting to the local `bagetter` service only. Not hardcoded to one feed — an operator adds `nuget.org` or another private feed by editing configuration, but nothing in phase 1 assumes more than the default. |

## Verified against the existing codebase

- **No precedent for a "runnable, installable pipeline package."** `src/*` projects are packable NuGet
  libraries (`src/Directory.Build.props`: `IsPackable=true`). `samples/*` are runnable console apps but
  explicitly excluded from packing (`samples/Directory.Build.props`: `IsPackable=false`,
  `OutputType=Exe`). `SERVER.md`'s claim that samples model how a pipeline package works doesn't hold
  yet — Phase 1 makes it hold, by turning packing on for them with the `dotnet tool` shape.
- **`PipelineContext.RunId`** (`src/EtlPipelines.Abstractions/Execution/PipelineContext.cs:41`) is
  `Guid.NewGuid()`-generated internally, with no constructor parameter or setter. There is no way today
  for an external system to inject a session id into a run. Two ways to close this: touch `Core`'s
  stable public API to accept one, or correlate externally without touching it. Going with the latter
  (Phase 6) — `Core`/`Abstractions` are widely-referenced packages and this doesn't need to be their
  concern.
- **Telemetry is already built for exactly this.** `src/EtlPipelines.Core/EtlDiagnostics.cs` publishes
  everything through a plain `ActivitySource`/`Meter` named `"EtlPipelines"`
  ("so any OpenTelemetry exporter picks them up without this library depending on one" — its own doc
  comment). The only existing consumer, `src/EtlPipelines.Hosting/PipelineTraceLogger.cs`, is a small
  `ActivityListener` that logs stopped activities. A gRPC-forwarding listener is the same fifteen lines
  with a different sink — no changes needed to `EtlDiagnostics.cs` or `EtlPipeline.cs`.
- **Configuration is already lazy and section-shaped, which is exactly what a remote source needs.**
  `src/EtlPipelines.Sql/ConnectionRegistration.cs` reads `IConfiguration.GetConnectionString(name)` the
  first time a connection opens, not at DI-registration time.
  `src/EtlPipelines.Files.Sftp/SftpConnectionRegistration.cs` does the same against a `Sftp:{name}`
  section. Neither needs to change — a custom `IConfigurationProvider` populated from the server before
  a pipeline's first connection attempt is a drop-in.
- **The recursive-option-read-during-`ConfigureServices` pattern already exists and is exactly what a
  `--session-id`/`--server-url` option needs.** `samples/Samples.Common/SampleWorkspace.cs` adds
  `--work-dir` as a recursive `Option<string?>` on the root command, then reads it off the
  `ParseResult` while services are still being composed (comment: "Read while the container is being
  composed rather than injected from it: the pipeline's own registration needs the file paths, and
  that runs before there is a provider to resolve anything from."). Copy this shape exactly.
- **`EtlPipelines.slnx`** has exactly three top-level folders today (`/src/`, `/samples/`, `/tests/`),
  each with its own `Directory.Build.props` added as a `<File>` next to that folder's `<Project>`
  entries. Two new top-level folders follow the same shape (Phase 2 onward).
- **`CliWrap 3.8.1`** is centrally versioned in `Directory.Packages.props` and used nowhere in the repo
  — confirmed by grep. It's the right fit for shelling out to `dotnet tool install`/`dotnet pack` (both
  Phase 1's own tests and, later, the Agent's installer) instead of raw `Process.Start`.
- **No gRPC, EF Core, ASP.NET Core Web SDK, Data Protection, or NuGet-client package exists anywhere in
  the repo today.** All new dependencies, and central-package-management entries need to be added for
  every one of them.
- **Docker doesn't exist in this repo at all yet** — no `Dockerfile`, no `docker-compose.yml`. Built
  from nothing in Phase 7.
- **CI already packs unconditionally on every run** (`.github/workflows/ci.yml`'s `pack` job runs
  `dotnet pack --configuration Release --output ./packages` with no filtering) but only **pushes** to
  nuget.org on a `v*` tag, via `dotnet nuget push "packages/*.nupkg" ...`. Once samples become
  packable, that push glob needs narrowing to `packages/EtlPipelines.*.nupkg` — otherwise demo/test
  fixtures ship to public nuget.org on the next tagged release. One-line change, called out in Phase 1.

## Phase 1 — Pipeline packaging: real `dotnet tool` packages, proven on the samples

**Goal:** any runnable pipeline application packs, installs, and runs exactly the way `dotnet` itself
already packages and runs CLI tools — `dotnet pack` produces a tool package, `dotnet tool install
--tool-path <dir>` installs it with a real generated shim executable, and that shim is invoked directly
(`list`, `run <name>`, same verbs as today). Sequenced first because every later phase — the Agent's
installer, the Server's install-delegation flow, even how `EtlPipelines.GrpcClient`'s `--session-id`
option gets to the process — assumes this shape already exists. Proven against the existing samples
rather than a new project, since turning packing on for them is cheap and gives six ready-made,
already-tested pipelines to install and run for real.

**New file: `PipelinePackage.props`, at the repo root**, alongside `Directory.Build.props`/
`Directory.Packages.props` (added to the existing `<Folder Name="/Solution Items/">` in
`EtlPipelines.slnx`). Not a `Directory.Build.props` — those apply to every project in a folder
automatically, and not every project under `samples/` is a runnable pipeline (`Samples.Common` is a
shared library the others reference). This file is imported **explicitly**, one line, by each project
that genuinely is one — matching the repo's existing preference for explicit opt-in over folder-wide
magic (`UseSampleWorkspace` is opted into per `Program.cs`, not auto-wired).

```xml
<Project>
  <!--
    Imported by any project that is itself a runnable ETL pipeline application, so it packs and
    installs exactly the way any other dotnet global tool does. `dotnet tool install`, `dotnet tool
    run` and `dotnet tool uninstall` all work against it unmodified - which is also what the Agent's
    own installer turns out to be nothing more than.
  -->
  <PropertyGroup>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName Condition="'$(ToolCommandName)' == ''">$(AssemblyName)</ToolCommandName>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>
```

`PackAsTool` requires `OutputType=Exe`, already the `samples/Directory.Build.props` folder default. It
does **not** require (and should not set) a `RuntimeIdentifier` — a `dotnet tool` package is packed
portable (`tools/<tfm>/any/`), and the OS-specific apphost/shim is generated locally by `dotnet tool
install` on whatever machine installs it, using the SDK already present there. That is exactly what
avoids the RID matrix a self-contained/single-file publish would otherwise force, while still handing
the Agent a normal, directly-runnable executable — a strictly better outcome than the original plan of
invoking `dotnet <dll path>` by hand.

**Applied to six of the seven sample projects** — every one except `Samples.Common` (a shared library,
not itself runnable): `Samples.ArchiveToDatabase`, `Samples.Branching`, `Samples.CsvToDatabase`,
`Samples.CsvToExcel`, `Samples.ExcelToSql`, `Samples.SqlToWorkbook`. Each gets one line added near the
top of its `.csproj`, right after the `Sdk="Microsoft.NET.Sdk"` line:

```xml
<Import Project="$(MSBuildThisFileDirectory)../../PipelinePackage.props" />
```

Each of those six also needs a `<Description>` added — the root `Directory.Build.props` supplies
`Authors`/`Product`/`PackageLicenseExpression`/`RepositoryUrl`, but every existing packable project in
this repo sets its own `<Description>`, and NuGet's own pack-time diagnostics flag a package with none.

**CI change:** narrow `.github/workflows/ci.yml`'s `deploy` job push step from
`dotnet nuget push "packages/*.nupkg" ...` to `dotnet nuget push "packages/EtlPipelines.*.nupkg" ...`
— samples are now packable (so the local, unconditional `pack` job picks them up, which is fine, that
artifact is just build output), but they are demo/test fixtures, not products, and should never be
what a tagged release actually pushes to public nuget.org.

**Tests — the first real subprocess tests in this repo.** Every existing test invokes a sample
in-process (`Program.RunAsync(string[])` called directly — confirmed nowhere in the repo does a test
spawn and read a real OS process). Packaging-as-a-tool is exactly the thing that can only be proven by
actually spawning the installed shim and reading its real stdout/exit code, so this is new territory.
New test project `tests/EtlPipelines.PipelinePackaging.Tests`, built on **`CliWrap`** (the already
centrally-versioned, currently-unused dependency): for at least one sample (`Samples.ArchiveToDatabase`
is the richest — two registered pipelines, a real dependency graph), `dotnet pack` it to a temp
`packages/` folder, `dotnet tool install --tool-path <temp-dir> --add-source <temp-packages-folder>
Samples.ArchiveToDatabase`, then run the installed shim with `list` and assert its stdout is exactly
the two pipeline names (`build-feed`, `archive`) — the same contract
`ListPipelinesHandler`/`Writer.WriteLine` already guarantees today, now proven to survive being
installed as a real tool rather than run via `dotnet run`. Then `run build-feed --work-dir <scratch>`
and assert exit code `0` and the expected output file exists, exactly as
`tests/EtlPipelines.Samples.Tests` already does for the in-process case — this test proves the same
behavior survives packaging, it doesn't re-derive it. Tag the class `[Category("Packaging")]`: this is
slow (a real `dotnet pack`+`dotnet tool install` round-trip, invoking MSBuild and NuGet restore) but
needs no Docker, so it gets its own sequential step in `integration-tests.yml` alongside the existing
per-concern steps, rather than slowing down every fast per-PR run.

**Verification (run these by hand once the phase lands, in addition to the automated test above):**

```bash
dotnet pack samples/Samples.ArchiveToDatabase --configuration Release --output ./packages
dotnet tool install --tool-path ./tool-install-test --add-source ./packages Samples.ArchiveToDatabase
./tool-install-test/samples.archivetodatabase list
./tool-install-test/samples.archivetodatabase run build-feed --work-dir ./scratch
dotnet tool uninstall --tool-path ./tool-install-test Samples.ArchiveToDatabase
```

(`dotnet tool install` conventionally lowercases the shim filename — confirm the exact name empirically
when this phase is implemented rather than trust the casing above; the automated test should assert
whatever that turns out to be rather than hardcode a guess.)

## Solution layout

Two new top-level solution folders, mirroring `/src/`, `/samples/`, `/tests/`:

- **`/server/`** — new `server/Directory.Build.props` (`IsPackable=false`, `OutputType=Exe` as the
  folder default, same shape as `samples/Directory.Build.props`).
    - `server/EtlPipelines.Server/` — the gRPC host (`Microsoft.NET.Sdk.Web`, first project in the repo
      to use it).
    - `server/EtlPipelines.Server.Database/` — EF Core layer, `OutputType` overridden to `Library`.
- **`/agent/`** — new `agent/Directory.Build.props`, same shape.
    - `agent/EtlPipelines.Agent/` — generic host + `BackgroundService`, plain console SDK.
    - `agent/EtlPipelines.Agent.GrpcClient/` — `OutputType` overridden to `Library`.
- **`src/EtlPipelines.GrpcClient/`** stays under the existing `/src/` folder
  (`src/Directory.Build.props`: `IsPackable=true`, `GenerateDocumentationFile=true`) — it's the one
  project of the five a third party's pipeline package actually depends on, the same way it depends on
  `EtlPipelines.Sql` or `EtlPipelines.Hosting` today. It also imports `PipelinePackage.props`? **No** —
  it's a library other pipeline processes reference, not itself a runnable pipeline; only actual
  pipeline applications (samples now, real ones later) import it.

**Only `EtlPipelines.GrpcClient` ships as a library package to NuGet, alongside the existing
`EtlPipelines.*` connectors.** `Server`, `Server.Database`, `Agent`, `Agent.GrpcClient` are internal
deployables (Docker images), not packages anything outside this repo references — leave them
`IsPackable=false` like the folder default.

A new root-level `/docker/` folder (no `.csproj`, not a solution folder) holds `docker-compose.yml`,
`Dockerfile.server`, `Dockerfile.agent`.

### New central package versions

| Package | Consumer | Why |
|---|---|---|
| `Grpc.AspNetCore` | Server | Kestrel + service hosting |
| `Grpc.Net.ClientFactory`, `Grpc.Net.Client`, `Google.Protobuf`, `Grpc.Tools` | GrpcClient, Agent.GrpcClient | typed client codegen via `AddGrpcClient` |
| `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design` | Server.Database | Postgres, `dotnet ef migrations` |
| `Microsoft.AspNetCore.DataProtection` | Server | secrets-at-rest, key ring on the `server-cache` volume |
| `NuGet.Protocol`, `NuGet.Versioning` | Server only | browsing/resolving packages across the configured feed list for `ListAvailablePackages`/`ListUpdates` — metadata only. The Agent needs **no** NuGet-client library at all: per Phase 1, installing a package is just shelling out to `dotnet tool install --tool-path ... --add-source <feed>`, which already does download+extract+shim through the SDK itself. |

## Phase 2 — Scaffolding and the proto surface

Stand up the five server/agent projects (building clean under `TreatWarningsAsErrors=true`, no network
code yet) and the first-cut `.proto` contracts everything else codegens against.

**Three services, not one** — `SERVER.md` conflates every caller into one vague "GRPC client," but
there are three genuinely different trust boundaries:

1. **`pipeline_execution.proto`** → `PipelineExecutionService`, consumed by `EtlPipelines.GrpcClient`
   from *inside an arbitrary third-party pipeline process*. Narrowest surface — authenticated later by
   a single-use session id scoped to exactly one run, never a broader credential.
   ```protobuf
   service PipelineExecutionService {
     rpc GetConfiguration(GetConfigurationRequest) returns (GetConfigurationResponse);
     rpc ReportStageResult(ReportStageResultRequest) returns (Ack);
     rpc ReportRunResult(ReportRunResultRequest) returns (Ack);
     rpc Heartbeat(HeartbeatRequest) returns (Ack);
   }
   message GetConfigurationResponse { map<string, string> entries = 1; } // flat IConfiguration keys, e.g. "ConnectionStrings:sales"
   ```
2. **`agent_execution.proto`** → `AgentService`, consumed by `EtlPipelines.Agent.GrpcClient`. Your own
   infrastructure, a separate process/machine. Server-streaming `Subscribe` so the agent doesn't poll:
   ```protobuf
   service AgentService {
     rpc RegisterAgent(RegisterAgentRequest) returns (RegisterAgentResponse);
     rpc Heartbeat(AgentHeartbeatRequest) returns (Ack);
     rpc Subscribe(SubscribeRequest) returns (stream WorkItem); // InstallPackage | ExecutePipeline
     rpc ReportInstallResult(ReportInstallResultRequest) returns (Ack);
     rpc ReportExecutionStatus(ReportExecutionStatusRequest) returns (Ack); // started/exited/resource sample
   }
   ```
3. **`management.proto`** → `ManagementService`, the end-user/API surface (no auth in phase 1):
   ```protobuf
   service ManagementService {
     rpc ListInstalledPipelines(Empty) returns (ListInstalledPipelinesResponse);
     rpc ListAvailablePackages(ListAvailablePackagesRequest) returns (ListAvailablePackagesResponse);
     rpc ListUpdates(Empty) returns (ListUpdatesResponse);
     rpc InstallPackage(InstallPackageRequest) returns (InstallPackageResponse);
     rpc UninstallPackage(UninstallPackageRequest) returns (Ack);
     rpc UpdatePackage(UpdatePackageRequest) returns (UpdatePackageResponse);
     rpc ExecutePipeline(ExecutePipelineRequest) returns (ExecutePipelineResponse); // returns a run id immediately
     rpc StreamRunProgress(StreamRunProgressRequest) returns (stream RunProgressEvent);
     rpc ListAgents(Empty) returns (ListAgentsResponse);
     rpc SetConfigurationEntry(SetConfigurationEntryRequest) returns (Ack);
   }
   ```

**Tests:** one TUnit smoke test per new test project asserting the generated types exist and the
solution builds. **Verification:** `dotnet build --configuration Release` clean.

## Phase 3 — `EtlPipelines.Server.Database`

The EF Core layer against Postgres, proven in isolation before `Server` depends on it.

```
Packages              Id, NugetPackageId, CreatedAt
PackageVersions        Id, PackageId FK, Version, InstalledAt, Status (Installing/Installed/Failed)
Pipelines               Id, PackageVersionId FK, Name, CreatedAt   -- one row per pipeline WITHIN a package
Agents                 Id, MachineName, Tags, Version, Status, LastHeartbeatAt
Runs                    Id (Guid = the session id), PipelineId FK, AgentId FK nullable,
                        Status (Queued/Dispatched/Running/Succeeded/Failed/AgentLost),
                        RequestedAt, StartedAt, CompletedAt, ExitCode, RowsRead, RowsWritten, RowsFailed
StageResults            Id, RunId FK, Sequence, Name, RowsIn, RowsOut, RowsFailed, ElapsedMs, ErrorCode, ErrorDescription
AgentResourceSamples    Id, RunId FK, AgentId FK, SampledAt, CpuPercent, WorkingSetBytes
ConfigurationEntries    Id, Key (colon-path, e.g. "ConnectionStrings:sales"), EncryptedValue (bytea), UpdatedAt
NuGetFeeds              Id, Url, Ordinal   -- the configurable feed list, seeded with the local bagetter URL
```

`ConfigurationEntries` is a flat key-value table mirroring `IConfiguration`'s own colon-path shape
deliberately — it's what lets `GetConfigurationResponse.entries` (Phase 2) feed straight into a
`ConfigurationProvider` with zero translation and zero knowledge of any connector's section shape
(Phase 6). `Pipelines` is a child of `PackageVersions`, not `Packages` directly, because
`Samples.ArchiveToDatabase` already proves one package can register more than one pipeline
(`build-feed` and `archive`) — `ListInstalledPipelines`/`ExecutePipeline` both operate at pipeline-name
granularity, matching how `PipelineRunner.Find(name)`
(`src/EtlPipelines.Hosting/PipelineRunner.cs`) already does a linear scan by name within one process.

**Tests:** fast TUnit tests against EF Core's SQLite/in-memory provider for mapping/query logic;
`[Category("Docker")]` + `Testcontainers.PostgreSql` (already centrally versioned) for a real migration
+ round-trip, following `DatabaseFixture<TContainer>`
  (`tests/EtlPipelines.Sql.Databases.Tests/DatabaseFixture.cs`) and its
  `[ClassDataSource<T>(Shared = SharedType.PerAssembly)]` pairing exactly.

**Verification:** `dotnet ef migrations add InitialCreate -p server/EtlPipelines.Server.Database`,
Docker-tagged tests green locally.

## Phase 4 — `EtlPipelines.Server`: catalog and management API (no agent yet)

`ManagementService.ListAvailablePackages`/`ListInstalledPipelines`/`ListUpdates`/
`SetConfigurationEntry` working end to end against `Server.Database` and the real feed list, for
*metadata only*. `InstallPackage`/`ExecutePipeline` stubbed to fail with "no agents available" until
Phase 5.

**Key types:** `NuGetFeedClient` (wraps `NuGet.Protocol`'s search/find-package resources across every
URL in the `NuGetFeeds` table — browse only, no download here), `PackageCatalogService`,
`SecretsStore` (wraps `IDataProtector` + `ConfigurationEntries`, `Protect`/`Unprotect` on
write/read — key ring via `PersistKeysToFileSystem` on the path that becomes the `server-cache` volume
in Phase 7).

**Docker Compose:** add `postgres` and `nuget` (bagetter) services with named volumes.

**Tests:** fast tests with a mocked `NuGetFeedClient`/`IDataProtector`; `[Category("Docker")]` tests
for the full `ManagementService` surface against a real Postgres container.

**Verification:** `docker compose up postgres nuget -d`, `grpcurl` against `ListAvailablePackages`
pointed at a test package pushed to bagetter.

## Phase 5 — `EtlPipelines.Agent` / `EtlPipelines.Agent.GrpcClient`: registration and install

An agent that registers, holds `AgentService.Subscribe` open, receives an `InstallPackage` work item,
installs it, and reports pipeline names back — closing the install loop. Considerably simpler than it
would otherwise be, because Phase 1 already settled what a pipeline package *is*: installing one is
nothing but

```
dotnet tool install --tool-path <agent-cache>/<packageId>/<version> --add-source <feed-url(s)> <packageId> --version <version>
```

run via **`CliWrap`**, exactly like Phase 1's own packaging tests do. No manual `.nupkg` download, no
`System.IO.Compression.ZipFile` extraction, no `NuGet.Packaging` — the `dotnet` SDK already does all of
that, on every platform, more robustly than a hand-rolled version would. The installed shim's path is
then computed the same way Phase 1's verification did (`<agent-cache>/<packageId>/<version>/<toolCommandName>`),
and `list` is invoked directly against it.

**Key types:** `AgentRegistration` (`BackgroundService` holding the `Subscribe` stream),
`PackageInstaller` (wraps the `dotnet tool install` invocation above), `PipelineProcessRunner` (runs
the installed shim with `list`/`run <name> --session-id ... --server-url ...`, output parsed one
pipeline name per line for `list` — matching `ListPipelinesHandler`'s deliberate `Writer.WriteLine`,
not-the-logger, output), `ResourceMonitor` (`Process.TotalProcessorTime`/`WorkingSet64` sampled on a
timer, no new dependency).

**Docker Compose:** add `agent` service with an `agent-cache` volume.

**Tests:** fast tests for `PackageInstaller`/`ResourceMonitor` against a local test package (reuse
Phase 1's packaged sample — no new fixture needed); `[Category("Docker")]` end-to-end: a real bagetter
container seeded with a sample packed in Phase 1, real `Server`+`Server.Database` (Postgres container),
real `Agent` — assert `ListInstalledPipelines` reports the sample's pipeline names after
`InstallPackage`.

**Verification:** `docker compose up -d`, `grpcurl ... InstallPackage`, poll `ListInstalledPipelines`.

## Phase 6 — Execute a pipeline end to end

`ExecutePipeline` → agent dispatch → process launch with a session id and server URL → the pipeline
process pulling its configuration and reporting results back through `EtlPipelines.GrpcClient` →
`Server` persisting `Runs`/`StageResults`. The phase that actually touches Core/Hosting — and, per the
verified findings above, touches them by reuse, not by change:

- **Progress forwarding** — `GrpcProgressReporter : IDisposable` in `EtlPipelines.GrpcClient`, copying
  `PipelineTraceLogger`'s `ActivityListener` subscription to `EtlDiagnostics.SourceName` exactly, but
  calling `ReportStageResult`/`ReportRunResult` from `ActivityStopped` instead of logging. Zero changes
  to `EtlDiagnostics.cs`/`EtlPipeline.cs`.
- **Configuration pull** — `GrpcConfigurationProvider`/`GrpcConfigurationSource` calls
  `GetConfiguration(session_id)` once at startup and materializes the flat map into `IConfiguration`.
  `ConnectionRegistration`/`SftpConnectionRegistration` read connection strings and `Sftp:{name}`
  sections lazily on first connect either way — this provider is a drop-in, zero changes to
  `EtlPipelines.Sql`/`EtlPipelines.Files.Sftp`.
- **Session id** — `--session-id`/`--server-url` added as recursive options via a new
  `EtlPipelinesHost.UseGrpcClient(...)` extension, built exactly like
  `SampleWorkspace.UseSampleWorkspace` (`samples/Samples.Common/SampleWorkspace.cs`): declared on the
  root command, read off the `ParseResult` during `ConfigureServices`, before any provider exists to
  inject from. `Runs.Id` (the server-issued session id) is the primary key everywhere in the schema;
  `PipelineContext.RunId` — still internally generated, untouched — rides along as a secondary,
  diagnostic-only field on the first report call. `Core`'s public API gets no changes.

**Key types:** `GrpcConfigurationProvider`/`GrpcConfigurationSource`, `GrpcProgressReporter`,
`EtlPipelinesHost.UseGrpcClient(...)`, server-side `RunDispatcher` (turns `ExecutePipelineRequest` into
a `WorkItem` on an agent's `Subscribe` stream), `RunStatusStore`.

**Tests:** fast tests for `GrpcConfigurationProvider` against a mocked
`PipelineExecutionServiceClient`; fast tests for `GrpcProgressReporter` against a real
`ActivitySource`. **The full-stack `[Category("Docker")]` end-to-end test belongs here, not later** —
this is the first phase where catalog, agent, process launch, config pull, and progress reporting all
exist simultaneously; deferring it risks a wiring mistake between phases going uncaught. Take one of
Phase 1's already-packaged samples (`Samples.CsvToDatabase` — already proven against five real database
engines), add a `ProjectReference` to `EtlPipelines.GrpcClient` for this test build, push to bagetter,
install, `ExecutePipeline`, poll `StreamRunProgress`, assert `Runs`/`StageResults` match what
`tests/EtlPipelines.Samples.Tests` already expects for that sample.

**Verification:** the E2E test above; manual smoke test with `grpcurl` + `docker compose logs -f`.

## Phase 7 — Docker Compose hardening and image build

`SERVER.md`'s compose file, finished: `Dockerfile.server`, `Dockerfile.agent`, all four volumes, health
checks, restart policies. Sequenced after Phase 6 deliberately — building images against a still-moving
gRPC surface means rebuilding every phase; doing it once here is cheaper.

```yaml
services:
  postgres: { image: postgres:18, volumes: [postgres-data:/var/lib/postgresql/data] }
  nuget:    { image: bagetter/bagetter, volumes: [nuget-data:/data] }
  server:   { build: ./Dockerfile.server, depends_on: [postgres, nuget], volumes: [server-cache:/var/lib/etlpipelines/server-cache] }
  agent:    { build: ./Dockerfile.agent, depends_on: [server], volumes: [agent-cache:/var/lib/etlpipelines/agent-cache] }
```

`server-cache` also holds the Data Protection key ring — it must be a durable volume, since losing it
makes every encrypted `ConfigurationEntries.EncryptedValue` unrecoverable.

**Verification:** `docker compose up --build`, all four containers healthy, Phase 6's smoke test
re-run entirely against the built images.

## Phase 8 — Reliability: heartbeat, crash recovery, cache eviction

The operational gaps `SERVER.md` never addresses, made concrete now that the happy path is proven:

- **Agent liveness** — `Agents.LastHeartbeatAt` plus a `Server`-side `BackgroundService` marking an
  agent `Offline` after N missed heartbeats, and any `Runs` it owned `AgentLost` rather than stuck
  `Running` forever.
- **Crash mid-run is not auto-retried.** A `Run` marked `AgentLost` stays there — retrying a partially
  applied load without knowing whether it's idempotent would be actively dangerous. Surface it for an
  operator to act on; a per-pipeline retry policy is a later phase's problem, not this one's.
- **Cache eviction** — `agent-cache` and `server-cache` both grow unboundedly through Phase 7. Add an
  LRU sweep on a timer, keyed on installed-tool last-access time, with a configurable size cap.

**Tests:** fast tests for the heartbeat timeout (fake clock) and the LRU sweep; a `[Category("Docker")]`
test that kills an agent container mid-run and asserts the server marks the run `AgentLost` within the
timeout window.

## Phase 9 — Documentation

Per `SERVER.md`'s own closing instruction: DocFX, a `docs/` folder, README updates. Last, because
documenting a still-moving target is wasted effort.

## Other gaps worth stating, not solving now

- **What "update" means** is never defined by `SERVER.md`. Resolved here: `UpdatePackage` always
  resolves to the latest stable version across the configured feeds (`InstallPackage` already takes an
  explicit version for pinning). A `Run` already in flight against the old version is **not**
  interrupted — old tool installs are kept on disk until nothing references them.
- **No protocol/schema versioning** between `Server` and an older `Agent`/pipeline-process binary. Fine
  for phase 1 (everything built and deployed together) — flagged for whichever later phase starts
  rolling these out independently.
- **Every agent installs its own copy of a package** — the install-delegation decision removes
  double-installation on the *install-validation* path (one agent does it once), but execution-time
  installation still has no cache shared across agents. Deferred; a shared cache volume or server-side
  proxying is a later phase's concern, not a blocker to a working system.

## Verification (whole subsystem)

1. `dotnet build --configuration Release` clean across all new projects, `TreatWarningsAsErrors=true`.
2. Fast TUnit suite (`--treenode-filter "/*/*/*/*[Category!=Docker]"`) green.
3. Packaging suite (`[Category("Packaging")]`) and Docker-tagged suite green locally with Docker
   running.
4. `docker compose -f docker/docker-compose.yml up --build`: all four containers healthy.
5. End-to-end manual smoke test: install a real sample package via `grpcurl`, execute it, watch
   `StreamRunProgress`, confirm `Runs`/`StageResults` in Postgres match the sample's own expected
   `PipelineResult` (cross-checked against `tests/EtlPipelines.Samples.Tests`).