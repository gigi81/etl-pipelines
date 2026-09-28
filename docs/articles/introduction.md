# Introduction

EtlPipelines is a set of NuGet packages for writing extract-transform-load jobs in .NET. A pipeline is
declared once as a chain of typed stages:

```csharp
services.AddEtlPipeline("orders", builder => builder
    .From<DownloadOrders, OrderRow>()
    .Where(o => o.Amount > 0)
    .Through<NormalizeOrders, OrderDto>()
    .To<SqlSink>());
```

At run time the stages overlap. Rows stream from the source through every transform into the sink
while the source is still reading, instead of each step finishing before the next one starts.

## Two ways to run a pipeline

### In-process, as a library

Reference the packages, register pipelines with the .NET generic host, and run them yourself, or let
`EtlPipelines.Hosting` turn your application into a command line tool with `list` and `run` verbs.
Configuration, credentials and scheduling are up to the application. The
[library guide](library-guide.md) covers this, together with every connector.

### Distributed, through the Server and its Agents

The repository also contains a small orchestration system for running pipelines on machines other
than the one they were written on:

- A pipeline application is packed as a **`dotnet tool`** and pushed to the bundled NuGet feed
  (bagetter).
- The **Server** keeps the catalog of installed packages and their pipelines. It stores configuration
  entries (connection strings, SFTP credentials) encrypted at rest, records every run and each of its
  stages, and exposes all of this over gRPC.
- One or more **Agents** connect to the Server. They install packages when asked and launch pipeline
  runs as separate processes. Each launched process pulls its configuration from the Server and
  reports its progress back to it.

The whole stack (Postgres, bagetter, Server, Agent) comes up with one `docker compose up`. Start with
the [architecture overview](server/architecture.md).

> [!WARNING]
> The Server has **no authentication or authorization** yet. Run it only on a trusted network. See
> [Known limitations](server/limitations.md).
