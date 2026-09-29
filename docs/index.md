---
_layout: landing
---

# EtlPipelines

A streaming ETL pipeline abstraction for .NET, built so that extract, transform and load actually
overlap rather than run one after another.

There are two ways to use it:

- **As a library.** Reference `EtlPipelines` (or `EtlPipelines.Core` plus the connectors you need),
  register pipelines against the .NET generic host, and run them in-process. The
  [library guide](articles/library-guide.md) covers the builder, the connectors and hosting a
  pipeline as a command line application.
- **As a distributed system.** Package a pipeline application as a `dotnet tool`, publish it to the
  bundled NuGet feed, and let a central **Server** install it on and dispatch runs to one or more
  **Agents**, with connection strings and credentials served from an encrypted store rather than
  shipped with the package. Start with the [architecture overview](articles/server/architecture.md),
  then [run the stack with Docker Compose](articles/server/deployment.md).

The [API reference](api/index.md) is generated from the XML documentation of every published
package.
