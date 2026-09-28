# Building and testing

## Prerequisites

- The .NET SDK pinned in `global.json` (.NET 10).
- Docker, for anything tagged `[Category("Docker")]` and for the compose stack.
- `dbdeploy` (`dotnet tool install --global dbdeploy`), for the database suites and for deploying
  the Server's schema by hand.
- PowerShell (`pwsh`) on the `PATH`, for `EtlPipelines.Extensions.Cli.Tests`.
- On macOS, native `protoc` and `grpc_csharp_plugin` binaries (see below).

```bash
dotnet build --configuration Release
```

Warnings are errors throughout (`TreatWarningsAsErrors`), and every packable project must have XML
documentation for its public API.

### macOS: native protobuf tools

`EtlPipelines.Protos` compiles the `.proto` files with `Grpc.Tools`, which only bundles x86-64
builds of `protoc` and `grpc_csharp_plugin` for macOS. On Apple Silicon those fail with
`Bad CPU type in executable`. Install native builds with Homebrew and point `Grpc.Tools` at them:

```bash
brew install protobuf grpc

# add to ~/.zshrc so every shell (and your IDE, if launched from one) picks them up
export PROTOBUF_PROTOC="$(brew --prefix)/bin/protoc"
export GRPC_PROTOC_PLUGIN="$(brew --prefix)/bin/grpc_csharp_plugin"
```

Both variables are needed: the bundled plugin is x86-64 only too. The Homebrew versions will not
match the `Grpc.Tools` version pinned in `Directory.Packages.props`, which only affects the code
generated on your machine. CI builds with the pinned version.

## Tests

Tests use [TUnit](https://tunit.dev) on Microsoft.Testing.Platform, AwesomeAssertions for
assertions, Moq for mocks, and Testcontainers for real databases and servers.

### Categories

| Category | Needs | Runs in CI |
|---|---|---|
| *(none)* and `Server`, `Agent`, `GrpcClient`, `Samples` | Nothing: SQLite in memory, mocks, a real in-process host | The **Test** job, on every push and PR (Ubuntu) and on `main` and tags (Windows and macOS too) |
| `Docker` | A Docker engine | The **Databases** job, on `main` and release tags only |
| `Packaging` | Nothing, but slow: a real `dotnet pack` and `dotnet tool install` round trip | The **Databases** job, on `main` and release tags only |

Run the fast suite, exactly as CI does:

```bash
dotnet test --configuration Release \
  --treenode-filter "/**[(Category!=Docker)&(Category!=Packaging)]"
```

Run one project, Docker tests included:

```bash
dotnet test tests/EtlPipelines.Server.Tests/EtlPipelines.Server.Tests.csproj --configuration Release
```

> [!TIP]
> Before you change a filter, list what it matches by running a built test project's executable
> directly: `<test project>/bin/Release/net10.0/<assembly> --list-tests --treenode-filter "..."`.
> A filter that matches nothing does not
> fail, because CI tolerates "zero tests ran" (exit code 8) for the projects that have only Docker
> tests.

### Writing a test that needs Docker

Put `[Category("Docker")]` on the class, and make sure the class's fixtures do not touch Docker until
`InitializeAsync`. TUnit builds `ClassDataSource` fixtures during test **discovery**. A fixture that
creates its container in its constructor (Testcontainers' `Build()` included) fails discovery on a
machine without Docker, and a test whose discovery fails loses its category, so the category filter
no longer excludes it. Create containers lazily, as `BagetterFixture` does with `Lazy<IContainer>`.

Tests that share a per-assembly container and write the same rows, or push the same package, must be
made idempotent (e.g. `dotnet nuget push --skip-duplicate`) or serialised with `[NotInParallel(...)]`.

The end-to-end suites in `tests/EtlPipelines.Server.Tests/EndToEnd/` run the Server and Agent
in-process against real Postgres and bagetter containers: install, execute, and agent-liveness.

## CI workflows

| Workflow | Trigger | What it proves |
|---|---|---|
| `ci.yml` | Pushes and PRs, except changes only to `docker/**` or Markdown | Build and the fast tests (Ubuntu on PRs; all three OSes on `main` and tags), Docker and packaging suites (`main` and tags), a single Codecov upload, `dotnet pack`, and on a `v*` tag the NuGet push (sample packages excluded) and a GitHub release. |
| `db.yml` | Pushes and PRs, except changes only to `docker/**` or Markdown | `dbdeploy validate`, `deploy`, and `ci` (which proves every rollback) against a Postgres service container. |
| `docker.yml` | Pushes and PRs, except changes only to Markdown | `docker compose up --build --wait` on fresh volumes, then the seed job. On `main`, also publishes the Server and Agent images to GHCR. |
| `docs.yml` | Changes to `docs/**`, Markdown, or the tool manifest | Builds this documentation site with warnings as errors. |

Superseded runs on the same branch are cancelled. Coverage and test-result artifacts are kept for one
day, and NuGet package artifacts for three.

## Building these docs

The site is built with [DocFX](https://dotnet.github.io/docfx/), pinned as a local tool in
`.config/dotnet-tools.json`:

```bash
dotnet tool restore
dotnet docfx docs/docfx.json --serve   # builds, then serves on http://localhost:8080
```

The API reference is generated from the XML documentation of the projects listed in `docfx.json`.
`docs/api/*.yml` and `docs/_site/` are build output and are not checked in.
