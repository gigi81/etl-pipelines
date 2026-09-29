# Server/Agent distributed execution subsystem

## Context

Every pipeline today is a library consumed directly by an application that references
`EtlPipelines.Hosting` and runs in-process, on one machine, under whatever credentials that
application has. This plan turns that into a distributed system: a central server that tracks which
pipeline packages exist, hands out their configuration (connection strings, SFTP credentials) without
the pipeline author ever seeing the underlying secret, and farms actual execution out to one or more
agent processes so load can be spread across machines.

Getting there means resolving the decisions that determine how much work this is and where the risk
sits: how a "pipeline package" is actually packaged and invoked, how a spawned pipeline process
authenticates back to the server, what "secure" secret storage means with zero existing precedent in
this repo, whether the server ever executes third-party downloaded code itself, and how three very
different callers (an end-user client, an agent, and an arbitrary pipeline process) are supposed to
share one undifferentiated "GRPC client."

This plan resolves those decisions against the codebase as it actually is today, and breaks the result
into one phase per PR — implement a phase's deliverables (code and tests, not documentation), raise a
PR, get it reviewed and merged, move to the next phase — starting with the one decision everything else
depends on: how a pipeline becomes an installable, runnable package in the first place. A final phase,
once every other phase has landed, brings the documentation up to date with the finished system (Phase 9).

> **Status: every phase has shipped.** This file is now the design record — the decisions, and the
> reasoning behind them — rather than a to-do list. Each phase below keeps its plan as written and
> ends with an **As shipped** note saying what actually landed, where it differs from the plan, and
> why. The user-facing documentation (architecture, deployment, configuration, the gRPC API,
> operations, and known limitations) lives in [`docs/`](docs/index.md), built with DocFX; this file
> does not try to duplicate it.

## Decisions already made (do not re-open)

