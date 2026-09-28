# API reference

Generated from the XML documentation comments of every package published to NuGet:

| Package | Namespaces |
|---|---|
| `EtlPipelines.Abstractions` | `EtlPipelines.Abstractions.*`: the contracts (ports, execution context and results, configuration) |
| `EtlPipelines.Core` | `EtlPipelines.Core.*`: the runtime, the builder, the built-in transforms and `EtlDiagnostics` |
| `EtlPipelines.Hosting` | `EtlPipelines.Hosting.*`: `EtlPipelinesHost`, `PipelineRunner` and the built-in `list`/`run` verbs |
| `EtlPipelines.GrpcClient` | `EtlPipelines.GrpcClient`: configuration pull and progress reporting for a pipeline an agent launched |
| `EtlPipelines.Extensions.*` | one namespace tree per connector |

`AddEtlPipeline` lives in `EtlPipelines.Core`. Each connector's builder shorthands (`FromCsv`,
`ToJsonLines` and the rest) live in a static `Extensions*` class in the root `EtlPipelines` namespace, so a
single `using EtlPipelines;` brings every installed connector's shorthands into scope.

The Server, the Agent and their database and gRPC-client libraries are deployables, not packages that
other code references, so this reference leaves them out. The
[server articles](../articles/server/architecture.md) cover them instead. The gRPC contracts are
documented from their `.proto` sources in the [gRPC API](../articles/server/grpc-api.md) article.