| | |
|---|---|
| Pipeline packaging | **A real `dotnet tool` package** (`PackAsTool`), installed and invoked exactly the way `dotnet tool install`/`dotnet tool run` already work for any .NET CLI tool — not a bespoke format. Proven first, against the existing samples, before anything else is built (Phase 1). |
| Install validation | **Delegated to an agent.** The server never downloads or executes a third-party package itself — it asks an already-registered agent to install it and report back what `list` prints. Keeps the server's own attack surface limited to "orchestrator," never "code runner for arbitrary NuGet content." |
| Secrets at rest | **ASP.NET Core Data Protection** (`Microsoft.AspNetCore.DataProtection`), key ring persisted to a Docker volume. No new external service. Explicitly not production-grade — swappable for a real KMS later, documented as such. |
| Auth/authz | **Deferred entirely** for phase 1. No agent auth, no client auth. The whole stack is not safe to expose beyond a trusted network until its own later phase adds this. |
| Package feed | **The local `bagetter` service, always.** Every package install goes through it — never straight to nuget.org — and out of the box it holds only the packed samples (Phase 1's `pack` output pushed to it, nothing else). `bagetter` itself supports proxying an upstream feed; turning that on so it also serves nuget.org packages (or another private feed) transparently through the same one endpoint is an operator's `bagetter`-side configuration choice, not something `Server` itself is aware of or needs a feed *list* for. `NuGetFeeds` (Phase 3) accordingly holds one row, not a list — see that phase for what this simplifies away. |
| Solution layout | **All code stays under `src/` and `tests/`.** `server`/`agent` are `.slnx` solution-folder groupings, the same shape `extensions`/`samples` already have — not physical top-level directories. See "Solution layout," below. |
| gRPC API versioning | **Versioned from the start, `v1`.** Every proto's `package` and every generated namespace carries a version segment before a single client exists to break — see "API versioning," below. |
| Database schema management | **[`dbdeploy`](https://github.com/gigi81/dbdeploy), not EF Core Migrations.** Schema and priming/seed data are owned by versioned `.Deploy.sql`/`.Rollback.sql` scripts, deployed by the `dbdeploy` CLI — never by `dotnet ef migrations`. EF Core in `Server.Database` is a query/mapping layer over a schema it does not own: no `Migrations` folder, no `dotnet ef database update`. See Phase 3, below. |

## Status: what shipped

| Phase | PR | As shipped, in one line |
|---|---|---|
| 1 — Pipeline packaging | [#5](https://github.com/gigi81/etl-pipelines/pull/5) | Samples pack as `dotnet tool`s by naming convention (`src/Samples.Build.props`), have descriptions, are excluded from the NuGet push, and `tests/EtlPipelines.PipelinePackaging.Tests` proves a real pack/install/run round trip. |
| 2 — Scaffolding and protos | [#6](https://github.com/gigi81/etl-pipelines/pull/6) | Five server/agent projects and the three `v1` protos, compiled once in a shared `EtlPipelines.Protos` project (added in Phase 5). |
| 3 — Server database | [#7](https://github.com/gigi81/etl-pipelines/pull/7) | `db/` owned by dbdeploy; the EF Core model is reverse-engineered with `dotnet ef dbcontext scaffold`, not hand-written. |
| 4 — Catalog and management API | [#8](https://github.com/gigi81/etl-pipelines/pull/8) | `ListAvailablePackages`, `ListInstalledPipelines`, `ListUpdates`, `SetConfigurationEntry` against Postgres and bagetter. |
| 5 — Agent registration and install | [#9](https://github.com/gigi81/etl-pipelines/pull/9) | Agents register, subscribe, install with `dotnet tool install`, and report pipeline names; `InstallPackage` works end to end. |
| 6 — Execute end to end | [#10](https://github.com/gigi81/etl-pipelines/pull/10), [#11](https://github.com/gigi81/etl-pipelines/pull/11) | `ExecutePipeline` → agent → process → `GetConfiguration`/`ReportStageResult`/`ReportRunResult` → `Runs`/`StageResults`, with `StreamRunProgress`. |
| 7 — Docker Compose and images | [#12](https://github.com/gigi81/etl-pipelines/pull/12) | Real images, health checks, restart policies, schema deployed on start, a one-shot `seed` service, GHCR publishing. |
| 8 — Reliability | [#14](https://github.com/gigi81/etl-pipelines/pull/14) | `AgentLivenessMonitor` (offline + `AgentLost`, never retried) and the agent's LRU `CacheEvictor`. |
| 9 — Documentation | [#15](https://github.com/gigi81/etl-pipelines/pull/15) | DocFX site under `docs/`, README, and this file brought in line with the above. |

What is still open is listed in "Other gaps worth stating, not solving now," at the end of this file,
and, from an operator's point of view, in [`docs/articles/server/limitations.md`](docs/articles/server/limitations.md).

## Verified against the existing codebase

> **Historical.** This section is the pre-implementation survey the phases below were planned
> against. It is kept because the phases refer back to it, but the gaps it describes have all since
> closed — each bullet that described one says where. For the codebase as it is now, read the code
> and [`docs/`](docs/index.md).

> Re-verified against `main` as of `41e8a4f` (well past `bfca455`, the commit this analysis was
> originally written against). The repo has moved a fair distance since: connector libraries and
> their test projects were renamed to `EtlPipelines.Extensions.*`, samples moved from a top-level
> `/samples/` folder into `src/` (`EtlPipelines.Samples.*`), a new `EtlPipelines.Extensions.Cli`
> connector and an `EtlPipelines.Extensions.Json` connector landed, and — most relevant here — **Phase
> 1's core idea already shipped**, in a lighter-weight form than planned below. Every bullet below is
> current as of that commit; anywhere it corrects an earlier version of itself, that is called out
> rather than silently edited away.

- **Samples already pack as `dotnet tool` packages — Phase 1 shipped, just not as planned.** A single
  commit ("Publishing samples as dotnet tools") replaced the old
  `samples/Directory.Build.props` (`IsPackable=false`, `OutputType=Exe`) with a new
  `src/Samples.Build.props` — imported unconditionally from `src/Directory.Build.props`, applying
  `<OutputType>Exe</OutputType>` and **`<PackAsTool>true</PackAsTool>`** to every project whose name
  starts with `EtlPipelines.Samples.` — rather than this plan's proposed root-level
  `PipelinePackage.props` imported explicitly per project. Net effect is the same shape Phase 1 wanted
  (every sample installs and runs like any other `dotnet` global tool) reached by a shorter path: a
  naming-convention condition instead of an opt-in import line. See "As actually implemented," at the
  end of Phase 1 below, for what this means for the rest of that phase's plan — in particular, **the
  CI push-glob narrowing this section used to only warn about is now a live gap**, not a hypothetical
  one: see the last bullet here.
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
  `src/EtlPipelines.Extensions.Sql/ConnectionRegistration.cs` reads
  `IConfiguration.GetConnectionString(name)` the first time a connection opens, not at
  DI-registration time. `src/EtlPipelines.Extensions.Files.Sftp/SftpConnectionRegistration.cs` does the
  same against a `Sftp:{name}` section. Neither needs to change — a custom `IConfigurationProvider`
  populated from the server before a pipeline's first connection attempt is a drop-in. (Paths corrected
  from `EtlPipelines.Sql`/`EtlPipelines.Files.Sftp` — both connectors were renamed to
  `EtlPipelines.Extensions.*` since this was first written; the code itself is unchanged.)
- **The recursive-option-read-during-`ConfigureServices` pattern this plan wanted to copy is no longer
  sample-only code to imitate — it has moved into `EtlPipelines.Hosting` itself, which is a better
  precedent, not a worse one.** The `samples/Samples.Common/SampleWorkspace.cs` this bullet used to cite
  no longer exists — the project it lived in was folded away entirely — but the pattern it demonstrated
  survived by graduating into the framework: `EtlPipelinesHost` (`src/EtlPipelines.Hosting/EtlPipelinesHost.cs`)
  now declares `VerboseOption`/`WorkDirOption` as `static Option<T>` properties, adds them to the root
  command as `Recursive = true` in its constructor, and reads `WorkDirOption` off the `ParseResult`
  inside `GetWorkspace(ParseResult)` while services are still being composed — exactly the shape this
  plan wanted for `--session-id`/`--server-url`, now living in the one package every pipeline
  application already references rather than in throwaway sample code. Phase 6, below, updates its own
  wording to match.
- **`EtlPipelines.slnx`** has **four** top-level solution folders today, not three: `/src/`,
  `/extensions/` (new — every `EtlPipelines.Extensions.*` connector, `Cli` and the new `Json` connector
  included), `/tests/`, and `/samples/`. Only `/src/` and `/tests/` still carry their own
  `Directory.Build.props` as a `<File>` entry; `/extensions/` has none of its own (its projects sit
  physically under `src/` and inherit `src/Directory.Build.props`), and `/samples/` is solution-folder
  grouping only — its projects physically live under `src/` too now (`src/EtlPipelines.Samples.*`), not
  under a separate top-level `samples/` directory. `/server/` and `/agent/` (Phase 2 onward) follow that
  same solution-folder-only shape, not the original plan's separate top-level directories — see
  "Solution layout," below, which now says so explicitly rather than leaving it to be inferred from
  `/extensions/` and `/samples/`'s example.
- **`CliWrap 3.8.1`, previously unused, now has a real, tested consumer in this repo:**
  `EtlPipelines.Extensions.Cli`, whose `CliCommandStage` wraps `CliWrap.Cli.Wrap(...).ExecuteBufferedAsync()`
  to run an external command as a pipeline stage, with its own test coverage
  (`tests/EtlPipelines.Extensions.Cli.Tests`). Nothing here changes what Phase 1/5 planned to use
  CliWrap for, but there is now an in-repo, already-reviewed example of the exit-code/stderr handling
  shape to match, rather than a green field.
- **No gRPC, EF Core, ASP.NET Core Web SDK, Data Protection, or NuGet-client package exists anywhere in
  the repo today.** All new dependencies, and central-package-management entries need to be added for
  every one of them. *(Resolved: added across Phases 2–5, see "New central package versions.")*
- **Docker doesn't exist in this repo at all yet** — no `Dockerfile`, no `docker-compose.yml`. Built
  from nothing in Phase 7. *(Resolved: `docker/`, Phases 4–7.)*
- **The CI push-glob narrowing this section used to describe as a Phase-1 to-do is now an outstanding,
  live gap — samples are packable today and this has not been fixed.** `.github/workflows/ci.yml`'s
  `pack` job still runs `dotnet pack --configuration Release --output ./packages` unconditionally, and
  its `deploy` job (tag-triggered) still runs
  `dotnet nuget push "packages/*.nupkg" --api-key ... --source https://api.nuget.org/v3/index.json`
  with the same unnarrowed glob it always had. Now that `EtlPipelines.Samples.*` pack as real tool
  packages (previous bullet), the very next `v*` tag pushes six sample/demo packages to public
  nuget.org alongside the real connectors — silently, since `--skip-duplicate` swallows nothing here on
  a first push. A single glob can't fix this: every real package and every sample share the
  `EtlPipelines.` prefix (samples are `EtlPipelines.Samples.*`), so narrowing to
  `packages/EtlPipelines.*.nupkg` — this section's original suggestion — **would still push the
  samples**. The push step needs an actual exclusion (a shell loop skipping any `*.Samples.*.nupkg`, or
  the `pack` job routing sample output to a separate, never-pushed folder in the first place) rather
  than a same-prefix include glob. Small, self-contained, and worth landing on its own, independently
  of and before any part of this plan. *(Resolved in #5: the `deploy` job's push step is a shell loop
  that skips every `EtlPipelines.Samples.*` package.)*

## Phase 1 — Pipeline packaging: real `dotnet tool` packages, proven on the samples

> **Status: shipped** — the packing itself by an earlier, lighter-weight commit, the rest (CI
> exclusion, descriptions, the packaging test) in #5. Kept as originally written; see "As shipped,"
> at the end of this phase, for where the result differs from this plan.

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
needs no Docker, so it gets its own sequential step alongside the Docker-backed suites, rather than
slowing down every fast per-PR run. *(As shipped: the "Pipeline packaging" step of `ci.yml`'s
`databases` job — `integration-tests.yml` itself was later folded into `ci.yml`, gated to `main` and
release tags.)*

**Verification (run these by hand once the phase lands, in addition to the automated test above):**

```bash
dotnet pack src/EtlPipelines.Samples.ArchiveToDatabase --configuration Release --output ./packages
dotnet tool install --tool-path ./tool-install-test --add-source ./packages EtlPipelines.Samples.ArchiveToDatabase
./tool-install-test/etlpipelines.samples.archivetodatabase list
./tool-install-test/etlpipelines.samples.archivetodatabase run build-feed --work-dir ./scratch
dotnet tool uninstall --tool-path ./tool-install-test EtlPipelines.Samples.ArchiveToDatabase
```

(Project name and path corrected for the actual `src/EtlPipelines.Samples.ArchiveToDatabase` location
— see "As actually implemented" below; `dotnet tool install` conventionally lowercases the shim
filename — confirm the exact name empirically rather than trust the casing above; an automated test
should assert whatever that turns out to be rather than hardcode a guess.)

### As shipped

Every sample installs and runs as a real `dotnet` tool, and CI proves it. Against the plan above:

- **No `PipelinePackage.props`, no per-project `<Import>` line.** `src/Directory.Build.props`
  unconditionally imports `src/Samples.Build.props`, which applies `OutputType=Exe` and
  `PackAsTool=true` to any project whose name starts with `EtlPipelines.Samples.` — a naming-convention
  condition rather than the explicit per-project opt-in planned here. Safe in practice (every project
  under that prefix really is a runnable sample, and `Samples.Common`, the one library the plan
  singled out, no longer exists), and the same convention was then reused for `src/ServerAgent.Build.props`
  (see "Solution layout").
- **Every sample has a `<Description>`** (#5).
- **The NuGet push excludes samples with a loop, not a glob.** As the "Verified against the existing
  codebase" section explains, `packages/EtlPipelines.*.nupkg` would still have matched every sample, so
  `ci.yml`'s `deploy` job iterates over `packages/*.nupkg` and skips `EtlPipelines.Samples.*` (#5).
- **`tests/EtlPipelines.PipelinePackaging.Tests` exists** (`[Category("Packaging")]`): it packs
  `EtlPipelines.Samples.ArchiveToDatabase`, installs it with `dotnet tool install --tool-path`,
  asserts `list` prints `build-feed` and `archive`, and runs `build-feed`. The shim is found by
  listing the tool path rather than by a hard-coded name, as the verification note above recommended.
  It runs in `ci.yml`'s `databases` job ("Pipeline packaging" step), on `main` and release tags.

## Solution layout

**No new top-level directories for code.** Every `.cs` project — connector, sample, and now server/agent
— lives under `src/` or `tests/`, full stop; `server`/`agent` is a grouping `EtlPipelines.slnx` draws,
not a place on disk. This is not a new idea here: `/extensions/` and `/samples/` are already exactly
this shape today — solution folders with no `Directory.Build.props` or physical directory of their own,
grouping projects that physically sit under `src/` (`src/EtlPipelines.Extensions.*`,
`src/EtlPipelines.Samples.*`) — and `/server/`/`/agent/` just extend the same pattern rather than
reverting to the separate-top-level-folder shape this section originally proposed.

- **`EtlPipelines.slnx`** gains two more `<Folder>` blocks, `/server/` and `/agent/`, each listing
  `<Project>` entries the same way `/extensions/` and `/samples/` already do — no `<File>` entry for a
  `Directory.Build.props`, because there isn't one:
    - `src/EtlPipelines.Server/` — the gRPC host (`Microsoft.NET.Sdk.Web`, first project in the repo to
      use it), `OutputType=Exe`.
    - `src/EtlPipelines.Server.Database/` — EF Core layer, a library.
    - `src/EtlPipelines.Agent/` — generic host + `BackgroundService`, `OutputType=Exe`.
    - `src/EtlPipelines.Agent.GrpcClient/` — a library.
- **New file: `src/ServerAgent.Build.props`**, imported unconditionally from `src/Directory.Build.props`
  right alongside the existing `<Import Project="Samples.Build.props" />` — the same naming-convention
  mechanism Phase 1 already established for samples, applied to this prefix instead:
  ```xml
  <Project>
    <!--
      EtlPipelines.Server, .Server.Database, .Agent and .Agent.GrpcClient are internal deployables -
      Docker images, never a package anything outside this repo references - so none of them packs,
      unlike everything else under src/.
    -->
    <PropertyGroup Condition="$(MSBuildProjectName.StartsWith('EtlPipelines.Server')) OR $(MSBuildProjectName.StartsWith('EtlPipelines.Agent'))">
      <IsPackable>false</IsPackable>
      <GenerateDocumentationFile>false</GenerateDocumentationFile>
    </PropertyGroup>

    <!-- Only the two hosts are runnable; .Server.Database and .Agent.GrpcClient stay libraries. -->
    <PropertyGroup Condition="'$(MSBuildProjectName)' == 'EtlPipelines.Server' OR '$(MSBuildProjectName)' == 'EtlPipelines.Agent'">
      <OutputType>Exe</OutputType>
    </PropertyGroup>
  </Project>
  ```
- **Test projects follow the same rule**: `tests/EtlPipelines.Server.Tests/`,
  `tests/EtlPipelines.Server.Database.Tests/`, `tests/EtlPipelines.Agent.Tests/` and so on sit directly
  under `tests/` — no new folder needed there either, they append to the existing `<Folder Name="/tests/">`
  block in `EtlPipelines.slnx` alongside `EtlPipelines.Extensions.*.Tests` and `EtlPipelines.Samples.Tests`,
  inheriting the one `tests/Directory.Build.props` unchanged.
- **`src/EtlPipelines.GrpcClient/`** needs no new treatment at all — it already sits under `src/` and
  already gets `src/Directory.Build.props`'s ordinary connector-package defaults
  (`IsPackable=true`, `GenerateDocumentationFile=true`), which is exactly right: it's the one project of
  the five a third party's pipeline package actually depends on, the same way it depends on
  `EtlPipelines.Extensions.Sql` or `EtlPipelines.Hosting` today, and packs like any of them. Does it get
  `PackAsTool=true` the way a sample does? **No** — it's a library other pipeline processes reference,
  not itself a runnable pipeline, and it doesn't match either the `EtlPipelines.Samples.*` or
  `EtlPipelines.Server*`/`EtlPipelines.Agent*` naming conditions those props files key off, so nothing
  needs to specifically exclude it. A separate new project since this plan was first written, the
  `EtlPipelines` meta-package (`src/EtlPipelines/EtlPipelines.csproj`), references every connector via a
  single `EtlPipelines.Extensions.*` wildcard `ProjectReference` — `GrpcClient` doesn't match that glob
  either (it isn't a connector), so it stays outside the meta-package too, referenced only directly by
  whatever depends on it.

A new root-level `/docker/` folder (no `.csproj`, not a solution folder, and not code — the one
top-level addition this phase actually needs on disk) holds `docker-compose.yml`, `Dockerfile.server`,
`Dockerfile.agent`.

**As shipped**, one more project joined the four above: **`src/EtlPipelines.Protos`**, in the `/src/`
solution folder, compiles all three `.proto` files once (`GrpcServices="Both"`), and `Server`,
`GrpcClient` and `Agent.GrpcClient` reference it instead of each compiling their own copy. Added in
Phase 5, when the first test needed a real `Server` and a real `Agent` in one process and the two
independently generated sets of message types collided (CS0433). It packs like any other `src/`
library, since `EtlPipelines.GrpcClient` — a published package — depends on it. `docker/` also gained
`Dockerfile.seed`, `seed.sh` and `entrypoint.server.sh` (Phase 7), and `docs/` (Phase 9) is the
second non-code top-level folder.

## API versioning

Every proto starts versioned, `v1`, rather than being versioned only once a `v2` is first needed — the
one-way door here is the *absence* of a version, not its presence: a `PipelineExecutionService` with no
version segment cannot later grow a `v2` without either breaking every existing client's package/service
name or living with an asymmetric `PipelineExecutionService` next to a `PipelineExecutionServiceV2`.
Starting at `v1` costs nothing (Phase 2 is greenfield either way) and avoids ever having to make that
choice.

**Shape, for all three services alike:**

- **File path carries the version**: `protos/v1/pipeline_execution.proto`,
  `protos/v1/agent_execution.proto`, `protos/v1/management.proto`. A future `v2` of one of them is a
  sibling file under `protos/v2/`, not an edit to the `v1` one.
- **`package` carries the version**, last segment: `etlpipelines.pipeline_execution.v1`,
  `etlpipelines.agent_execution.v1`, `etlpipelines.management.v1`. This is what actually matters at
  runtime — a gRPC method's wire path is `/<package>.<Service>/<Method>`, so `v1` and a future `v2`
  package resolve to entirely distinct, non-colliding endpoints without either service needing to know
  the other exists.
- **`option csharp_namespace` is set explicitly** (`EtlPipelines.PipelineExecution.V1`, and so on) rather
  than left to `protoc`'s default derivation from `package` — the default would produce
  `Etlpipelines.PipelineExecution.V1` (lowercase `p`), which is legal but reads as a typo next to every
  other `EtlPipelines.*` namespace in this repo.

**What a `v2` looks like, when one is actually needed:** add `protos/v2/<name>.proto` with `package
etlpipelines.<name>.v2` and its own `csharp_namespace`; `Server` hosts both `V1.PipelineExecutionService`
and `V2.PipelineExecutionService` (ASP.NET Core gRPC services are just services — nothing about hosting
two versions of one simultaneously is special-cased or harder than hosting two unrelated ones); a method
`v2` obsoletes gets `option deprecated = true` in the `v1` file rather than being deleted, so an
old `EtlPipelines.GrpcClient` embedded in an already-installed, already-running pipeline package keeps
working un-reinstalled. Removing `v1` entirely is a deliberate, separate decision for whenever nothing
still calls it — not a step of adding `v2`.

### New central package versions

| Package | Consumer | Why |
|---|---|---|
| `Grpc.AspNetCore` | Server | Kestrel + service hosting |
| `Grpc.Net.ClientFactory`, `Grpc.Net.Client`, `Google.Protobuf`, `Grpc.Tools` | GrpcClient, Agent.GrpcClient | typed client codegen via `AddGrpcClient` |
| `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL` | Server.Database | Postgres query/mapping layer only. *(As shipped, `Microsoft.EntityFrameworkCore.Design` is referenced too — not for migrations, which still do not exist, but for `dotnet ef dbcontext scaffold`, which reverse-engineers the model from the schema dbdeploy deployed; see Phase 3's "As shipped.")* |
| `Microsoft.AspNetCore.DataProtection` | Server | secrets-at-rest, key ring on the `server-cache` volume |
| `NuGet.Protocol`, `NuGet.Versioning` | Server only | browsing/resolving packages against the one `bagetter` feed for `ListAvailablePackages`/`ListUpdates` — metadata only, and whatever `bagetter` itself proxies through is invisible to this beyond it showing up in the results. The Agent needs **no** NuGet-client library at all: per Phase 1, installing a package is just shelling out to `dotnet tool install --tool-path ... --add-source <feed>`, which already does download+extract+shim through the SDK itself. |

## Phase 2 — Scaffolding and the proto surface

Stand up the five server/agent projects (building clean under `TreatWarningsAsErrors=true`, no network
code yet) and the first-cut `.proto` contracts everything else codegens against.

**Three services, not one.** A single undifferentiated "GRPC client" was the original starting point,
but there are three genuinely different trust boundaries, and folding them into one service would mean
every caller sees every method — an arbitrary third-party pipeline process included, which is exactly
who should never see `AgentService.RegisterAgent` or `ManagementService.SetConfigurationEntry`:

1. **`protos/v1/pipeline_execution.proto`** → `PipelineExecutionService`, consumed by
   `EtlPipelines.GrpcClient` from *inside an arbitrary third-party pipeline process*. Narrowest surface
   — authenticated later by a single-use session id scoped to exactly one run, never a broader
   credential.
   ```protobuf
   syntax = "proto3";
   package etlpipelines.pipeline_execution.v1;
   option csharp_namespace = "EtlPipelines.PipelineExecution.V1";

   service PipelineExecutionService {
     rpc GetConfiguration(GetConfigurationRequest) returns (GetConfigurationResponse);
     rpc ReportStageResult(ReportStageResultRequest) returns (Ack);
     rpc ReportRunResult(ReportRunResultRequest) returns (Ack);
     rpc Heartbeat(HeartbeatRequest) returns (Ack);
   }
   message GetConfigurationResponse { map<string, string> entries = 1; } // flat IConfiguration keys, e.g. "ConnectionStrings:sales"
   ```
2. **`protos/v1/agent_execution.proto`** → `AgentService`, consumed by `EtlPipelines.Agent.GrpcClient`.
   Your own infrastructure, a separate process/machine. Server-streaming `Subscribe` so the agent
   doesn't poll:
   ```protobuf
   syntax = "proto3";
   package etlpipelines.agent_execution.v1;
   option csharp_namespace = "EtlPipelines.AgentExecution.V1";

   service AgentService {
     rpc RegisterAgent(RegisterAgentRequest) returns (RegisterAgentResponse);
     rpc Heartbeat(AgentHeartbeatRequest) returns (Ack);
     rpc Subscribe(SubscribeRequest) returns (stream WorkItem); // InstallPackage | ExecutePipeline
     rpc ReportInstallResult(ReportInstallResultRequest) returns (Ack);
     rpc ReportExecutionStatus(ReportExecutionStatusRequest) returns (Ack); // started/exited/resource sample
   }
   ```
3. **`protos/v1/management.proto`** → `ManagementService`, the end-user/API surface (no auth in phase 1):
   ```protobuf
   syntax = "proto3";
   package etlpipelines.management.v1;
   option csharp_namespace = "EtlPipelines.Management.V1";

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

See "API versioning," above, for why every one of these carries a `v1` from day one and what changes
the day a `v2` of any single one of them is actually needed.

**Tests:** one TUnit smoke test per new test project asserting the generated types exist and the
solution builds. **Verification:** `dotnet build --configuration Release` clean.

**As shipped (#6):** as planned — the protos above, with every message fleshed out and commented in
`protos/v1/`, and every RPC initially answering `UNIMPLEMENTED` through one shared helper
(`ServiceScaffolding`). Later phases replaced those stubs one by one; `ListAgents`,
`UninstallPackage` and `UpdatePackage` still return `UNIMPLEMENTED` today (see "Other gaps").

## Phase 3 — `EtlPipelines.Server.Database`

The EF Core layer against Postgres, proven in isolation before `Server` depends on it — and, alongside
it, the `dbdeploy` scripts that own the schema EF Core only ever reads and writes through, never
creates.

**No `dotnet ef migrations`, anywhere, ever.** `EtlPipelines.Server.Database`'s `DbContext` maps onto a
schema it does not control: no `Migrations` folder, no `ModelBuilder`-driven `dotnet ef database
update`, `IEntityTypeConfiguration<T>` classes hand-written to match tables `dbdeploy` already created.
Schema changes are a `.Deploy.sql`/`.Rollback.sql` pair (plus, where needed, a `.Data.sql` for priming
static/reference rows) under a new `db/` folder at the repo root — a non-project asset directory in
the same spirit as `docker/`, not a violation of "all code stays under `src/`/`tests/`" above,
since there's no `.csproj` here, the same way there isn't one under `docker/`. Sequenced via
`db/main.csv`, `dbdeploy`'s own branch-aware format, with one script folder per database
(`db/server/`, lowercase, matching the database key `db/dbsettings.json` itself uses). `dbdeploy`
itself is a `dotnet tool` (`dotnet tool install --global dbdeploy`, its own documented install
path), not a `PackageReference` — nothing under "New central package versions," above, changes for
it. CI installs it the same way, as a step ahead of whatever in this phase's workflow needs it.

The tables `db/server`'s initial deploy script creates, and `Server.Database`'s
`IEntityTypeConfiguration<T>` classes map onto:

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
NuGetFeeds              Id, Url, Ordinal   -- always exactly one row, the local bagetter URL; Ordinal
                        kept for a possible future multi-feed phase, unused while this table has one row
```

`ConfigurationEntries` is a flat key-value table mirroring `IConfiguration`'s own colon-path shape
deliberately — it's what lets `GetConfigurationResponse.entries` (Phase 2) feed straight into a
`ConfigurationProvider` with zero translation and zero knowledge of any connector's section shape
(Phase 6). `Pipelines` is a child of `PackageVersions`, not `Packages` directly, because
`EtlPipelines.Samples.ArchiveToDatabase` already proves one package can register more than one pipeline
(`build-feed` and `archive`) — `ListInstalledPipelines`/`ExecutePipeline` both operate at pipeline-name
granularity, matching how `PipelineRunner.Find(name)`
(`src/EtlPipelines.Hosting/PipelineRunner.cs`) already does a linear scan by name within one process.

**Tests:** fast TUnit tests against EF Core's SQLite/in-memory provider (`EnsureCreated()`, not
`dbdeploy` — a convenience for exercising mapping/query logic quickly, unrelated to how the real schema
gets created, and not a substitute for proving `dbdeploy`'s own scripts are correct) for mapping/query
logic; `[Category("Docker")]` + `Testcontainers.PostgreSql` (already centrally versioned) for the real
round-trip — this is the one that matters, because it's the only test that runs `dbdeploy deploy`
against a genuinely empty Postgres container before anything else touches it, proving the scripts and
the `DbContext`'s hand-written mapping actually agree — following `DatabaseFixture<TContainer>`
  (`tests/EtlPipelines.Extensions.Sql.Databases.Tests/DatabaseFixture.cs` — path corrected for the
  `EtlPipelines.Extensions.*` rename; the class itself is unchanged) and its
  `[ClassDataSource<T>(Shared = SharedType.PerAssembly)]` pairing exactly. `dbdeploy ci` — its own verb
  for proving every rollback actually rolls back — belongs in this phase's CI step too, not deferred.

**Verification:** `dbdeploy deploy` against a fresh local Postgres, `dbdeploy validate`, `dbdeploy ci`,
Docker-tagged tests green locally.

**As shipped (#7):**

- The schema is one baseline script, `db/server/_Init.sql`, with the tables exactly as listed above;
  later changes are to be `Deploy`/`Rollback` pairs appended to `db/main.csv`.
- **The EF Core model is scaffolded, not hand-written.** Instead of hand-maintained
  `IEntityTypeConfiguration<T>` classes, `ServerDbContext.cs` and `Entities/*.cs` are generated by
  `dotnet ef dbcontext scaffold` against a database dbdeploy has just deployed — a read-only,
  reverse-engineering use of the EF tooling that keeps "dbdeploy owns the schema" intact while
  removing any chance of the mapping drifting from it by hand. Customizations go in
  `ServerDbContext.Customizations.cs`. The exact command is in
  `src/EtlPipelines.Server.Database/README.md`. (`_Init.sql`'s own header comment still mentions
  `IEntityTypeConfiguration<T>`; it is left as is, since editing a deployed script is exactly what
  dbdeploy's model forbids.)
- Postgres is `15.1` everywhere — CI, Testcontainers fixtures, and later compose.
- `dbdeploy validate`/`deploy`/`ci` run in their own workflow, `db.yml`, against a Postgres service
  container; the Testcontainers round trip runs in `ci.yml`'s `databases` job ("Server.Database" step).

## Phase 4 — `EtlPipelines.Server`: catalog and management API (no agent yet)

`ManagementService.ListAvailablePackages`/`ListInstalledPipelines`/`ListUpdates`/
`SetConfigurationEntry` working end to end against `Server.Database` and the real `bagetter` instance,
for *metadata only*. `InstallPackage`/`ExecutePipeline` stubbed to fail with "no agents available" until
Phase 5.

**Key types:** `NuGetFeedClient` (wraps `NuGet.Protocol`'s search/find-package resources against the
single `bagetter` URL in the `NuGetFeeds` table — browse only, no download here; whatever `bagetter`
itself proxies upstream just shows up in the results, transparently), `PackageCatalogService`,
`SecretsStore` (wraps `IDataProtector` + `ConfigurationEntries`, `Protect`/`Unprotect` on
write/read — key ring via `PersistKeysToFileSystem` on the path that becomes the `server-cache` volume
in Phase 7).

**Docker Compose:** add `postgres` and `nuget` (bagetter) services with named volumes. `nuget`'s
compose definition also runs (or is seeded by) a one-shot step pushing Phase 1's packed samples to it,
so `docker compose up` produces a `bagetter` that already has something installable — see Phase 7's
"Bagetter seeding," which covers exactly this, including why upstream proxying stays off unless an
operator turns it on.

**Tests:** fast tests with a mocked `NuGetFeedClient`/`IDataProtector`; `[Category("Docker")]` tests
for the full `ManagementService` surface against a real Postgres container.

**Verification:** `docker compose up postgres nuget -d`, `grpcurl` against `ListAvailablePackages`
pointed at a test package pushed to bagetter.

**As shipped (#8):** as planned. `NuGetFeedSeeder`, a hosted service, upserts the one `NuGetFeeds` row
from `NuGetFeed:Url` on every start, so the feed is configuration rather than data an operator has to
insert. `ListUpdates` compares each package's most recently installed version against the latest
*stable* version on the feed. Bagetter seeding itself was deferred to Phase 7, as the plan allowed.

## Phase 5 — `EtlPipelines.Agent` / `EtlPipelines.Agent.GrpcClient`: registration and install

An agent that registers, holds `AgentService.Subscribe` open, receives an `InstallPackage` work item,
installs it, and reports pipeline names back — closing the install loop. Considerably simpler than it
would otherwise be, because Phase 1 already settled what a pipeline package *is*: installing one is
nothing but

```
dotnet tool install --tool-path <agent-cache>/<packageId>/<version> --add-source <feed-url(s)> <packageId> --version <version>
```

run via **`CliWrap`** — Phase 1's own packaging tests as planned above still don't exist (see Phase 1's
"As actually implemented"), but `CliWrap` now has a real, tested consumer in this repo regardless:
`EtlPipelines.Extensions.Cli`'s `CliCommandStage`, a shape worth following for exit-code and stderr
handling here too. No manual `.nupkg` download, no
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

**As shipped (#9):**

- **`--configfile`, not `--add-source`.** `dotnet tool install` refuses a plain-HTTP source unless a
  `NuGet.Config` marks it `allowInsecureConnections`, and — unlike `dotnet nuget push` — has no flag
  for it. `PackageInstaller` writes a per-install `NuGet.Config` (with `<clear />`, so only the
  dispatched feed is used) to a temp directory and passes it with `--configfile`.
- `InstallPackage` sends the work item to any connected agent (`AgentConnectionRegistry`, in memory)
  and waits up to five minutes for that agent's `ReportInstallResult`. Recording the result is
  idempotent per package version (a re-install reuses the row and never downgrades an installed
  version on failure).
- `EtlPipelines.Protos` was extracted in this phase — see "Solution layout."
- **`ResourceMonitor` exists but is not wired in.** It samples a process's CPU and working set as
  planned, and the Server stores `RESOURCE_SAMPLE` reports in `AgentResourceSamples`, but the agent
  never starts it, so nothing is sampled yet (see "Other gaps").
- The end-to-end `[Category("Docker")]` test runs `Server` and `Agent` in-process against real
  Postgres and bagetter containers, rather than as separate containers — the pattern every later
  end-to-end test follows.

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
  `EtlPipelines.Extensions.Sql`/`EtlPipelines.Extensions.Files.Sftp`.
- **Session id** — `--session-id`/`--server-url` added as recursive options via a new
  `EtlPipelinesHost.UseGrpcClient(...)` extension *(as shipped: no extension — see below)*, built exactly like `EtlPipelinesHost` already builds
  `VerboseOption`/`WorkDirOption` itself (`src/EtlPipelines.Hosting/EtlPipelinesHost.cs`): declared as
  `static Option<T>` properties, added to the root command as `Recursive = true` in the constructor,
  read off the `ParseResult` during `ConfigureServices`, before any provider exists to inject from. An
  even closer precedent than this plan originally had — `SampleWorkspace.UseSampleWorkspace`, the
  sample-only convenience this bullet used to cite, no longer exists; the pattern it demonstrated
  graduated into `EtlPipelinesHost` itself (see "Verified against the existing codebase," above), so
  `UseGrpcClient(...)` now extends the exact same class that already owns this mechanism, rather than
  imitating a pattern that lived in throwaway sample code. `Runs.Id` (the server-issued session id) is
  the primary key everywhere in the schema; `PipelineContext.RunId` — still internally generated,
  untouched — rides along as a secondary, diagnostic-only field on the first report call. `Core`'s
  public API gets no changes.

**Key types:** `GrpcConfigurationProvider`/`GrpcConfigurationSource`, `GrpcProgressReporter`,
`EtlPipelinesHost.UseGrpcClient(...)`, server-side `RunDispatcher` (turns `ExecutePipelineRequest` into
a `WorkItem` on an agent's `Subscribe` stream), `RunStatusStore`.

**Tests:** fast tests for `GrpcConfigurationProvider` against a mocked
`PipelineExecutionServiceClient`; fast tests for `GrpcProgressReporter` against a real
`ActivitySource`. **The full-stack `[Category("Docker")]` end-to-end test belongs here, not later** —
this is the first phase where catalog, agent, process launch, config pull, and progress reporting all
exist simultaneously; deferring it risks a wiring mistake between phases going uncaught. Take one of
Phase 1's already-packaged samples (`EtlPipelines.Samples.CsvToDatabase` — already proven against five
real database engines), add a `ProjectReference` to `EtlPipelines.GrpcClient` for this test build, push
to bagetter, install, `ExecutePipeline`, poll `StreamRunProgress`, assert `Runs`/`StageResults` match
what `tests/EtlPipelines.Samples.Tests` already expects for that sample.

**Verification:** the E2E test above; manual smoke test with `grpcurl` + `docker compose logs -f`.

**As shipped (#10, follow-up #11):**

- **Built in, not opted into.** There is no `UseGrpcClient(...)`: `EtlPipelinesHost` itself declares
  `SessionIdOption`/`ServerUrlOption` on every application, and `EtlPipelines.Hosting` references
  `EtlPipelines.GrpcClient` directly. The gRPC configuration source and `GrpcProgressReporter` are
  registered only when **both** options are present, so a plain local run is unchanged and no pipeline
  author has to remember a call for their package to work under an agent. The Phase 6 E2E test
  therefore needed no test-only `ProjectReference` either.
- `RunDispatcher` creates the `Runs` row directly as `Dispatched` (`Queued` is reserved and unused) and
  picks any connected agent. `GetConfiguration` returns every configuration entry — entries are not
  scoped per pipeline.
- `RunStatusStore` keeps each run's progress in memory and replays it to late `StreamRunProgress`
  subscribers; it is not persisted and not evicted.
- The agent reports `STARTED`/`EXITED` via `ReportExecutionStatus`; `EXITED` settles a run whose
  process died before calling `ReportRunResult` itself, and publishes the matching `RunCompleted`.
- **#11:** the URL handed to a launched process is `Server:PublicUrl` when set, otherwise Kestrel's
  real bound address resolved lazily after start — computing it at build time raced Kestrel's own
  binding and crashed agent-launched pipelines.
- `PipelineExecutionService.Heartbeat` validates the session and records nothing (see Phase 8's
  "As shipped" and "Other gaps").

## Phase 7 — Docker Compose hardening and image build

The compose file, finished: `Dockerfile.server`, `Dockerfile.agent`, all four volumes, health checks,
restart policies. Sequenced after Phase 6 deliberately — building images against a still-moving gRPC
surface means rebuilding every phase; doing it once here is cheaper.

```yaml
# the plan's sketch - see "As shipped" below for the real file
services:
  postgres: { image: postgres:18, volumes: [postgres-data:/var/lib/postgresql/data] }
  nuget:    { image: bagetter/bagetter, volumes: [nuget-data:/data] }
  server:   { build: ./Dockerfile.server, depends_on: [postgres, nuget], volumes: [server-cache:/var/lib/etlpipelines/server-cache] }
  agent:    { build: ./Dockerfile.agent, depends_on: [server], volumes: [agent-cache:/var/lib/etlpipelines/agent-cache] }
```

`server-cache` also holds the Data Protection key ring — it must be a durable volume, since losing it
makes every encrypted `ConfigurationEntries.EncryptedValue` unrecoverable.

**Schema deployment on startup.** A fresh `postgres-data` volume starts genuinely empty — `Server`
cannot serve a single request until `db`'s scripts have run against it. `Dockerfile.server`'s
entrypoint runs `dbdeploy deploy` (against the `postgres` service, once `depends_on`'s health check says
it's actually ready to accept connections) before starting `Server` itself, so `docker compose up` on a
brand-new volume takes itself from empty database to serving traffic with no separate manual step. An
already-deployed database — every run after the first — is a fast no-op for `dbdeploy deploy` to check,
not a repeated schema rebuild.

**Bagetter seeding.** `bagetter`'s image starts with nothing installed and no upstream configured — a
package feed with zero packages isn't a useful default for `docker compose up` to hand someone. A
`compose.yaml` step (a short-lived seed service, or a `command:` on `nuget` itself, decided when this
phase is actually built) runs `dotnet nuget push` for every `.nupkg` Phase 1's `pack` step already
produces for the six samples, once, against the freshly-started `nuget` service — so a first-run stack
comes up with exactly the samples installable and nothing else. Proxying `bagetter` through to nuget.org
(or another upstream) so it also serves real packages is `bagetter`'s own `Mirror`-style upstream
configuration, off by default here, an operator opts into by editing `bagetter`'s own config — not
something `docker-compose.yml`, `Server`, or this phase's seed step enables or is even aware of.

**Verification:** `docker compose up --build` against a genuinely fresh set of volumes, all four
containers healthy, `Server`'s schema present with no manual `dbdeploy` invocation from outside the
stack, `bagetter`'s own package listing shows the six seeded samples and nothing else, Phase 6's smoke
test re-run entirely against the built images.

**As shipped (#12):** `docker/docker-compose.yml`, `Dockerfile.server`, `Dockerfile.agent`,
`Dockerfile.seed`:

- **`postgres:15.1`**, not 18 — the version already pinned in CI and every Testcontainers fixture,
  rather than a fourth, untested one.
- **Health checks** on `postgres` (`pg_isready`) and `server` (`curl --http2-prior-knowledge` against
  its h2c endpoint); `server` waits for a healthy `postgres`, `agent` for a healthy `server`. `nuget`
  and `agent` have none (bagetter's image ships no tooling this repo controls to probe it with, so
  `seed` retries its pushes instead; the agent listens on no port). Every
  long-running service is `restart: unless-stopped`.
- **Schema on startup** exactly as planned: `entrypoint.server.sh` regenerates `db/dbsettings.json`
  for the compose network and runs `dbdeploy deploy` before starting the app.
- **The server image is runtime-only; the agent image keeps the full SDK**, since the agent runs
  `dotnet tool install` for packages nobody can know at build time.
- **Seeding is a separate one-shot `seed` service** (`restart: "no"`) that packs the six samples and
  pushes them to `nuget`, retrying until the feed answers. Upstream proxying stays off.
- **`docker.yml`** runs `docker compose up --build --wait` on fresh volumes plus the seed job on every
  relevant push and PR. It originally published `ghcr.io/gigi81/etl-pipelines/{server,agent}` on
  every push to `main`; it now publishes only for a `v*` tag (`<version>`, `<major>.<minor>`,
  `latest`, SHA) or a `release/**` branch (`release-<name>`, SHA), in both cases only after that
  compose check passed.
- The compose file publishes no host ports; `docs/articles/server/deployment.md` shows how to reach
  the Server from the host.

## Phase 8 — Reliability: heartbeat, crash recovery, cache eviction

The operational gaps nothing earlier in this plan addresses, made concrete now that the happy path is
proven:

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

**As shipped (#14):**

- **`AgentLivenessMonitor`**, a `BackgroundService` on a `TimeProvider`-driven timer
  (`AgentLiveness:PollInterval`, default 10 s), marks agents whose `LastHeartbeatAt` is older than
  `AgentLiveness:Timeout` (default 30 s, three missed 10-second heartbeats) `Offline`, and every
  `Queued`/`Dispatched`/`Running` run whose agent is that far behind `AgentLost`, publishing
  `RunCompleted { AGENT_LOST }`. Runs are joined against the agent's heartbeat directly, so a sweep
  that failed halfway is finished by the next one. Nothing is retried, as planned.
- **`CacheEvictor`** (agent) touches a `.last-used` marker whenever a version is installed or run, and
  every `Agent:CacheEvictionInterval` (30 min) deletes least-recently-used
  `<cache>/<package>/<version>` directories until the cache is under `Agent:CacheSizeCapBytes`
  (5 GiB), skipping anything it cannot delete.
- **`server-cache` is deliberately not swept.** By the time this phase was built it held only the Data
  Protection key ring — a few kilobytes that must never be evicted, since losing a key makes stored
  secrets unrecoverable. The plan's "both grow unboundedly" was true only of `agent-cache`.
- **Scoped to agent liveness.** A pipeline process that hangs while its agent stays healthy is not
  detected; per-run liveness via `PipelineExecutionService.Heartbeat` is left as a gap.
- The Docker test stops an in-process agent host mid-run (`IHostedService.StopAsync`, which tears down
  its heartbeat loop and `Subscribe` stream) rather than killing a container, consistent with every
  other end-to-end test here, and asserts `Offline` and `AgentLost` with a 2-second timeout.
- Found and fixed along the way: recording an install result was not idempotent (a re-install hit the
  `PackageVersions` unique index), and `BagetterFixture` built its container during test discovery,
  which broke the Docker category filter on machines without Docker.

## Phase 9 — Documentation

DocFX, a `docs/` folder, README updates, this file itself brought in line with what actually shipped
across every phase. Last, because documenting a still-moving target is wasted effort.

**As shipped:**

- **`docs/`** is a DocFX site: an introduction, the library guide (the README, included rather than
  copied), server articles — architecture, deployment, configuration, writing a pipeline package, the
  gRPC API, operations and reliability, known limitations — and contributor articles on building,
  testing and the database schema, plus an API reference generated from every published package's
  XML docs (Server/Agent projects, being deployables, are excluded).
- DocFX is a local tool (`.config/dotnet-tools.json`): `dotnet tool restore && dotnet docfx
  docs/docfx.json --serve`. **`docs.yml`** builds the site with `--warningsAsErrors` whenever docs or
  Markdown change, uploading nothing, and on a release tag (`v1.0.0`) also publishes it to GitHub
  Pages — only then, so the published docs always describe a released version.
- **README** gained a "Running pipelines on a server" section, the `EtlPipelines.GrpcClient` package,
  and links into `docs/`.
- **This file**: the status table at the top and an "As shipped" note per phase.

## Other gaps worth stating, not solving now

- **What "update" means** is worth pinning down explicitly: `UpdatePackage` always resolves to the
  latest stable version `bagetter` reports (`InstallPackage` already takes an explicit version for
  pinning). A `Run` already in flight against the old version is **not** interrupted — old tool installs
  are kept on disk until nothing references them.
- **Protocol versioning exists at the proto level (`v1`, see "API versioning," above) but nothing yet
  uses the room it leaves.** For phase 1 (everything built and deployed together) client and server are
  always the same version anyway, so this is dormant capability, not a gap being carried forward
  unaddressed — flagged for whichever later phase is the first to actually roll `Server` and an
  `Agent`/pipeline-process binary out independently, and needs `v1` to keep serving one while `v2` is
  introduced for the other.
- **Every agent installs its own copy of a package** — the install-delegation decision removes
  double-installation on the *install-validation* path (one agent does it once), but execution-time
  installation still has no cache shared across agents. Deferred; a shared cache volume or server-side
  proxying is a later phase's concern, not a blocker to a working system. *(As shipped this is
  sharper than stated: an agent never installs at execution time at all, so a run dispatched to an
  agent that lacks the package — or evicted it — fails with exit code `-1`.)*

Left open by the phases as shipped (operator-facing detail in
[`docs/articles/server/limitations.md`](docs/articles/server/limitations.md)):

- **Auth/authz, and TLS** — the deferred decision above still stands; nothing here is safe beyond a
  trusted network.
- **Configuration entries are global** — every run receives every entry; per-pipeline scoping needs
  a schema change.
- **Agent selection is arbitrary** — any connected agent; `Agents.Tags` is recorded but unused, and
  selection is unaware of which agent has which package installed.
- **One work item at a time per agent** — `AgentRegistration` handles its `Subscribe` stream
  sequentially, so a long run delays installs behind it (which time out after five minutes).
- **Agents do not reconnect in-process** — a dropped `Subscribe` stream ends the agent process; the
  container restart policy brings it back as a new registration.
- **In-memory Server state** — connections, pending installs and `RunStatusStore` history are lost on
  restart, and the history is never evicted while the process runs.
- **No per-run liveness** — `PipelineExecutionService.Heartbeat` records nothing, so a hung process on
  a healthy agent stays `Running`.
- **`ResourceMonitor` is not wired into `ReportExecutionStatus`** — `AgentResourceSamples` stays empty.
- **`ListAgents`, `UninstallPackage`, `UpdatePackage` are unimplemented** — still `UNIMPLEMENTED`
  stubs. `UpdatePackage`'s intended semantics are in the first bullet of this section.
- **Cache eviction does not tell the Server** — and nothing reinstalls an evicted version on demand.

## Verification (whole subsystem)

1. `dotnet build --configuration Release` clean across all new projects, `TreatWarningsAsErrors=true`.
2. Fast TUnit suite (`--treenode-filter "/**[(Category!=Docker)&(Category!=Packaging)]"`) green —
   `ci.yml`'s Test job.
3. Packaging suite (`[Category("Packaging")]`) and Docker-tagged suite green locally with Docker
   running — `ci.yml`'s `databases` job, on `main` and release tags.
4. `docker compose -f docker/docker-compose.yml up --build`: all four containers healthy —
   `docker.yml`.
5. End-to-end manual smoke test: install a real sample package via `grpcurl`, execute it, watch
   `StreamRunProgress`, confirm `Runs`/`StageResults` in Postgres match the sample's own expected
   `PipelineResult` (cross-checked against `tests/EtlPipelines.Samples.Tests`).